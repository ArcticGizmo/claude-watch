using System.Text.Json.Nodes;

namespace ClaudeWatch;

/// <summary>Aggregated statistics for a single day, derived from session transcripts.</summary>
/// <param name="Day">The local calendar day these figures cover.</param>
/// <param name="SessionCount">Distinct sessions (transcripts) with at least one record that day.</param>
/// <param name="ActiveTime">Estimated engagement time — see <see cref="SessionStatsService"/> for how
/// it is inferred from the gaps between transcript records.</param>
internal sealed record DayStats(DateOnly Day, int SessionCount, TimeSpan ActiveTime)
{
    public static DayStats Empty(DateOnly day) => new(day, 0, TimeSpan.Zero);

    /// <summary>One-line summary for the tray menu, e.g. "Today: 4 sessions · 3h 12m active".</summary>
    public string TraySummary()
    {
        if (SessionCount == 0)
            return "Today: no sessions yet";
        var sessions = SessionCount == 1 ? "1 session" : $"{SessionCount} sessions";
        return $"Today: {sessions} · {FormatActive(ActiveTime)} active";
    }

    private static string FormatActive(TimeSpan t) => StatsFormat.Duration(t);
}

/// <summary>Token counts split by billing class. Cache writes price at ~1.25× input, cache reads at
/// ~0.1× — kept separate so the cost estimate is honest rather than treating every token the same.</summary>
internal readonly record struct TokenTotals(long Input, long Output, long CacheWrite, long CacheRead)
{
    public long Total => Input + Output + CacheWrite + CacheRead;
    public static readonly TokenTotals Zero = default;
    public static TokenTotals operator +(TokenTotals a, TokenTotals b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);
}

internal sealed record ToolStat(string Tool, int Count);
internal sealed record ProjectStat(string Project, int Sessions, TimeSpan ActiveTime, long Tokens);

/// <summary>Per-model token totals plus the equivalent pay-as-you-go API cost (null when the model's
/// price isn't known, so we never fabricate a number).</summary>
internal sealed record ModelStat(string Model, TokenTotals Tokens, decimal? Cost);

/// <summary>The full Tier 1 + 2 statistics for one day, as shown in the Stats window.</summary>
internal sealed record StatsReport(
    DateOnly Day,
    int SessionCount,
    TimeSpan ActiveTime,
    int Prompts,
    int ToolCalls,
    int SubAgents,
    TokenTotals Tokens,
    decimal EstimatedCost,        // sum of the per-model costs we could price
    bool CostComplete,            // false when some tokens used a model we have no price for
    IReadOnlyList<ProjectStat> Projects,
    IReadOnlyList<ToolStat> Tools,
    IReadOnlyList<ModelStat> Models,
    int[] HourlyActiveSeconds)    // 24 bins, local hour -> estimated active seconds
{
    public static StatsReport Empty(DateOnly day) => new(
        day, 0, TimeSpan.Zero, 0, 0, 0, TokenTotals.Zero, 0m, true,
        [], [], [], new int[24]);
}

/// <summary>Shared formatting for stat values, so the tray line and the Stats window read identically.</summary>
internal static class StatsFormat
{
    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return "<1m";
    }

    /// <summary>Compact token count: 12.3M / 45.6k / 789.</summary>
    public static string Tokens(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.0}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.0}k";
        return n.ToString();
    }

    public static string Cost(decimal usd) => usd >= 100m ? $"${usd:0}" : $"${usd:0.00}";
}

/// <summary>
/// Computes session statistics by scanning Claude Code transcripts on disk
/// (<c>~/.claude/projects/{enc-cwd}/{sessionId}.jsonl</c>). Transcript-derived and retroactive: it
/// records nothing of its own, just reads the append-only logs Claude Code already writes, so it works
/// for sessions that ran long before this feature existed — and survives the tray being closed.
///
/// Each transcript is one session; a record's <c>timestamp</c> places it on a calendar day. "Active
/// time" is inferred, not measured: walking a session's records in time order, each gap counts toward
/// active time, but a gap longer than <see cref="IdleThreshold"/> is capped (the user had walked away),
/// so the figure reflects engagement rather than wall-clock since the first message.
///
/// Best-effort throughout — unreadable files and malformed lines are skipped, never thrown. Reads are
/// off the caller's hot path; callers should invoke <see cref="ForDay"/> on a background thread.
///
/// Phase 1 surfaces only the daily headline (sessions + active time); the scan is structured so later
/// phases can layer tokens, cost, tool mix and trends onto the same pass.
/// </summary>
internal static class SessionStatsService
{
    private static readonly string ProjectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    // Gaps longer than this between a session's records are treated as "walked away" and capped, so
    // active time measures engagement. Matches SessionMonitor's NeedsAttention window (5 minutes).
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);

    // A small tail credited after a session's last record, so a single quick exchange (one or two
    // records, no gaps) isn't counted as zero active time.
    private static readonly TimeSpan SessionTail = TimeSpan.FromSeconds(30);

    /// <summary>Computes the headline statistics for the given local day.</summary>
    public static DayStats ForDay(DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        int sessions = 0;
        var active = TimeSpan.Zero;

        foreach (var file in EnumerateCandidateTranscripts(dayStart))
        {
            var times = ReadTimestampsInRange(file, dayStart, dayEnd);
            if (times.Count == 0)
                continue;
            sessions++;
            active += ActiveSpan(times);
        }

        return new DayStats(day, sessions, active);
    }

    // Only transcripts last modified at or after the window start can contain a record inside it — a
    // file untouched since yesterday cannot hold one of today's records. This keeps a "today" scan to
    // the handful of files actually touched today rather than every transcript ever written.
    private static IEnumerable<string> EnumerateCandidateTranscripts(DateTime windowStart)
    {
        if (!Directory.Exists(ProjectsDir))
            yield break;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(ProjectsDir); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.jsonl"); }
            catch { continue; }

            foreach (var file in files)
            {
                DateTime mtime;
                try { mtime = File.GetLastWriteTime(file); }
                catch { continue; }
                if (mtime >= windowStart)
                    yield return file;
            }
        }
    }

    // Reads the record timestamps falling inside [from, to), sorted ascending. Opened shared so an
    // actively-appended transcript can still be read; malformed/partial lines are skipped.
    private static List<DateTime> ReadTimestampsInRange(string file, DateTime from, DateTime to)
    {
        var result = new List<DateTime>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                    continue;
                // Cheap substring pre-filter before paying for a JSON parse — most lines are kept,
                // but skipping the rare timestamp-less record (e.g. file-history-snapshot) is free.
                if (!line.Contains("\"timestamp\""))
                    continue;

                try
                {
                    if (JsonNode.Parse(line)?["timestamp"]?.GetValue<string>() is { } iso
                        && DateTimeOffset.TryParse(iso, out var dto))
                    {
                        var t = dto.LocalDateTime;
                        if (t >= from && t < to)
                            result.Add(t);
                    }
                }
                catch { }
            }
        }
        catch { }

        result.Sort();
        return result;
    }

    // Sums the gaps between consecutive records, capping any gap over the idle threshold, then adds a
    // small tail. times must be sorted ascending and non-empty.
    private static TimeSpan ActiveSpan(List<DateTime> times)
    {
        var total = TimeSpan.Zero;
        for (int i = 1; i < times.Count; i++)
        {
            var gap = times[i] - times[i - 1];
            if (gap <= TimeSpan.Zero)
                continue;
            total += gap < IdleThreshold ? gap : IdleThreshold;
        }
        return total + SessionTail;
    }

    // ── Rich report (Tier 1 + 2) ─────────────────────────────────────────────────
    // Per-model equivalent API pricing, USD per million tokens (input, output). Keys are matched as a
    // prefix of the transcript's model id so dated snapshots (claude-haiku-4-5-20251001) resolve too.
    // Cache reads bill at ~0.1× input and cache writes at ~1.25× input — applied in CostOf.
    private static readonly (string key, decimal input, decimal output)[] Prices =
    [
        ("claude-fable-5",   10m, 50m),
        ("claude-opus-4",     5m, 25m),   // 4.5 / 4.6 / 4.7 / 4.8 all share Opus-tier pricing
        ("claude-sonnet-4",   3m, 15m),
        ("claude-haiku-4",    1m,  5m),
    ];

    /// <summary>Computes the full daily report (headline figures plus tokens, cost, per-project,
    /// tool mix, model split and an hourly activity histogram). Heavier than <see cref="ForDay"/> —
    /// call it off the UI thread.</summary>
    public static StatsReport ReportForDay(DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        var sessions = new List<SessionDayData>();
        foreach (var file in EnumerateCandidateTranscripts(dayStart))
        {
            var data = ParseSessionDay(file, dayStart, dayEnd);
            if (data != null)
                sessions.Add(data);
        }
        return Aggregate(day, sessions);
    }

    // Parses one transcript, keeping only records timestamped inside the day, and rolls up everything
    // the report needs in a single pass. Returns null if the file has no records in range.
    private static SessionDayData? ParseSessionDay(string file, DateTime from, DateTime to)
    {
        var data = new SessionDayData();
        bool any = false;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch { continue; }
                if (node == null)
                    continue;

                // The cwd is constant for a session; grab it from the first record that carries one.
                if (data.Project.Length == 0)
                {
                    var cwd = node["cwd"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(cwd))
                        data.Project = Path.GetFileName(cwd!.TrimEnd('/', '\\'));
                }

                if (ParseTimestamp(node["timestamp"]?.GetValue<string>()) is not { } t || t < from || t >= to)
                    continue;
                any = true;
                data.Times.Add(t);

                var type = node["type"]?.GetValue<string>();
                bool isMeta = node["isMeta"]?.GetValue<bool>() ?? false;
                var message = node["message"];
                var content = message?["content"];

                if (type == "user" && !isMeta && IsUserPrompt(content))
                    data.Prompts++;

                // Token usage rides on assistant records; attribute it to the record's model.
                if (message?["usage"] is { } usage)
                {
                    var model = message["model"]?.GetValue<string>() ?? "unknown";
                    var tt = new TokenTotals(
                        LongOf(usage["input_tokens"]),
                        LongOf(usage["output_tokens"]),
                        LongOf(usage["cache_creation_input_tokens"]),
                        LongOf(usage["cache_read_input_tokens"]));
                    data.Tokens += tt;
                    data.Models[model] = data.Models.GetValueOrDefault(model, TokenTotals.Zero) + tt;
                }

                if (content is JsonArray blocks)
                {
                    foreach (var b in blocks)
                    {
                        if (b?["type"]?.GetValue<string>() != "tool_use")
                            continue;
                        var name = b!["name"]?.GetValue<string>() ?? "tool";
                        data.ToolCalls++;
                        data.ToolCounts[name] = data.ToolCounts.GetValueOrDefault(name) + 1;
                        if (name == "Task")
                            data.SubAgents++;   // the Task tool is how a session spawns a sub-agent
                    }
                }
            }
        }
        catch { }
        return any ? data : null;
    }

    // A user record counts as a prompt when it carries author text — a plain-string body, or an array
    // with a text block. Records that are only tool_result blocks (the common array shape) don't count.
    private static bool IsUserPrompt(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
            return !string.IsNullOrWhiteSpace(s);
        if (content is JsonArray arr)
            foreach (var b in arr)
                if (b?["type"]?.GetValue<string>() == "text")
                    return true;
        return false;
    }

    private static StatsReport Aggregate(DateOnly day, List<SessionDayData> sessions)
    {
        var active = TimeSpan.Zero;
        int prompts = 0, toolCalls = 0, subAgents = 0;
        var tokens = TokenTotals.Zero;
        var hourly = new int[24];
        var toolCounts = new Dictionary<string, int>();
        var modelTokens = new Dictionary<string, TokenTotals>();
        var projects = new Dictionary<string, (int sessions, TimeSpan active, long tokens)>();

        foreach (var s in sessions)
        {
            s.Times.Sort();
            var span = ActiveSpan(s.Times);
            active += span;
            prompts += s.Prompts;
            toolCalls += s.ToolCalls;
            subAgents += s.SubAgents;
            tokens += s.Tokens;
            AccumulateHourly(hourly, s.Times);

            foreach (var (k, v) in s.ToolCounts)
                toolCounts[k] = toolCounts.GetValueOrDefault(k) + v;
            foreach (var (k, v) in s.Models)
                modelTokens[k] = modelTokens.GetValueOrDefault(k, TokenTotals.Zero) + v;

            var proj = s.Project.Length > 0 ? s.Project : "session";
            var p = projects.GetValueOrDefault(proj);
            projects[proj] = (p.sessions + 1, p.active + span, p.tokens + s.Tokens.Total);
        }

        decimal totalCost = 0;
        bool costComplete = true;
        var models = new List<ModelStat>();
        foreach (var (model, tt) in modelTokens)
        {
            var cost = CostOf(model, tt);
            if (cost is { } c) totalCost += c;
            else if (tt.Total > 0) costComplete = false;
            models.Add(new ModelStat(model, tt, cost));
        }
        models.Sort((a, b) => b.Tokens.Total.CompareTo(a.Tokens.Total));

        var toolStats = toolCounts
            .Select(kv => new ToolStat(kv.Key, kv.Value))
            .OrderByDescending(t => t.Count)
            .ToList();
        var projectStats = projects
            .Select(kv => new ProjectStat(kv.Key, kv.Value.sessions, kv.Value.active, kv.Value.tokens))
            .OrderByDescending(p => p.ActiveTime)
            .ToList();

        return new StatsReport(day, sessions.Count, active, prompts, toolCalls, subAgents,
            tokens, totalCost, costComplete, projectStats, toolStats, models, hourly);
    }

    // Attributes each capped inter-record gap to the hour the gap started in, so the histogram reflects
    // when the day's engagement actually happened. Same idle-threshold rule as ActiveSpan.
    private static void AccumulateHourly(int[] hourly, List<DateTime> times)
    {
        for (int i = 1; i < times.Count; i++)
        {
            var gap = times[i] - times[i - 1];
            if (gap <= TimeSpan.Zero)
                continue;
            var capped = gap < IdleThreshold ? gap : IdleThreshold;
            hourly[times[i - 1].Hour] += (int)capped.TotalSeconds;
        }
        if (times.Count > 0)
            hourly[times[^1].Hour] += (int)SessionTail.TotalSeconds;
    }

    private static decimal? CostOf(string model, TokenTotals t)
    {
        foreach (var (key, input, output) in Prices)
        {
            if (!model.StartsWith(key, StringComparison.Ordinal))
                continue;
            return (t.Input * input
                  + t.CacheRead * input * 0.10m
                  + t.CacheWrite * input * 1.25m
                  + t.Output * output) / 1_000_000m;
        }
        return null;   // unknown model — surfaced as "—" rather than a fabricated figure
    }

    private static DateTime? ParseTimestamp(string? iso) =>
        !string.IsNullOrEmpty(iso) && DateTimeOffset.TryParse(iso, out var dto) ? dto.LocalDateTime : null;

    private static long LongOf(JsonNode? n)
    {
        if (n == null) return 0;
        try { return n.GetValue<long>(); }
        catch { try { return (long)n.GetValue<double>(); } catch { return 0; } }
    }

    // Mutable per-session scratch used only while parsing one transcript.
    private sealed class SessionDayData
    {
        public string Project = "";
        public readonly List<DateTime> Times = new();
        public int Prompts;
        public int ToolCalls;
        public int SubAgents;
        public TokenTotals Tokens = TokenTotals.Zero;
        public readonly Dictionary<string, int> ToolCounts = new();
        public readonly Dictionary<string, TokenTotals> Models = new();
    }
}
