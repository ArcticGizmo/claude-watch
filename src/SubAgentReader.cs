using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeWatch;

/// <summary>
/// Discovers the sub-agents (Agent/Task tool invocations) currently running under a session.
///
/// Sub-agents do not get their own <c>~/.claude/sessions/*.json</c> file — they run
/// in the parent's process. The only live record is the parent's transcript at
/// <c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>, where each sub-agent appears as
/// an assistant <c>tool_use</c> block named "Agent" (or "Task" in older builds). A sub-agent is still running while that
/// tool_use has no matching <c>tool_result</c> yet — a signal that holds even when the
/// sub-agent is sitting in a long, silent shell command (where a file-mtime heuristic would
/// wrongly report it as finished).
///
/// Results are cached per transcript by (length, last-write) so the common case — a scan while
/// a sub-agent runs, during which the parent transcript does not change — costs a stat, not a parse.
/// </summary>
internal sealed class SubAgentReader
{
    private readonly string _projectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects"
    );

    private readonly Dictionary<string, CacheEntry> _cache = new();

    private readonly record struct CacheEntry(
        long Length,
        DateTime WriteUtc,
        IReadOnlyList<SubAgent> Result
    );

    /// <summary>
    /// Returns the sub-agents currently running under the given session, or an empty list if
    /// the transcript can't be located or read. Only direct children of the session are
    /// reported (a sub-agent's own sub-agents live in that sub-agent's transcript).
    /// </summary>
    public IReadOnlyList<SubAgent> GetRunning(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return [];

        var path = ResolveTranscript(sessionId, cwd);
        if (path == null)
            return [];

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
            return [];
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

    private static IReadOnlyList<SubAgent> Parse(string path)
    {
        // Collect every Task tool_use and the set of tool_use ids that already have a result;
        // a Task whose id never gets a result is a sub-agent still running.
        var taskUses = new Dictionary<string, (string Desc, string Type)>();
        var resultIds = new HashSet<string>();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Cheap pre-filter: only the (rare) lines that could carry a sub-agent tool_use or
            // any tool_result are worth parsing as JSON. Most transcript lines match neither.
            if (!line.Contains("\"Agent\"") && !line.Contains("\"Task\"") && !line.Contains("tool_result"))
                continue;

            try
            {
                if (JsonNode.Parse(line)?["message"]?["content"] is not JsonArray content)
                    continue;

                foreach (var block in content)
                {
                    var type = block?["type"]?.GetValue<string>();
                    // The sub-agent launcher is the "Agent" tool (older Claude Code called it "Task").
                    var name = type == "tool_use" ? block!["name"]?.GetValue<string>() : null;
                    if (name is "Agent" or "Task")
                    {
                        var id = block["id"]?.GetValue<string>();
                        if (id == null)
                            continue;
                        var input = block["input"];
                        var desc = input?["description"]?.GetValue<string>() ?? "";
                        var atype = input?["subagent_type"]?.GetValue<string>() ?? "";
                        taskUses[id] = (desc, atype);
                    }
                    else if (type == "tool_result")
                    {
                        var rid = block!["tool_use_id"]?.GetValue<string>();
                        if (rid != null)
                            resultIds.Add(rid);
                    }
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        var running = new List<SubAgent>();
        foreach (var (id, info) in taskUses)
        {
            if (resultIds.Contains(id))
                continue;
            var label = string.IsNullOrWhiteSpace(info.Desc)
                ? (string.IsNullOrWhiteSpace(info.Type) ? "sub-agent" : info.Type)
                : info.Desc;
            running.Add(new SubAgent(id, label, info.Type));
        }
        return running;
    }
}
