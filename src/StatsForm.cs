using System.Drawing.Drawing2D;

namespace ClaudeWatch;

/// <summary>
/// A dedicated window showing today's session statistics: the headline figures (sessions, active
/// time, prompts, tool calls), token totals with an equivalent API cost, an hourly activity histogram,
/// and per-project / tool-mix / model breakdowns. All figures come from <see cref="SessionStatsService"/>,
/// which derives them from the transcripts on disk — so the view is retroactive and needs nothing
/// recorded ahead of time.
///
/// The dashboard is owner-drawn onto a scrollable panel: a single <see cref="DrawDashboard"/> routine
/// both measures (Graphics null) and paints, so layout never drifts between the two passes. The report
/// is computed off the UI thread, mirroring <see cref="HistoryViewerForm"/>'s loading pattern. Standard
/// resizable chrome with a dark title bar; a single reused instance owned by the application context.
/// </summary>
internal sealed class StatsForm : Form
{
    private static readonly Color BodyBg = Color.FromArgb(18, 18, 24);
    private static readonly Color CardBg = Color.FromArgb(30, 30, 42);

    private readonly Panel _scroll;
    private readonly ContentPanel _content;

    private readonly Font _bigFont    = new("Segoe UI Semibold", 21f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _h1Font     = new("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _h2Font     = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _bodyFont   = new("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _labelFont  = new("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Bitmap? _icon = LoadEmbeddedBitmap("ClaudeWatch.icon.png");

    private StatsReport? _report;
    private bool _loading = true;

    public StatsForm()
    {
        Text          = "Session stats";
        BackColor     = BodyBg;
        ForeColor     = Theme.Fg;
        StartPosition = FormStartPosition.Manual;
        MinimumSize   = new Size(520, 480);
        DoubleBuffered = true;
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Max(MinimumSize.Width, (int)(wa.Width * 0.34));
        int h = Math.Max(MinimumSize.Height, (int)(wa.Height * 0.78));
        Size = new Size(w, h);
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

        _content = new ContentPanel(this) { Location = Point.Empty };
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BodyBg };
        _scroll.Controls.Add(_content);
        _scroll.Resize += (_, _) => Relayout();
        Controls.Add(_scroll);

        KeyPreview = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.UseDarkScrollBars(_scroll.Handle);
        RefreshStats();   // kick the first load
    }

    /// <summary>Recomputes today's report off the UI thread, then repaints. Safe to call repeatedly
    /// (e.g. each time the window is re-opened).</summary>
    public void RefreshStats()
    {
        _loading = true;
        Relayout();
        var today = DateOnly.FromDateTime(DateTime.Now);
        Task.Run(() => SessionStatsService.ReportForDay(today)).ContinueWith(t =>
        {
            var report = t.IsCompletedSuccessfully ? t.Result : StatsReport.Empty(today);
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() => { _report = report; _loading = false; Relayout(); }));
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // ── Layout ───────────────────────────────────────────────────────────────────
    // Measure with the viewport width; if the content is taller than the viewport a vertical scrollbar
    // will appear and steal width, so re-measure once at the narrower width before committing the size.
    private void Relayout()
    {
        if (!IsHandleCreated) return;
        int vw = Math.Max(MinimumSize.Width, _scroll.ClientSize.Width);
        int h = DrawDashboard(null, vw);
        if (h > _scroll.ClientSize.Height)
        {
            vw -= SystemInformation.VerticalScrollBarWidth;
            h = DrawDashboard(null, vw);
        }
        _content.Size = new Size(vw, h);
        _content.Invalidate();
    }

    // ── Dashboard rendering ────────────────────────────────────────────────────────
    private const int Pad = 22;
    private const int Gap = 12;

    // Single source of layout: when g is null this only advances the y cursor (a measure pass);
    // otherwise it paints. Returns the total content height.
    private int DrawDashboard(Graphics? g, int width)
    {
        if (g != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        int y = Pad;
        int x = Pad;
        int innerW = width - Pad * 2;

        // Title
        Draw(g, "Today", _h1Font, Theme.Title, x, y);
        if (_report is { } rpt0)
            Draw(g, rpt0.Day.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMM d"), _bodyFont, Theme.Muted,
                x + 4, y + 28);
        y += 58;

        if (_loading)
        {
            Draw(g, "Loading…", _bodyFont, Theme.Muted, x, y);
            return y + 40;
        }

        var report = _report ?? StatsReport.Empty(DateOnly.FromDateTime(DateTime.Now));

        if (report.SessionCount == 0)
        {
            Draw(g, "No sessions recorded today yet.", _bodyFont, Theme.Muted, x, y);
            return y + 40;
        }

        // ── Headline stat cards ──
        var cards = new (string value, string label)[]
        {
            (report.SessionCount.ToString(),            report.SessionCount == 1 ? "session" : "sessions"),
            (StatsFormat.Duration(report.ActiveTime),   "active"),
            (report.Prompts.ToString(),                 report.Prompts == 1 ? "prompt" : "prompts"),
            (report.ToolCalls.ToString(),               "tool calls"),
        };
        const int cardH = 70;
        int cardW = (innerW - Gap * (cards.Length - 1)) / cards.Length;
        for (int i = 0; i < cards.Length; i++)
        {
            int cx = x + i * (cardW + Gap);
            if (g != null)
            {
                FillRound(g, new Rectangle(cx, y, cardW, cardH), 8, CardBg);
                DrawCentered(g, cards[i].value, _bigFont, Theme.Title, new Rectangle(cx, y + 8, cardW, 34));
                DrawCentered(g, cards[i].label, _labelFont, Theme.Muted, new Rectangle(cx, y + 44, cardW, 18));
            }
        }
        y += cardH + 18;
        if (report.SubAgents > 0)
        {
            Draw(g, $"includes {report.SubAgents} sub-agent {(report.SubAgents == 1 ? "run" : "runs")}",
                _labelFont, Theme.Muted, x, y);
            y += 22;
        }

        // ── Tokens & cost ──
        y = SectionHeader(g, "Tokens & cost", x, y, innerW);
        var tk = report.Tokens;
        y = KeyValueRow(g, "Output",      StatsFormat.Tokens(tk.Output),     x, y, innerW);
        y = KeyValueRow(g, "Input",       StatsFormat.Tokens(tk.Input),      x, y, innerW);
        y = KeyValueRow(g, "Cache write", StatsFormat.Tokens(tk.CacheWrite), x, y, innerW);
        y = KeyValueRow(g, "Cache read",  StatsFormat.Tokens(tk.CacheRead),  x, y, innerW);
        y = KeyValueRow(g, "Total",       StatsFormat.Tokens(tk.Total),      x, y, innerW, bold: true);
        y += 6;
        string cost = report.EstimatedCost > 0
            ? $"≈ {StatsFormat.Cost(report.EstimatedCost)} equivalent API cost{(report.CostComplete ? "" : " (partial)")}"
            : "cost unavailable for these models";
        Draw(g, cost, _labelFont, Theme.Muted, x, y);
        y += 26;

        // ── Hourly activity ──
        if (report.HourlyActiveSeconds.Any(s => s > 0))
            y = HourlyHistogram(g, report.HourlyActiveSeconds, x, y, innerW);

        // ── Per-project ──
        if (report.Projects.Count > 0)
        {
            y = SectionHeader(g, "By project", x, y, innerW);
            long maxActive = Math.Max(1, report.Projects.Max(p => (long)p.ActiveTime.TotalSeconds));
            foreach (var p in report.Projects.Take(8))
            {
                string right = $"{StatsFormat.Duration(p.ActiveTime)} · {StatsFormat.Tokens(p.Tokens)}";
                y = BarRow(g, p.Project, right, (long)p.ActiveTime.TotalSeconds, maxActive, x, y, innerW, Theme.Accent);
            }
            y += 8;
        }

        // ── Tool mix ──
        if (report.Tools.Count > 0)
        {
            y = SectionHeader(g, "Tool mix", x, y, innerW);
            long maxTool = Math.Max(1, report.Tools.Max(t => (long)t.Count));
            foreach (var t in report.Tools.Take(12))
                y = BarRow(g, t.Tool, t.Count.ToString(), t.Count, maxTool, x, y, innerW, Theme.Green);
            y += 8;
        }

        // ── Model split ──
        if (report.Models.Count > 0)
        {
            y = SectionHeader(g, "By model", x, y, innerW);
            foreach (var m in report.Models)
            {
                string name = m.Model.StartsWith("claude-", StringComparison.Ordinal) ? m.Model["claude-".Length..] : m.Model;
                string right = m.Cost is { } c
                    ? $"{StatsFormat.Tokens(m.Tokens.Total)} · {StatsFormat.Cost(c)}"
                    : $"{StatsFormat.Tokens(m.Tokens.Total)} · —";
                y = KeyValueRow(g, name, right, x, y, innerW);
            }
            y += 4;
        }

        return y + Pad;
    }

    // ── Drawing helpers (all no-op when g is null, but advance the caller's y identically) ──
    private void Draw(Graphics? g, string text, Font font, Color color, int x, int y)
    {
        if (g != null)
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private static void DrawCentered(Graphics g, string text, Font font, Color color, Rectangle r) =>
        TextRenderer.DrawText(g, text, font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

    private int SectionHeader(Graphics? g, string title, int x, int y, int innerW)
    {
        Draw(g, title, _h2Font, Theme.Fg, x, y);
        if (g != null)
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, x, y + 24, x + innerW, y + 24);
        return y + 34;
    }

    private int KeyValueRow(Graphics? g, string key, string value, int x, int y, int innerW, bool bold = false)
    {
        var f = bold ? _h2Font : _bodyFont;
        Draw(g, key, _bodyFont, bold ? Theme.Fg : Theme.Muted, x, y);
        if (g != null)
            TextRenderer.DrawText(g, value, f, new Rectangle(x, y, innerW, 20), Theme.Fg,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return y + 24;
    }

    // A labelled horizontal bar: name on the left, a proportional bar filling the row, value at the right.
    private int BarRow(Graphics? g, string label, string right, long value, long max, int x, int y, int innerW, Color color)
    {
        const int rowH = 26;
        if (g != null)
        {
            var track = new Rectangle(x, y + 3, innerW, rowH - 8);
            FillRound(g, track, 5, Theme.Track);
            int barW = (int)(innerW * Math.Clamp(value / (double)max, 0, 1));
            if (barW > 4)
                FillRound(g, new Rectangle(x, y + 3, barW, rowH - 8), 5, color);

            // Label and value drawn over the bar, with a subtle shadow-free contrast colour.
            TextRenderer.DrawText(g, label, _bodyFont, new Rectangle(x + 8, y, innerW - 90, rowH), Theme.Title,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, right, _bodyFont, new Rectangle(x, y, innerW - 8, rowH), Theme.Title,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        return y + rowH;
    }

    private int HourlyHistogram(Graphics? g, int[] hourly, int x, int y, int innerW)
    {
        y = SectionHeader(g, "Active by hour", x, y, innerW);
        const int areaH = 64;
        int max = Math.Max(1, hourly.Max());
        double cellW = innerW / 24.0;
        if (g != null)
        {
            for (int hr = 0; hr < 24; hr++)
            {
                int bx = x + (int)(hr * cellW);
                int bw = Math.Max(2, (int)cellW - 3);
                int bh = (int)(areaH * (hourly[hr] / (double)max));
                if (bh < 2 && hourly[hr] > 0) bh = 2;
                if (bh > 0)
                    FillRound(g, new Rectangle(bx, y + (areaH - bh), bw, bh), 2, Theme.Accent);
            }
            // Baseline + a few hour ticks.
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, x, y + areaH + 1, x + innerW, y + areaH + 1);
            foreach (int hr in new[] { 0, 6, 12, 18 })
                TextRenderer.DrawText(g, $"{hr:00}", _labelFont, new Point(x + (int)(hr * cellW), y + areaH + 4),
                    Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        return y + areaH + 24;
    }

    private static void FillRound(Graphics g, Rectangle r, int radius, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        using var path = new GraphicsPath();
        if (d <= 1)
        {
            path.AddRectangle(r);
        }
        else
        {
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
        }
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static Bitmap? LoadEmbeddedBitmap(string resourceName)
    {
        try
        {
            using var stream = typeof(StatsForm).Assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null;
        }
        catch { return null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bigFont.Dispose();
            _h1Font.Dispose();
            _h2Font.Dispose();
            _bodyFont.Dispose();
            _labelFont.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The scrollable surface the dashboard is painted onto. Double-buffered so the custom
    /// paint doesn't flicker; it simply forwards painting back to the owning form.</summary>
    private sealed class ContentPanel : Panel
    {
        private readonly StatsForm _owner;
        public ContentPanel(StatsForm owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            BackColor = BodyBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _owner.DrawDashboard(e.Graphics, Width);
        }
    }
}
