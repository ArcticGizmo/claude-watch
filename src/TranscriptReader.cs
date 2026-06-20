using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeWatch;

/// <summary>
/// Reads a session's most recent tool call from its transcript and turns it into a short,
/// human-friendly phrase ("Reading Foo.cs", "Running: npm test", "Editing Bar.cs").
///
/// The transcript lives at <c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>. It is appended
/// live and can grow large, so we never read the whole file — only the <see cref="TailBytes"/>
/// tail is seeked and scanned. The most recent <c>tool_use</c> block in that tail wins.
///
/// Results are cached per transcript by (length, last-write) so a scan while a session is busy —
/// during which the transcript does not change between consecutive scans — costs a stat, not a parse.
/// Every failure path returns <c>null</c>; this is best-effort and must never throw.
/// </summary>
internal sealed class TranscriptReader
{
    private const int TailBytes = 32 * 1024;

    private readonly string _projectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "projects"
    );

    private readonly Dictionary<string, CacheEntry> _cache = new();

    private readonly record struct CacheEntry(long Length, DateTime WriteUtc, string? Result);

    /// <summary>
    /// Returns a friendly phrase describing the latest tool call in the session's transcript,
    /// or <c>null</c> if the transcript can't be located/read or holds no tool call.
    /// </summary>
    public string? GetActivity(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var path = ResolveTranscript(sessionId, cwd);
        if (path == null)
            return null;

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
            return null;
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

    private static string? Parse(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        long start = Math.Max(0, fs.Length - TailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);

        // When we seeked into the middle of the file the first line is almost certainly a partial
        // record; discard it so JSON parsing starts on a clean line boundary.
        if (start > 0)
            reader.ReadLine();

        // Lines are chronological, so the last tool_use we see is the most recent. Track only the
        // friendly phrase, overwriting as newer tool calls appear.
        string? latest = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Cheap pre-filter: only assistant lines carrying a tool_use are worth parsing.
            if (!line.Contains("tool_use"))
                continue;

            try
            {
                if (JsonNode.Parse(line)?["message"]?["content"] is not JsonArray content)
                    continue;

                foreach (var block in content)
                {
                    if (block?["type"]?.GetValue<string>() != "tool_use")
                        continue;
                    var name = block["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name))
                        continue;
                    latest = Describe(name, block["input"]);
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        return latest;
    }

    /// <summary>Maps a tool name + its input to a short present-tense phrase.</summary>
    private static string Describe(string tool, JsonNode? input)
    {
        string? Str(string key) => input?[key]?.GetValue<string>();

        switch (tool)
        {
            case "Read":
                return "Reading " + FileLabel(Str("file_path"));
            case "Edit":
            case "MultiEdit":
                return "Editing " + FileLabel(Str("file_path"));
            case "Write":
                return "Writing " + FileLabel(Str("file_path"));
            case "NotebookEdit":
                return "Editing " + FileLabel(Str("notebook_path"));
            case "Bash":
                return "Running: " + Clip(Str("command") ?? "command");
            case "Grep":
                return "Searching: " + Clip(Str("pattern") ?? "");
            case "Glob":
                return "Finding: " + Clip(Str("pattern") ?? "");
            case "Task":
            case "Agent":
                return "Delegating: " + Clip(Str("description") ?? "sub-agent");
            case "WebFetch":
                return "Fetching " + Clip(Str("url") ?? "");
            case "WebSearch":
                return "Searching web: " + Clip(Str("query") ?? "");
            case "TodoWrite":
                return "Updating todos";
            default:
                return tool;
        }
    }

    private static string FileLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "file";
        try
        {
            var name = Path.GetFileName(path.TrimEnd('/', '\\'));
            return string.IsNullOrEmpty(name) ? Clip(path) : name;
        }
        catch
        {
            return Clip(path);
        }
    }

    private static string Clip(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        const int max = 60;
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
