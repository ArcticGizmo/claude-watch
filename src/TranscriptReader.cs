using System.Text.Json;
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
    private readonly Dictionary<string, CacheEntry> _titleCache = new();
    private readonly Dictionary<string, ContextCacheEntry> _contextFillCache = new();
    private readonly Dictionary<string, BareCommandCacheEntry> _bareCommandCache = new();
    private readonly Dictionary<string, ArtifactCacheEntry> _artifactCache = new();

    private readonly record struct CacheEntry(long Length, DateTime WriteUtc, string? Result);
    private readonly record struct BareCommandCacheEntry(long Length, DateTime WriteUtc, bool Result);
    private readonly record struct ContextCacheEntry(long Length, DateTime WriteUtc, float? Fill, int Window);
    private readonly record struct ArtifactCacheEntry(long Length, DateTime WriteUtc, IReadOnlyList<Artifact> Result);

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

    /// <summary>
    /// True when the most recent meaningful turn in the session's transcript is a built-in slash
    /// command (e.g. <c>/clear</c>, <c>/model</c>, <c>/doctor</c>) that completed without the model
    /// doing any work — i.e. no assistant message has been appended since the command was invoked.
    ///
    /// This is the discriminator for the fast/interactive built-ins the user never wants flagged as
    /// "running/done": those commands flip the session busy->idle (or busy->waiting for the ones with
    /// a picker) for a fraction of a second but produce no assistant turn, whereas a real prompt — or
    /// a command that actually drives the model, like <c>/init</c> or a custom command — always leaves
    /// assistant records behind. Metadata trailers (<c>last-prompt</c>, <c>mode</c>, <c>ai-title</c>, …)
    /// are ignored; only <c>assistant</c> records and command invocations are weighed.
    ///
    /// Cached by (length, last-write) like the other readers, so a scan that doesn't change the
    /// transcript costs a stat, not a parse. Best-effort; never throws (returns false on any failure).
    /// </summary>
    public bool LastTurnWasBareCommand(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        var path = ResolveTranscript(sessionId, cwd);
        if (path == null)
            return false;

        try
        {
            var fi = new FileInfo(path);
            if (
                _bareCommandCache.TryGetValue(path, out var cached)
                && cached.Length == fi.Length
                && cached.WriteUtc == fi.LastWriteTimeUtc
            )
                return cached.Result;

            var result = ParseBareCommand(path);
            _bareCommandCache[path] = new BareCommandCacheEntry(fi.Length, fi.LastWriteTimeUtc, result);
            return result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the session's explicit name as set by Claude Code's built-in <c>/rename</c> command —
    /// a <c>custom-title</c> transcript record. Null when the transcript can't be located/read or was
    /// never renamed. The auto-generated <c>ai-title</c> is deliberately ignored.
    ///
    /// A <c>/rename</c> title may have been set once early on, so — like Claude Code itself — we look
    /// in the tail first (where a later rename lands) and fall back to the head.
    /// </summary>
    public string? GetTitle(string sessionId, string cwd)
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
                _titleCache.TryGetValue(path, out var cached)
                && cached.Length == fi.Length
                && cached.WriteUtc == fi.LastWriteTimeUtc
            )
                return cached.Result;

            var result = ParseTitle(path);
            _titleCache[path] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc, result);
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the session's context-window fill (0–1) and the resolved window size in tokens.
    /// Fill is null when no usage data is available. The window defaults to
    /// <see cref="ModelContext.DefaultWindow"/> when no <c>/model</c> command was found.
    /// Best-effort; never throws.
    /// </summary>
    public (float? Fill, int Window) GetContextFill(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return (null, ModelContext.DefaultWindow);

        var path = ResolveTranscript(sessionId, cwd);
        if (path == null)
            return (null, ModelContext.DefaultWindow);

        try
        {
            var fi = new FileInfo(path);
            if (_contextFillCache.TryGetValue(path, out var cached)
                && cached.Length == fi.Length && cached.WriteUtc == fi.LastWriteTimeUtc)
                return (cached.Fill, cached.Window);

            var (fill, window) = ParseContextFill(path, cwd);
            _contextFillCache[path] = new ContextCacheEntry(fi.Length, fi.LastWriteTimeUtc, fill, window);
            return (fill, window);
        }
        catch
        {
            return (null, ModelContext.DefaultWindow);
        }
    }

    /// <summary>
    /// Returns the web Artifacts this session has published to claude.ai, de-duplicated by URL and in
    /// the order they were first published. Empty when the transcript can't be located/read or holds
    /// no artifacts. Best-effort; never throws.
    /// </summary>
    public IReadOnlyList<Artifact> GetArtifacts(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return [];

        var path = ResolveTranscript(sessionId, cwd);
        if (path == null)
            return [];

        try
        {
            var fi = new FileInfo(path);
            if (_artifactCache.TryGetValue(path, out var cached)
                && cached.Length == fi.Length && cached.WriteUtc == fi.LastWriteTimeUtc)
                return cached.Result;

            var result = ParseArtifacts(path);
            _artifactCache[path] = new ArtifactCacheEntry(fi.Length, fi.LastWriteTimeUtc, result);
            return result;
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<Artifact> ParseArtifacts(string path)
    {
        // An Artifact can be published anywhere in the session, so we read the whole file — but it's
        // cheap: a substring pre-filter skips almost every line, and the result is cached by
        // length+mtime so this only re-runs when the transcript actually changed. Each publish leaves
        // one record whose toolUseResult.url is the hosted page; re-publishing reuses the URL, so we
        // de-dupe by URL (last title wins) while preserving first-seen order.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        var order = new List<string>();
        var titles = new Dictionary<string, string>();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Cheap pre-filter: only the publish result records carry the artifact URL stem.
            if (!line.Contains("code/artifact"))
                continue;

            try
            {
                var result = JsonNode.Parse(line)?["toolUseResult"];
                var url = result?["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url) || !url.Contains("/code/artifact/"))
                    continue;

                var title = result?["title"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title))
                    title = "Untitled artifact";

                if (!titles.ContainsKey(url))
                    order.Add(url);
                titles[url] = title.Trim();
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        if (order.Count == 0)
            return [];

        return order.Select(u => new Artifact(u, titles[u])).ToList();
    }

    private static (float? fill, int window) ParseContextFill(string path, string cwd)
    {
        // A /model switch can land anywhere in the transcript, and the most recent one wins — so unlike
        // the activity/title tail-scans we must read the whole file. It's cheap: a substring pre-filter
        // skips almost every line untouched (model records are rare, usage records parse only near the
        // end's worth of assistant turns), and the result is cached by length+mtime so this full pass
        // only re-runs when the transcript actually changed.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        long latestUsed = 0;
        string? latestDisplayName = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
                continue;

            // /model confirmation: a user-type record whose content is the terminal output string
            // wrapped in <local-command-stdout>. The wrapper is the key discriminator — and it must be
            // at the *start* of the content: user messages that quote or mention a "Set model to" line
            // in their body carry the wrapper mid-string and must not be mistaken for a real switch.
            if (line.Contains("local-command-stdout") && line.Contains("Set model to"))
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node?["type"]?.GetValue<string>() == "user")
                    {
                        var raw = node["message"]?["content"]?.GetValue<string>();
                        if (raw != null && raw.StartsWith("<local-command-stdout>"))
                        {
                            var dn = ModelContext.ParseDisplayName(raw);
                            if (dn != null)
                                latestDisplayName = dn;
                        }
                    }
                }
                catch { }
            }

            if (line.Contains("\"usage\""))
            {
                try
                {
                    var usage = JsonNode.Parse(line)?["message"]?["usage"];
                    if (usage != null)
                    {
                        long total = TokenLong(usage["input_tokens"]) + TokenLong(usage["cache_read_input_tokens"]);
                        if (total > 0)
                            latestUsed = total;
                    }
                }
                catch { }
            }
        }

        // A /model confirmation in the transcript is authoritative (the user explicitly switched, and
        // the most recent one wins). Lacking one, the session is running the configured default model —
        // whose transcript message.model field can't reveal whether it's the 200k or 1M variant — so we
        // fall back to the model id in settings.json, where the "[1m]" suffix makes that distinction.
        int window = latestDisplayName != null
            ? ModelContext.WindowFor(latestDisplayName)
            : ModelContext.WindowForConfiguredModel(ReadConfiguredModel(cwd));

        if (latestUsed == 0)
            return (null, window);

        return (Math.Clamp((float)latestUsed / window, 0f, 1f), window);
    }

    private static readonly JsonDocumentOptions JsonLeniency = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads the configured default <c>model</c> from Claude Code's settings, in the same precedence
    /// Claude Code applies: project-local (<c>.claude/settings.local.json</c>) over project
    /// (<c>.claude/settings.json</c>) over user (<c>~/.claude/settings.json</c>). The first file that
    /// carries a non-blank <c>model</c> wins. Returns null when none do (e.g. the model is inherited
    /// from a managed/enterprise layer we don't read), which the caller maps to the default window.
    /// Best-effort; never throws.
    /// </summary>
    private static string? ReadConfiguredModel(string cwd)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(cwd))
        {
            candidates.Add(Path.Combine(cwd, ".claude", "settings.local.json"));
            candidates.Add(Path.Combine(cwd, ".claude", "settings.json"));
        }
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json"));

        foreach (var path in candidates)
        {
            var model = ReadModelField(path);
            if (model != null)
                return model;
        }

        return null;
    }

    // Reads the top-level "model" string from one settings file, or null if absent/blank/unreadable.
    private static string? ReadModelField(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonLeniency);
            if (doc.RootElement.TryGetProperty("model", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                var s = m.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
        }
        catch { }

        return null;
    }

    private static long TokenLong(JsonNode? n)
    {
        if (n == null) return 0;
        try { return n.GetValue<long>(); }
        catch { try { return (long)n.GetValue<double>(); } catch { return 0; } }
    }

    /// <summary>Returns the full path to a session's .jsonl transcript, or null if not found.</summary>
    public static string? FindTranscript(string sessionId, string cwd)
    {
        var projectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        if (!string.IsNullOrEmpty(cwd))
        {
            var encoded = Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
            var direct = Path.Combine(projectsDir, encoded, sessionId + ".jsonl");
            if (File.Exists(direct))
                return direct;
        }

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
                    latest = ToolSummary.Describe(name, block["input"]);
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        return latest;
    }

    private static bool ParseBareCommand(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        long start = Math.Max(0, fs.Length - TailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);

        // Seeking into the middle almost certainly lands mid-record; drop the partial first line.
        // (If the tail spans a full window there is necessarily an assistant turn within it, so a
        // bare command older than the window can't be the latest meaningful turn anyway.)
        if (start > 0)
            reader.ReadLine();

        // Walk chronologically and remember only which of the two meaningful kinds came last: an
        // assistant turn (the model did work) or a command invocation (the user ran a slash command).
        // Everything else — plain user prompts, command stdout, mode/title/snapshot metadata — is
        // skipped so the trailing metadata records every transcript ends with don't muddy the verdict.
        bool lastWasCommand = false;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            bool maybeAssistant = line.Contains("\"type\":\"assistant\"");
            bool maybeCommand = line.Contains("command-name");
            if (!maybeAssistant && !maybeCommand)
                continue;

            try
            {
                var node = JsonNode.Parse(line);
                if (node?["type"]?.GetValue<string>() == "assistant")
                {
                    lastWasCommand = false;
                    continue;
                }

                // A command invocation is recorded either as a system/local_command record (content
                // is the command string) or as a user record whose message content is that string.
                // Either way the content starts with "<command-name>"; requiring the prefix at the
                // very start keeps a user message that merely quotes the tag from being mistaken for
                // a real invocation.
                if (ContentString(node)?.StartsWith("<command-name>") == true)
                    lastWasCommand = true;
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        return lastWasCommand;
    }

    // Pulls a record's textual content out of either shape we see for command invocations:
    // a top-level "content" string (system/local_command) or "message.content" string (user).
    // Returns null when content is absent or an array (e.g. an assistant block list).
    private static string? ContentString(JsonNode? node)
    {
        if (node?["content"] is JsonValue direct)
        {
            try { return direct.GetValue<string>(); } catch { }
        }
        if (node?["message"]?["content"] is JsonValue msg)
        {
            try { return msg.GetValue<string>(); } catch { }
        }
        return null;
    }

    private static string? ParseTitle(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Scan the tail first — a later /rename lands here. If none and the file spans more than one
        // window, a title set once early may be in the head, so look there before giving up.
        return ScanWindow(fs, Math.Max(0, fs.Length - TailBytes))
            ?? (fs.Length > TailBytes ? ScanWindow(fs, 0) : null);
    }

    // Scans a window of the transcript from <paramref name="start"/> and returns the last
    // custom-title (the /rename name) record it contains, or null.
    private static string? ScanWindow(FileStream fs, long start)
    {
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, leaveOpen: true);

        // A non-zero start almost certainly lands mid-record; drop the partial first line.
        if (start > 0)
            reader.ReadLine();

        string? custom = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Cheap pre-filter: the /rename record type is "custom-title".
            if (!line.Contains("custom-title"))
                continue;

            try
            {
                var node = JsonNode.Parse(line);
                if (node?["type"]?.GetValue<string>() == "custom-title")
                    custom = node["customTitle"]?.GetValue<string>() ?? custom;
            }
            catch
            {
                // Malformed/partial line — skip it.
            }
        }

        return custom;
    }
}
