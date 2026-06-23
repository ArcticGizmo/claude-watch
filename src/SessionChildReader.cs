using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeWatch;

/// <summary>
/// Discovers the children running under a session — both sub-agents (Agent/Task tool) and
/// background shells/"tasks" (Bash/PowerShell launched with <c>run_in_background: true</c>).
/// Neither gets its own <c>~/.claude/sessions/*.json</c> file; both run inside the parent's
/// process and the only live record is the parent transcript at
/// <c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>. Both are surfaced from a single
/// full-file scan so a busy session is parsed once per poll, not twice.
///
/// <para><b>Sub-agents.</b> Each appears as an assistant <c>tool_use</c> named "Agent" (or "Task"
/// in older builds). It is still running while that tool_use has no matching <c>tool_result</c> —
/// a signal that holds even when the sub-agent sits in a long, silent shell command.</para>
///
/// <para><b>Background shells.</b> A <c>tool_use</c> named "Bash"/"PowerShell" with
/// <c>run_in_background: true</c>; its immediate <c>tool_result</c> reads
/// "Command running in background with ID: &lt;id&gt;", which is the shell id we key on. Unlike a
/// sub-agent the shell does not block the parent, so the spawn-result match cannot tell us it is
/// done. Completion is instead reported by Claude Code as a <c>&lt;task-notification&gt;</c> record
/// (top-level type "queue-operation"/"attachment", never "assistant") carrying the shell's
/// <c>&lt;task-id&gt;</c> and a terminal <c>&lt;status&gt;</c>, written to the transcript even while
/// the session is idle. A shell is running until such a notification names it. (assistant-type lines
/// are skipped for this so the model merely quoting a notification can't retire a live shell.)</para>
///
/// Results are cached per transcript by (length, last-write) so the common case — a scan while a
/// child runs, during which the parent transcript does not change — costs a stat, not a parse.
/// </summary>
internal sealed class SessionChildReader
{
    private readonly string _projectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects"
    );

    private readonly Dictionary<string, CacheEntry> _cache = new();
    // Tracks when each shell ID was first returned as "running" by this instance.
    // Used to retire orphaned shells whose output file was never created.
    private readonly Dictionary<string, DateTime> _shellFirstSeen = new();

    private static readonly Regex TaskIdRegex = new(@"<task-id>([A-Za-z0-9]+)</task-id>", RegexOptions.Compiled);
    private static readonly Regex TerminalStatusRegex = new(@"<status>(completed|failed|killed)</status>", RegexOptions.Compiled);
    // The shell id Claude Code reports back when a command is launched in the background.
    private static readonly Regex BackgroundIdRegex = new(@"running in background with ID:\s*([A-Za-z0-9]+)", RegexOptions.Compiled);
    // The result text written when a background shell is stopped/killed (no task-notification fires
    // for a kill, so this — and the kill tool_use itself — are how we learn a shell ended early).
    private static readonly Regex StoppedTaskRegex = new(@"stopped task:\s*([A-Za-z0-9]+)", RegexOptions.Compiled);

    private readonly record struct CacheEntry(long Length, DateTime WriteUtc, SessionChildren Result);

    /// <summary>
    /// Returns the children currently running under the given session, or empty lists if the
    /// transcript can't be located or read. Only direct children of the session are reported.
    /// </summary>
    public SessionChildren GetRunning(string sessionId, string cwd, bool sessionBusy = true)
    {
        if (string.IsNullOrEmpty(sessionId))
            return SessionChildren.Empty;

        var path = ResolveTranscript(sessionId, cwd);
        if (path == null)
            return SessionChildren.Empty;

        try
        {
            var fi = new FileInfo(path);
            SessionChildren result;
            if (
                _cache.TryGetValue(path, out var cached)
                && cached.Length == fi.Length
                && cached.WriteUtc == fi.LastWriteTimeUtc
            )
                result = cached.Result;
            else
            {
                result = Parse(path);
                _cache[path] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc, result);
            }

            result = ApplyFirstSeenCleanup(result, TasksDir(path), sessionBusy);
            return result;
        }
        catch
        {
            return SessionChildren.Empty;
        }
    }

    private string? ResolveTranscript(string sessionId, string cwd) =>
        ResolveTranscript(sessionId, cwd, _projectsDir);

    public static string? ResolveTranscript(string sessionId, string cwd, string? projectsDir = null)
    {
        projectsDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects"
        );

        // Claude Code encodes the cwd into the project dir name by replacing every
        // non-alphanumeric character with '-' (e.g. C:\a\b.c -> C--a-b-c). Try that first.
        if (!string.IsNullOrEmpty(cwd))
        {
            var encoded = Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
            var direct = Path.Combine(projectsDir, encoded, sessionId + ".jsonl");
            if (File.Exists(direct))
                return direct;
        }

        // Fallback: the sessionId is a UUID, so a scan across project dirs is unambiguous and
        // covers any cwd-encoding edge case the rule above doesn't capture.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(projectsDir))
            {
                var candidate = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch { }

        return null;
    }

    private static SessionChildren Parse(string path)
    {
        // Sub-agents: a Task tool_use whose id never gets a tool_result is still running.
        var taskUses = new Dictionary<string, (string Desc, string Type)>();
        var resultIds = new HashSet<string>();

        // Background shells: map the launching tool_use id -> (command, tool), then resolve the
        // shell id from that tool_use's "...ID: <id>" result. A shell is running until a
        // task-notification reports it completed/failed/killed.
        var shellUses = new Dictionary<string, (string Command, string Tool)>();
        var shellIdByUseId = new Dictionary<string, string>();
        var finishedShells = new HashSet<string>();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Cheap pre-filter: only lines that could carry a sub-agent/shell tool_use, a
            // tool_result, or a completion notification are worth parsing as JSON.
            bool maybeChild =
                line.Contains("\"Agent\"")
                || line.Contains("\"Task\"")
                || line.Contains("tool_result")
                || line.Contains("run_in_background")
                || line.Contains("task-notification")
                || line.Contains("TaskStop")
                || line.Contains("KillShell")
                || line.Contains("KillBash");
            if (!maybeChild)
                continue;

            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; } // Malformed/partial line (transcripts are appended live) — skip it.

            var recordType = node?["type"]?.GetValue<string>();

            // Background-shell completion: a task-notification names the shell via <task-id> and a
            // terminal <status>. Skip assistant-authored lines so the model quoting a notification
            // (as in this very transcript) can't retire a live shell.
            if (recordType != "assistant" && line.Contains("task-notification"))
            {
                var idMatch = TaskIdRegex.Match(line);
                if (idMatch.Success && TerminalStatusRegex.IsMatch(line))
                    finishedShells.Add(idMatch.Groups[1].Value);
            }

            // Background-shell kill: stopping a shell (via the TaskStop/KillShell tool or the
            // interactive background-task UI) writes a "...stopped task: <id>" result but NO
            // task-notification, so detect that text directly. Skip assistant lines so the model
            // narrating a kill can't retire a live shell.
            if (recordType != "assistant" && line.Contains("stopped task"))
            {
                var m = StoppedTaskRegex.Match(line);
                if (m.Success)
                    finishedShells.Add(m.Groups[1].Value);
            }

            if (node?["message"]?["content"] is not JsonArray content)
                continue;

            // The structured toolUseResult field carries the background task id directly —
            // prefer it over regex-parsing the content string when it is present.
            var bgTaskId = node["toolUseResult"]?["backgroundTaskId"]?.GetValue<string>();

            foreach (var block in content)
            {
                var type = block?["type"]?.GetValue<string>();
                if (type == "tool_use")
                {
                    var name = block!["name"]?.GetValue<string>();
                    var id = block["id"]?.GetValue<string>();
                    if (id == null)
                        continue;
                    var input = block["input"];

                    // The sub-agent launcher is the "Agent" tool (older Claude Code called it "Task").
                    if (name is "Agent" or "Task")
                    {
                        var desc = input?["description"]?.GetValue<string>() ?? "";
                        var atype = input?["subagent_type"]?.GetValue<string>() ?? "";
                        taskUses[id] = (desc, atype);
                    }
                    // A background shell is any Bash/PowerShell launched with run_in_background.
                    else if (
                        name is "Bash" or "PowerShell"
                        && input?["run_in_background"]?.GetValue<bool>() == true
                    )
                    {
                        var cmd = SingleLine(input["command"]?.GetValue<string>() ?? "");
                        shellUses[id] = (cmd, name);
                    }
                    // A kill tool names the shell by id; the field has drifted over versions
                    // (task_id, older shell_id/bash_id), so accept any of them.
                    else if (name is "TaskStop" or "KillShell" or "KillBash")
                    {
                        var killId =
                            input?["task_id"]?.GetValue<string>()
                            ?? input?["shell_id"]?.GetValue<string>()
                            ?? input?["bash_id"]?.GetValue<string>();
                        if (killId != null)
                            finishedShells.Add(killId);
                    }
                }
                else if (type == "tool_result")
                {
                    var rid = block!["tool_use_id"]?.GetValue<string>();
                    if (rid == null)
                        continue;
                    resultIds.Add(rid);

                    // The spawn result of a background shell carries its shell id.
                    // Prefer the structured toolUseResult.backgroundTaskId field; fall back to
                    // regex-parsing the content string for older transcript formats.
                    if (shellUses.ContainsKey(rid))
                    {
                        if (!string.IsNullOrEmpty(bgTaskId))
                            shellIdByUseId[rid] = bgTaskId;
                        else
                        {
                            var text = ResultText(block["content"]);
                            var m = BackgroundIdRegex.Match(text);
                            if (m.Success)
                                shellIdByUseId[rid] = m.Groups[1].Value;
                        }
                    }
                }
            }
        }

        var subAgents = new List<SubAgent>();
        foreach (var (id, info) in taskUses)
        {
            if (resultIds.Contains(id))
                continue;
            var label = string.IsNullOrWhiteSpace(info.Desc)
                ? (string.IsNullOrWhiteSpace(info.Type) ? "sub-agent" : info.Type)
                : info.Desc;
            subAgents.Add(new SubAgent(id, label, info.Type));
        }

        var tasksDir = TasksDir(path);
        var staleCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(30);

        var shells = new List<BackgroundShell>();
        var seenShellIds = new HashSet<string>();
        foreach (var (useId, info) in shellUses)
        {
            // A shell we can't resolve an id for hasn't reported back yet (no spawn result); skip
            // it until it does, and drop any that have since reported a terminal status.
            if (!shellIdByUseId.TryGetValue(useId, out var shellId))
                continue;
            if (finishedShells.Contains(shellId))
                continue;
            // Dedupe by shell id: a transcript can carry the same shell under more than one tool_use
            // id (e.g. context replayed when a sub-agent runs), and the shell id is its true identity.
            if (!seenShellIds.Add(shellId))
                continue;
            // Fallback for orphaned shells: Claude Code sometimes omits task-notifications when a
            // shell is killed implicitly (user interrupts the session, conversation branch changes,
            // session goes away). If the output file exists but hasn't been written to in the last
            // 30 minutes, the shell is no longer producing output and can be considered done.
            if (tasksDir != null && IsOutputFileStale(tasksDir, shellId, staleCutoff))
                continue;
            var label = string.IsNullOrWhiteSpace(info.Command) ? "shell" : info.Command;
            shells.Add(new BackgroundShell(shellId, label, info.Tool));
        }

        return new SessionChildren(subAgents, shells);
    }

    // Maintains _shellFirstSeen and retires orphaned shells.
    // When the session is idle (sessionBusy=false), a much shorter stale window is used for
    // output files: shells stopped by a manual session termination clean up within ~2 minutes
    // rather than waiting for the 30-minute fallback in Parse().
    private SessionChildren ApplyFirstSeenCleanup(SessionChildren result, string? tasksDir, bool sessionBusy)
    {
        var now = DateTime.UtcNow;

        foreach (var shell in result.Shells)
            _shellFirstSeen.TryAdd(shell.ShellId, now);

        // Drop tracking for shells no longer in the running list.
        var activeIds = new HashSet<string>(result.Shells.Select(s => s.ShellId));
        foreach (var key in _shellFirstSeen.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _shellFirstSeen.Remove(key);

        if (tasksDir == null)
            return result;

        // When idle, retire shells whose output file hasn't been written in 2 minutes.
        // This handles the common case of a manually-terminated session: the shell is killed
        // with the session, no task-notification is written, but the output file goes stale fast.
        // When busy, trust the 30-minute stale check in Parse() so silent long-running shells
        // (large builds, ML training runs, etc.) aren't prematurely retired.
        var idleStaleWindow = sessionBusy ? (TimeSpan?)null : TimeSpan.FromMinutes(2);

        var filtered = result.Shells.Where(shell =>
        {
            if (tasksDir == null) return true;
            var outputFile = Path.Combine(tasksDir, shell.ShellId + ".output");

            // No-output-file fallback: retire after 30 min of first-seen regardless.
            if (!File.Exists(outputFile))
            {
                if (!_shellFirstSeen.TryGetValue(shell.ShellId, out var firstSeen)) return true;
                return (now - firstSeen).TotalMinutes < 30;
            }

            // Idle-session fast stale check: retire if output file hasn't been touched recently.
            if (idleStaleWindow.HasValue)
            {
                try
                {
                    var fi = new FileInfo(outputFile);
                    if ((now - fi.LastWriteTimeUtc) > idleStaleWindow.Value)
                        return false;
                }
                catch { }
            }

            return true;
        }).ToList();

        return filtered.Count == result.Shells.Count
            ? result
            : new SessionChildren(result.SubAgents, filtered);
    }

    // transcript: ~/.claude/projects/{enc-cwd}/{sessionId}.jsonl
    // tasks dir:  %LOCALAPPDATA%/Temp/claude/{enc-cwd}/{sessionId}/tasks
    private static string? TasksDir(string transcriptPath)
    {
        var sessionId = Path.GetFileNameWithoutExtension(transcriptPath);
        var encCwd    = Path.GetFileName(Path.GetDirectoryName(transcriptPath));
        if (string.IsNullOrEmpty(encCwd)) return null;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Temp", "claude", encCwd, sessionId, "tasks");
    }

    private static bool IsOutputFileStale(string tasksDir, string shellId, DateTime cutoff)
    {
        try
        {
            var fi = new FileInfo(Path.Combine(tasksDir, shellId + ".output"));
            return fi.Exists && fi.LastWriteTimeUtc < cutoff;
        }
        catch { return false; }
    }

    // Tool_result content is either a plain string or an array of {type:"text", text:...} blocks.
    private static string ResultText(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
            return s;
        if (content is JsonArray arr)
        {
            foreach (var b in arr)
            {
                var t = b?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(t))
                    return t;
            }
        }
        return "";
    }

    // Collapse a (possibly multi-line) command into a single trimmed line for the row label.
    private static string SingleLine(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();
}

/// <summary>The children surfaced from a session's transcript: running sub-agents and background shells.</summary>
internal readonly record struct SessionChildren(
    IReadOnlyList<SubAgent> SubAgents,
    IReadOnlyList<BackgroundShell> Shells
)
{
    public static readonly SessionChildren Empty = new([], []);
}
