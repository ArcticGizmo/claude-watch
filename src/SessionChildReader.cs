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

    private static readonly Regex TaskIdRegex = new(@"<task-id>([A-Za-z0-9]+)</task-id>", RegexOptions.Compiled);
    private static readonly Regex TerminalStatusRegex = new(@"<status>(completed|failed|killed)</status>", RegexOptions.Compiled);
    // The shell id Claude Code reports back when a command is launched in the background.
    private static readonly Regex BackgroundIdRegex = new(@"running in background with ID:\s*([A-Za-z0-9]+)", RegexOptions.Compiled);

    private readonly record struct CacheEntry(long Length, DateTime WriteUtc, SessionChildren Result);

    /// <summary>
    /// Returns the children currently running under the given session, or empty lists if the
    /// transcript can't be located or read. Only direct children of the session are reported.
    /// </summary>
    public SessionChildren GetRunning(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return SessionChildren.Empty;

        var path = ResolveTranscript(sessionId, cwd);
        if (path == null)
            return SessionChildren.Empty;

        try
        {
            var fi = new FileInfo(path);
            if (
                _cache.TryGetValue(path, out var cached)
                && cached.Length == fi.Length
                && cached.WriteUtc == fi.LastWriteTimeUtc
            )
                return cached.Result;

            var result = Parse(path);
            _cache[path] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc, result);
            return result;
        }
        catch
        {
            return SessionChildren.Empty;
        }
    }

    private string? ResolveTranscript(string sessionId, string cwd)
    {
        // Claude Code encodes the cwd into the project dir name by replacing every
        // non-alphanumeric character with '-' (e.g. C:\a\b.c -> C--a-b-c). Try that first.
        if (!string.IsNullOrEmpty(cwd))
        {
            var encoded = Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
            var direct = Path.Combine(_projectsDir, encoded, sessionId + ".jsonl");
            if (File.Exists(direct))
                return direct;
        }

        // Fallback: the sessionId is a UUID, so a scan across project dirs is unambiguous and
        // covers any cwd-encoding edge case the rule above doesn't capture.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_projectsDir))
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
                || line.Contains("task-notification");
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

            if (node?["message"]?["content"] is not JsonArray content)
                continue;

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
                }
                else if (type == "tool_result")
                {
                    var rid = block!["tool_use_id"]?.GetValue<string>();
                    if (rid == null)
                        continue;
                    resultIds.Add(rid);

                    // The spawn result of a background shell carries its shell id; record it only
                    // for tool_use ids we've seen launch a background shell.
                    if (shellUses.ContainsKey(rid))
                    {
                        var text = ResultText(block["content"]);
                        var m = BackgroundIdRegex.Match(text);
                        if (m.Success)
                            shellIdByUseId[rid] = m.Groups[1].Value;
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

        var shells = new List<BackgroundShell>();
        foreach (var (useId, info) in shellUses)
        {
            // A shell we can't resolve an id for hasn't reported back yet (no spawn result); skip
            // it until it does, and drop any that have since reported a terminal status.
            if (!shellIdByUseId.TryGetValue(useId, out var shellId))
                continue;
            if (finishedShells.Contains(shellId))
                continue;
            var label = string.IsNullOrWhiteSpace(info.Command) ? "shell" : info.Command;
            shells.Add(new BackgroundShell(shellId, label, info.Tool));
        }

        return new SessionChildren(subAgents, shells);
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
