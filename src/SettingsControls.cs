namespace ClaudeWatch;

using System.Drawing.Drawing2D;

/// <summary>Shared dark palette for the settings window and its custom controls. Mirrors the
/// overlay's colours so the two surfaces read as one app.</summary>
internal static class Theme
{
    public static readonly Color FormBg      = Color.FromArgb(24, 24, 32);
    public static readonly Color Fg          = Color.FromArgb(225, 225, 235);
    public static readonly Color Title       = Color.FromArgb(245, 245, 250);
    public static readonly Color Muted       = Color.FromArgb(140, 140, 160);
    public static readonly Color Accent      = Color.FromArgb(96, 165, 250);
    public static readonly Color AccentHover = Color.FromArgb(147, 197, 253);
    public static readonly Color Border      = Color.FromArgb(45, 45, 60);
    public static readonly Color ButtonBg    = Color.FromArgb(45, 45, 60);
    public static readonly Color ButtonHover = Color.FromArgb(60, 60, 80);
    public static readonly Color Danger      = Color.FromArgb(248, 113, 113);

    // Usage bar / status palette (same thresholds the overlay uses).
    public static readonly Color Green  = Color.FromArgb(34, 197, 94);
    public static readonly Color Yellow = Color.FromArgb(250, 204, 21);
    public static readonly Color Orange = Color.FromArgb(251, 146, 60);
    public static readonly Color Red    = Color.FromArgb(239, 68, 68);
    public static readonly Color Track          = Color.FromArgb(38, 38, 52);
    public static readonly Color ExpectedMark  = Color.FromArgb(180, 180, 195);

    public static Color ModeColor(PermissionMode m) => m switch
    {
        PermissionMode.AcceptEdits => Color.FromArgb(167, 139, 250),
        PermissionMode.Plan        => Color.FromArgb(96, 165, 250),
        PermissionMode.Auto        => Color.FromArgb(250, 204, 21),
        PermissionMode.Bypass      => Color.FromArgb(239, 68, 68),
        _                          => Color.Transparent,
    };

    public static Color UsageColor(double pct) => pct switch
    {
        < 50 => Green,
        < 75 => Yellow,
        < 90 => Orange,
        _    => Red,
    };

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R * (1 - t) + b.R * t),
        (int)(a.G * (1 - t) + b.G * t),
        (int)(a.B * (1 - t) + b.B * t));
}

/// <summary>A Material-style on/off switch: a rounded pill track with a sliding knob.</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _on;

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size           = new Size(46, 26);
        Cursor         = Cursors.Hand;
        DoubleBuffered = true;
        TabStop        = false;
        BackColor      = Theme.FormBg;
    }

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set the state without raising <see cref="CheckedChanged"/> (used when syncing to
    /// external state, so we don't re-trigger the install/uninstall the change handler runs).</summary>
    public void SetCheckedSilently(bool value)
    {
        if (_on == value) return;
        _on = value;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
            Checked = !Checked;
        base.OnMouseClick(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(1, 1, Width - 2, Height - 2);
        Color track = _on ? Theme.Green : Color.FromArgb(70, 70, 88);
        if (!Enabled) track = Theme.Blend(track, BackColor, 0.5f);

        using (var path = Pill(rect))
        using (var brush = new SolidBrush(track))
            g.FillPath(brush, path);

        int knobD = rect.Height - 6;
        int knobX = _on ? rect.Right - knobD - 3 : rect.Left + 3;
        Color knob = Color.FromArgb(235, 235, 245);
        if (!Enabled) knob = Theme.Blend(knob, BackColor, 0.4f);
        using var kb = new SolidBrush(knob);
        g.FillEllipse(kb, knobX, rect.Top + 3, knobD, knobD);
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        int d = r.Height;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
}

/// <summary>A small indeterminate spinner: a rotating accent arc on a faint track. Only animates
/// (and only consumes a timer) while <see cref="Spinning"/> is true and the control is visible.</summary>
internal sealed class Spinner : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private int _angle;
    private bool _spinning;

    public Spinner()
    {
        Size           = new Size(18, 18);
        DoubleBuffered = true;
        TabStop        = false;
        BackColor      = Theme.FormBg;
        Visible        = false;
        _timer = new System.Windows.Forms.Timer { Interval = 60 };
        _timer.Tick += (_, _) => { _angle = (_angle + 30) % 360; Invalidate(); };
    }

    /// <summary>Start/stop the animation. Also toggles visibility so the spinner only shows while busy.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Spinning
    {
        get => _spinning;
        set
        {
            if (_spinning == value) return;
            _spinning = value;
            Visible   = value;
            if (value) _timer.Start(); else _timer.Stop();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_spinning) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float pad = 2.5f;
        var rect = new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2);
        float thickness = Math.Max(2f, Width / 9f);

        using var track = new Pen(Theme.Border, thickness);
        g.DrawArc(track, rect, 0, 360);

        using var arc = new Pen(Theme.Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, rect, _angle, 100);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Renders the 5-hour ("Session") and 7-day ("Weekly") usage windows as labelled
/// progress bars, matching the overlay's bars for consistency. Shows a placeholder line when
/// usage tracking is off or no reading is available yet.</summary>
internal sealed class UsageBarsControl : Control
{
    private const int BarRowHeight = 24;
    private const int CaptionW     = 64;
    private const int PctW         = 44;
    private const int TrackH       = 8;

    private UsageInfo _usage = UsageInfo.Empty;
    private bool _on = true;
    private bool _showExpectedRate = true;

    public UsageBarsControl()
    {
        DoubleBuffered = true;
        BackColor      = Theme.FormBg;
        Height         = 74;
    }

    public void SetUsage(UsageInfo usage)         { _usage = usage; Invalidate(); }
    public void SetOn(bool on)                    { _on = on; Invalidate(); }
    public void SetShowExpectedRate(bool show)    { _showExpectedRate = show; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var capFont    = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var pctFont    = new Font("Segoe UI", 8.5f, FontStyle.Bold,    GraphicsUnit.Point);
        using var footerFont = new Font("Segoe UI", 8f,   FontStyle.Regular, GraphicsUnit.Point);
        using var mutedBrush = new SolidBrush(Theme.Muted);

        if (!_on)
        {
            g.DrawString("Usage tracking is off — enable it above to see your limits.",
                footerFont, mutedBrush, 0, 4);
            return;
        }

        if (_usage.LastUpdated == DateTime.MinValue && _usage.FiveHourPercent == null)
        {
            g.DrawString(_usage.Error ?? "No usage data yet.", footerFont, mutedBrush, 0, 4);
            return;
        }

        bool stale = _usage.IsStale(DateTime.Now);
        double? sessionExpected = _showExpectedRate ? ElapsedPercent(_usage.FiveHourResetsAt, TimeSpan.FromHours(5)) : null;
        double? weeklyExpected  = _showExpectedRate ? ElapsedPercent(_usage.SevenDayResetsAt, TimeSpan.FromDays(7))  : null;
        DrawBar(g, 0,            "Session", _usage.FiveHourPercent, sessionExpected, stale, capFont, pctFont);
        DrawBar(g, BarRowHeight, "Weekly",  _usage.SevenDayPercent, weeklyExpected,  stale, capFont, pctFont);

        // Footer: last-updated / staleness plus reset times.
        var parts = new List<string>
        {
            _usage.Ok ? $"Updated {_usage.LastUpdated:h:mm tt}" : $"Stale — {_usage.Error}",
        };
        if (_usage.FiveHourResetsAt is { } fr) parts.Add($"5h resets {fr:ddd h:mm tt}");
        if (_usage.SevenDayResetsAt is { } wr) parts.Add($"weekly resets {wr:ddd h:mm tt}");
        g.DrawString(string.Join("   ·   ", parts), footerFont, mutedBrush, 0, BarRowHeight * 2 + 2);
    }

    private void DrawBar(Graphics g, int rowTop, string caption, double? percent,
                         double? expectedPct, bool stale, Font capFont, Font pctFont)
    {
        int midY = rowTop + BarRowHeight / 2;

        Color capColor = stale ? Theme.Blend(Theme.Muted, BackColor, 0.5f) : Theme.Muted;
        using (var capBrush = new SolidBrush(capColor))
        {
            var capSz = g.MeasureString(caption, capFont);
            g.DrawString(caption, capFont, capBrush, 0, midY - capSz.Height / 2);
        }

        int trackLeft  = CaptionW;
        int trackRight = Width - PctW;
        int trackW     = Math.Max(0, trackRight - trackLeft);
        int trackY     = midY - TrackH / 2;

        Color trackColor = stale ? Theme.Blend(Theme.Track, BackColor, 0.4f) : Theme.Track;
        using (var trackBrush = new SolidBrush(trackColor))
            FillRoundedBar(g, trackBrush, trackLeft, trackY, trackW, TrackH);

        string pctText;
        Color textColor;
        if (percent is { } p)
        {
            double clamped = Math.Clamp(p, 0, 100);
            Color barColor = Theme.UsageColor(clamped);
            if (stale) barColor = Theme.Blend(barColor, BackColor, 0.5f);

            int fillW = (int)Math.Round(trackW * clamped / 100.0);
            if (fillW > 0)
                using (var fillBrush = new SolidBrush(barColor))
                    FillRoundedBar(g, fillBrush, trackLeft, trackY, fillW, TrackH);

            pctText   = $"{(int)Math.Round(clamped)}%";
            textColor = barColor;
        }
        else
        {
            pctText   = "—";
            textColor = capColor;
        }

        // Expected-rate marker: thin vertical bar at the elapsed-time position.
        if (expectedPct is { } ep && trackW > 0)
        {
            int markerX = trackLeft + (int)Math.Round(trackW * ep / 100.0);
            Color markerColor = stale ? Theme.Blend(Theme.ExpectedMark, BackColor, 0.5f) : Theme.ExpectedMark;
            using var markerBrush = new SolidBrush(markerColor);
            g.FillRectangle(markerBrush, markerX - 1, trackY - 1, 2, TrackH + 2);
        }

        using var textBrush = new SolidBrush(textColor);
        var txtSz = g.MeasureString(pctText, pctFont);
        g.DrawString(pctText, pctFont, textBrush, Width - txtSz.Width, midY - txtSz.Height / 2);
    }

    private static double? ElapsedPercent(DateTime? resetsAt, TimeSpan window)
    {
        if (resetsAt is null) return null;
        var elapsed = DateTime.Now - (resetsAt.Value - window);
        return Math.Clamp(elapsed.TotalSeconds / window.TotalSeconds * 100.0, 0, 100);
    }

    private static void FillRoundedBar(Graphics g, Brush brush, int x, int y, int w, int h)
    {
        if (w <= 0) return;
        int r = Math.Min(h / 2, w / 2);
        if (r <= 0) { g.FillRectangle(brush, x, y, w, h); return; }
        using var path = new GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

/// <summary>A legend listing each permission mode with the coloured fast-forward badge the
/// overlay draws for it, so users can connect the dots between a mode and its on-screen icon.</summary>
internal sealed class ModeLegend : Control
{
    private const int RowH = 24;

    private static readonly (PermissionMode mode, string label)[] Modes =
    [
        (PermissionMode.Normal,      "Normal — no badge shown"),
        (PermissionMode.Plan,        "Plan mode"),
        (PermissionMode.AcceptEdits, "Accept edits"),
        (PermissionMode.Auto,        "Auto-accept"),
        (PermissionMode.Bypass,      "Bypass permissions"),
    ];

    public ModeLegend()
    {
        DoubleBuffered = true;
        BackColor      = Theme.FormBg;
        Height         = Modes.Length * RowH;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var font  = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        using var fg    = new SolidBrush(Theme.Fg);
        using var muted = new SolidBrush(Theme.Muted);

        int y = 0;
        foreach (var (mode, label) in Modes)
        {
            int midY = y + RowH / 2;

            if (mode == PermissionMode.Normal)
            {
                // No badge — a small dash placeholder keeps the labels aligned.
                using var dashPen = new Pen(Theme.Muted, 1.6f);
                g.DrawLine(dashPen, 6, midY, 16, midY);
            }
            else
            {
                DrawModeBadge(g, mode, 4, midY);
            }

            var sz = g.MeasureString(label, font);
            g.DrawString(label, font, mode == PermissionMode.Normal ? muted : fg,
                28, midY - sz.Height / 2);

            y += RowH;
        }
    }

    // Same double-chevron (fast-forward) glyph the overlay draws, in the mode's colour.
    private static void DrawModeBadge(Graphics g, PermissionMode mode, int x, int midY)
    {
        using var brush = new SolidBrush(Theme.ModeColor(mode));
        g.FillPolygon(brush, new[] { new Point(x,     midY - 5), new Point(x + 6,  midY), new Point(x,     midY + 5) });
        g.FillPolygon(brush, new[] { new Point(x + 7, midY - 5), new Point(x + 13, midY), new Point(x + 7, midY + 5) });
    }
}
