using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ClaudeWatch;

internal sealed class SessionMonitor : IDisposable
{
    private const int NeedsAttentionMinutes = 5;
    private const int DebounceMs = 150;

    private readonly string _sessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "sessions"
    );

    private readonly Dictionary<string, string> _lastRawStatus = new();
    private readonly Dictionary<string, DateTime> _idleSince = new();
    private readonly HashSet<string> _awaitingInputPids = new();
    // PIDs that had at least one running sub-agent on the previous scan, so we can detect the
    // moment they all finish and treat it like a busy->idle completion.
    private readonly HashSet<string> _hadRunningSubs = new();

    private readonly SubAgentReader _subAgents = new();
    private readonly TranscriptReader _transcripts = new();

    // PIDs we have an exit subscription for, keyed by the same string PID used everywhere else.
    private readonly Dictionary<string, Process> _trackedProcesses = new();

    private FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounceTimer;
    private bool _disposed;

    public event Action<IReadOnlyList<ClaudeSession>>? SessionsChanged;
    public event Action<ClaudeSession>? NeedsAttention;
    public event Action<ClaudeSession>? AwaitingInput;

    /// <summary>
    /// Raised (on a thread-pool thread) whenever something happened that warrants a re-scan:
    /// a session file changed, a tracked process exited, or the watcher dropped events.
    /// The owner is responsible for marshaling <see cref="Scan"/> onto the UI thread.
    /// </summary>
    public event Action? ChangeDetected;

    /// <summary>
    /// The earliest instant at which a session currently in <see cref="SessionStatus.NeedsAttention"/>
    /// will lapse back to <see cref="SessionStatus.Idle"/>. Null when no session is in that window.
    /// Recomputed at the end of every <see cref="Scan"/>.
    /// </summary>
    public DateTime? NextNeedsAttentionDeadline { get; private set; }

    public SessionMonitor()
    {
        _debounceTimer = new System.Threading.Timer(_ => ChangeDetected?.Invoke());
        EnsureWatcher();
    }

    public IReadOnlyList<ClaudeSession> Scan()
    {
        // The sessions directory may be created after we start; (re)attach the watcher lazily.
        EnsureWatcher();

        if (!Directory.Exists(_sessionsDir))
        {
            NextNeedsAttentionDeadline = null;
            SyncProcessSubscriptions(new HashSet<string>());
            SessionsChanged?.Invoke([]);
            return [];
        }

        var sessions = new List<ClaudeSession>();
        var now = DateTime.Now;

        string[] files;
        try
        {
            files = Directory.GetFiles(_sessionsDir, "*.json");
        }
        catch
        {
            return [];
        }

        foreach (var file in files)
        {
            var session = ReadSession(file, now);
            if (session != null)
                sessions.Add(session);
        }

        var activePids = sessions.Select(s => s.Pid).ToHashSet();
        foreach (var key in _lastRawStatus.Keys.Where(k => !activePids.Contains(k)).ToList())
        {
            _lastRawStatus.Remove(key);
            _idleSince.Remove(key);
            _awaitingInputPids.Remove(key);
            _hadRunningSubs.Remove(key);
        }

        SyncProcessSubscriptions(activePids);
        NextNeedsAttentionDeadline = ComputeNextDeadline(sessions);

        SessionsChanged?.Invoke(sessions);
        return sessions;
    }

    private DateTime? ComputeNextDeadline(IReadOnlyList<ClaudeSession> sessions)
    {
        DateTime? earliest = null;
        foreach (var session in sessions)
        {
            if (session.Status != SessionStatus.NeedsAttention)
                continue;
            if (!_idleSince.TryGetValue(session.Pid, out var idleAt))
                continue;

            var deadline = idleAt.AddMinutes(NeedsAttentionMinutes);
            if (earliest == null || deadline < earliest)
                earliest = deadline;
        }
        return earliest;
    }

    private ClaudeSession? ReadSession(string filePath, DateTime now)
    {
        try
        {
            string json;
            using (
                var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                )
            )
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();

            var node = JsonNode.Parse(json);
            if (node == null)
                return null;

            var pid =
                node["pid"]?.GetValue<long>().ToString()
                ?? Path.GetFileNameWithoutExtension(filePath);
            var sessionId = node["sessionId"]?.GetValue<string>() ?? "";
            var rawStatus = node["status"]?.GetValue<string>() ?? "idle";
            var waitingFor = node["waitingFor"]?.GetValue<string>();
            var cwd = node["cwd"]?.GetValue<string>() ?? "";
            var updatedAtMs = node["updatedAt"]?.GetValue<long>() ?? 0;

            var updatedAt =
                updatedAtMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs).LocalDateTime
                    : now;

            if (!IsProcessRunning(pid))
                return null;

            var prevRaw = _lastRawStatus.TryGetValue(pid, out var p) ? p : null;
            if (rawStatus == "idle" && prevRaw == "busy")
                _idleSince[pid] = now;
            _lastRawStatus[pid] = rawStatus;

            SessionStatus status;
            // Claude Code reports a dedicated "waiting" status (with a "waitingFor" hint such as
            // "permission prompt") while it is blocked on user input. Some flows may also surface
            // a non-empty waitingFor without flipping the status, so treat either as awaiting input.
            bool awaitingInput =
                rawStatus == "waiting" || !string.IsNullOrWhiteSpace(waitingFor);
            if (awaitingInput)
            {
                _idleSince.Remove(pid);
                status = SessionStatus.AwaitingInput;
            }
            else if (rawStatus == "busy")
            {
                _idleSince.Remove(pid);
                status = SessionStatus.Running;
                _awaitingInputPids.Remove(pid);
            }
            else
            {
                _awaitingInputPids.Remove(pid);
                if (
                    _idleSince.TryGetValue(pid, out var idleAt)
                    && (now - idleAt).TotalMinutes < NeedsAttentionMinutes
                )
                    status = SessionStatus.NeedsAttention;
                else
                    status = SessionStatus.Idle;
            }

            // Sub-agents (Task tool) run inside this session's process and have no session file
            // of their own; surface them from the transcript and roll their activity up.
            var subAgents = _subAgents.GetRunning(sessionId, cwd);
            bool hasRunningSubs = subAgents.Count > 0;
            bool hadRunningSubs = _hadRunningSubs.Contains(pid);
            bool subsJustFinished = hadRunningSubs && !hasRunningSubs;

            if (hasRunningSubs)
            {
                _hadRunningSubs.Add(pid);
                // A live sub-agent means the session is working even when Claude Code reports the
                // parent as idle (the parent loop is simply blocked waiting on the child).
                if (status is SessionStatus.Idle or SessionStatus.NeedsAttention)
                {
                    status = SessionStatus.Running;
                    _idleSince.Remove(pid);
                }
            }
            else
            {
                _hadRunningSubs.Remove(pid);
                // Sub-agents finished and the parent picked nothing else up: surface it like any
                // other busy->idle completion so the "done" alert still fires.
                if (subsJustFinished && status == SessionStatus.Idle)
                {
                    _idleSince[pid] = now;
                    status = SessionStatus.NeedsAttention;
                }
            }

            var projectName = string.IsNullOrEmpty(cwd)
                ? sessionId[..Math.Min(8, sessionId.Length)]
                : Path.GetFileName(
                    cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                );

            var mode = ReadPermissionMode(Path.Combine(_sessionsDir, $"{sessionId}.mode"));

            // Live activity: only worth reading the transcript tail while the session is working.
            var activity = status == SessionStatus.Running
                ? _transcripts.GetActivity(sessionId, cwd)
                : null;

            var session = new ClaudeSession(
                pid,
                sessionId,
                status,
                cwd,
                projectName,
                updatedAt,
                mode,
                subAgents,
                activity
            );

            if (status == SessionStatus.NeedsAttention && (prevRaw == "busy" || subsJustFinished))
                NeedsAttention?.Invoke(session);

            if (status == SessionStatus.AwaitingInput && _awaitingInputPids.Add(pid))
                AwaitingInput?.Invoke(session);

            return session;
        }
        catch
        {
            return null;
        }
    }

    private static PermissionMode ReadPermissionMode(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return PermissionMode.Normal;
            }
            var text = File.ReadAllText(path).Trim().ToLowerInvariant();
            return text switch
            {
                "acceptedits" or "accept_edits" or "accept-edits" => PermissionMode.AcceptEdits,
                "plan" => PermissionMode.Plan,
                "auto" => PermissionMode.Auto,
                "bypass"
                or "bypassall"
                or "bypass_all"
                or "bypasspermissions"
                or "bypass_permissions" => PermissionMode.Bypass,
                _ => PermissionMode.Normal,
            };
        }
        catch
        {
            return PermissionMode.Normal;
        }
    }

    private static bool IsProcessRunning(string pid)
    {
        if (!int.TryParse(pid, out var id))
            return false;
        try
        {
            return !Process.GetProcessById(id).HasExited;
        }
        catch
        {
            return false;
        }
    }

    // ----- Event-driven trigger plumbing -------------------------------------------------

    private void EnsureWatcher()
    {
        if (_watcher != null || _disposed)
            return;
        if (!Directory.Exists(_sessionsDir))
            return;

        try
        {
            var watcher = new FileSystemWatcher(_sessionsDir)
            {
                // Watch every file in the directory: *.json session files and their sibling
                // *.mode files. Re-scanning is cheap and idempotent, so a slightly broad
                // trigger is harmless and simpler than running two watchers.
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
        }
        catch
        {
            // If the watcher can't be created the reconciliation poll still keeps state fresh.
            _watcher = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => RequestScanDebounced();

    private void RequestScanDebounced()
    {
        if (_disposed)
            return;
        // (Re)arm the debounce: a single logical write often fires several events in a burst.
        _debounceTimer.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The watcher buffer overflowed (or the dir went away). Tear it down so the next Scan
        // re-attaches a fresh one, and force an immediate reconciliation scan.
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileEvent;
                _watcher.Changed -= OnFileEvent;
                _watcher.Deleted -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
        }
        catch { }
        _watcher = null;

        ChangeDetected?.Invoke();
    }

    private void SyncProcessSubscriptions(HashSet<string> activePids)
    {
        // Drop subscriptions for PIDs that are no longer active.
        foreach (var pid in _trackedProcesses.Keys.Where(k => !activePids.Contains(k)).ToList())
        {
            if (_trackedProcesses.Remove(pid, out var proc))
            {
                try { proc.Exited -= OnTrackedProcessExited; } catch { }
                proc.Dispose();
            }
        }

        // Add subscriptions for newly-seen PIDs so an unclean exit (which leaves a stale
        // session file and fires no filesystem event) still triggers a re-scan.
        foreach (var pid in activePids)
        {
            if (_trackedProcesses.ContainsKey(pid))
                continue;
            if (!int.TryParse(pid, out var id))
                continue;
            try
            {
                var proc = Process.GetProcessById(id);
                proc.EnableRaisingEvents = true;
                proc.Exited += OnTrackedProcessExited;
                if (proc.HasExited)
                {
                    // Exited between scan and subscribe; reconciliation/next scan will clean up.
                    proc.Exited -= OnTrackedProcessExited;
                    proc.Dispose();
                    continue;
                }
                _trackedProcesses[pid] = proc;
            }
            catch
            {
                // Process gone or inaccessible; the reconciliation poll covers it.
            }
        }
    }

    private void OnTrackedProcessExited(object? sender, EventArgs e) => ChangeDetected?.Invoke();

    public void Acknowledge(string pid) => _idleSince.Remove(pid);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _debounceTimer.Dispose();

        if (_watcher != null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileEvent;
                _watcher.Changed -= OnFileEvent;
                _watcher.Deleted -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
        }

        foreach (var proc in _trackedProcesses.Values)
        {
            try { proc.Exited -= OnTrackedProcessExited; } catch { }
            proc.Dispose();
        }
        _trackedProcesses.Clear();
    }
}
