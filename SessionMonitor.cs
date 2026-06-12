using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ClaudeWatch;

internal sealed class SessionMonitor : IDisposable
{
    private const int NeedsAttentionMinutes = 5;

    private readonly string _sessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "sessions");

    private readonly Dictionary<string, string> _lastRawStatus = new();
    private readonly Dictionary<string, DateTime> _idleSince = new();

    public event Action<IReadOnlyList<ClaudeSession>>? SessionsChanged;
    public event Action<ClaudeSession>? NeedsAttention;

    public IReadOnlyList<ClaudeSession> Scan()
    {
        if (!Directory.Exists(_sessionsDir))
            return [];

        var sessions = new List<ClaudeSession>();
        var now = DateTime.Now;

        string[] files;
        try { files = Directory.GetFiles(_sessionsDir, "*.json"); }
        catch { return []; }

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
        }

        SessionsChanged?.Invoke(sessions);
        return sessions;
    }

    private ClaudeSession? ReadSession(string filePath, DateTime now)
    {
        try
        {
            string json;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();

            var node = JsonNode.Parse(json);
            if (node == null) return null;

            var pid = node["pid"]?.GetValue<long>().ToString() ?? Path.GetFileNameWithoutExtension(filePath);
            var sessionId = node["sessionId"]?.GetValue<string>() ?? "";
            var rawStatus = node["status"]?.GetValue<string>() ?? "idle";
            var cwd = node["cwd"]?.GetValue<string>() ?? "";
            var updatedAtMs = node["updatedAt"]?.GetValue<long>() ?? 0;

            var updatedAt = updatedAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs).LocalDateTime
                : now;

            if (!IsProcessRunning(pid))
                return null;

            var prevRaw = _lastRawStatus.TryGetValue(pid, out var p) ? p : null;
            if (rawStatus == "idle" && prevRaw == "busy")
                _idleSince[pid] = now;
            _lastRawStatus[pid] = rawStatus;

            SessionStatus status;
            if (rawStatus == "busy")
            {
                status = SessionStatus.Running;
                _idleSince.Remove(pid);
            }
            else if (_idleSince.TryGetValue(pid, out var idleAt) &&
                     (now - idleAt).TotalMinutes < NeedsAttentionMinutes)
            {
                status = SessionStatus.NeedsAttention;
            }
            else
            {
                status = SessionStatus.Idle;
            }

            var projectName = string.IsNullOrEmpty(cwd)
                ? sessionId[..Math.Min(8, sessionId.Length)]
                : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var session = new ClaudeSession(pid, sessionId, status, cwd, projectName, updatedAt);

            if (status == SessionStatus.NeedsAttention && prevRaw == "busy")
                NeedsAttention?.Invoke(session);

            return session;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessRunning(string pid)
    {
        if (!int.TryParse(pid, out var id)) return false;
        try { return !Process.GetProcessById(id).HasExited; }
        catch { return false; }
    }

    public void Dispose() { }
}
