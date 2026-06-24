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

    private static string FormatActive(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return "<1m";
    }
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
}
