using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeWatch;

/// <summary>
/// Floating always-on-top status widget.
/// Compact bar by default; click the header to expand to per-session rows.
/// Drag the header to reposition. Right-click for the exit menu.
/// Clicking a session row focuses the terminal running that session.
/// </summary>
internal sealed class OverlayForm : Form
{
    // ── Layout ────────────────────────────────────────────────────────────────
    private const int FormWidth       = 280;
    private const int HeaderHeight    = 44;
    private const int BarRowHeight    = 18;
    private const int UsageStripHeight= 50;  // two usage bars + padding, shown only when expanded
    private const int RowHeight        = 46;
    private const int SubRowHeight      = 24;
    private const int SubIndent         = 22;
    private const int HorizPad          = 12;
    private const int Corner            = 10;

    // Dense mode: a narrow strip hugging the right screen edge that expands on hover.
    private const int DenseClosedWidth = 44;
    private const int DenseTopPad      = 8;
    private const int DenseIconSize    = 22;
    private const int DenseGap         = 6;
    private const int DenseRowHeight   = 22;
    private const int DenseBottomPad   = 8;

    // Header right-side glyphs (the dense toggle icon and the expand chevron).
    private const int ChevronBoxW = 14;
    private const int IconBoxW    = 16;
    private const int IconBoxH    = 16;
    private const int IconGap     = 6;

    // ── Palette ───────────────────────────────────────────────────────────────
    private static readonly Color BgColor        = Color.FromArgb(15,  15,  20);
    private static readonly Color BorderNormal   = Color.FromArgb(45,  45,  60);
    private static readonly Color BorderAttention= Color.FromArgb(251, 146, 60);
    private static readonly Color RunningColor      = Color.FromArgb(34,  197, 94);
    private static readonly Color AttentionColor   = Color.FromArgb(251, 146, 60);
    private static readonly Color AwaitingColor    = Color.FromArgb(250, 204, 21);
    private static readonly Color IdleColor        = Color.FromArgb(100, 116, 139);
    private static readonly Color FgColor        = Color.FromArgb(225, 225, 235);
    private static readonly Color MutedColor     = Color.FromArgb(110, 110, 130);
    private static readonly Color SepColor       = Color.FromArgb(35,  35,  50);
    private static readonly Color RowHoverColor  = Color.FromArgb(25,  25,  38);
    private static readonly Color SubAgentColor  = Color.FromArgb(56,  189, 248);
    private static readonly Color TreeLineColor  = Color.FromArgb(55,  55,  72);
    private static readonly Color UsageRedColor  = Color.FromArgb(239, 68,  68);
    private static readonly Color UsageTrackColor= Color.FromArgb(38,  38,  52);

    // ── State ─────────────────────────────────────────────────────────────────
    // A flat render list of parent-session rows interleaved with their running sub-agent
    // child rows, in draw order. Built from the sessions on each update.
    private readonly record struct DisplayRow(ClaudeSession Session, SubAgent? Sub)
    {
        public bool IsSubAgent => Sub != null;
    }

    private IReadOnlyList<ClaudeSession> _sessions = [];
    private List<DisplayRow> _rows = [];
    private bool  _expanded;
    private bool  _dragging;
    private Point _dragStartScreen;
    private Point _formStartLoc;
    private bool  _wasDrag;
    private int   _hoveredRow = -1;
    private bool  _attentionFlash;

    // Dense mode: an alternate, out-of-the-way presentation with its own coordinates.
    // _dense toggles the whole mode; _denseOpen is the hover-expanded popup within it.
    // Floating and dense each keep their own position: _floatingLoc holds the floating
    // location while we're in dense mode, and _denseY holds the dense strip's Y (its X is
    // always locked to the right screen edge). Nothing here is persisted across restarts.
    private bool  _dense;
    private bool  _denseOpen;
    private int   _denseY;
    private bool  _denseYInit;
    private Point _floatingLoc;

    private UsageInfo _usage = UsageInfo.Empty;
    private bool _usageEnabled = true;
    private bool _inUsageStrip;
    private readonly UsageTooltipForm _usageTooltip = new();

    // Top of the session rows when expanded: header, plus the usage strip when it's shown.
    private int RowsTop => HeaderHeight + (_usageEnabled ? UsageStripHeight : 0);

    private readonly System.Windows.Forms.Timer _flashTimer;
    private readonly System.Windows.Forms.Timer _flashStopTimer;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Windows.Forms.Timer _usageHoverTimer;
    private readonly System.Windows.Forms.Timer _denseCloseTimer;

    // The claude-watch icon, shown atop the dense strip purely for flair. Null if unavailable.
    private readonly Bitmap? _icon = LoadEmbeddedBitmap("ClaudeWatch.icon.png");

    // Is the full session body (usage bars + rows) currently on screen? In floating mode that's
    // the expanded state; in dense mode it's the hover-opened popup.
    private bool ShowFullPanel => _dense ? _denseOpen : _expanded;

    public event EventHandler? ExitRequested;
    public event Action<string>? SessionFocused;

    // ── Construction ──────────────────────────────────────────────────────────
    public OverlayForm()
    {
        FormBorderStyle  = FormBorderStyle.None;
        ShowInTaskbar    = false;
        TopMost          = true;
        AllowTransparency = true;
        BackColor        = Color.Black;
        TransparencyKey  = Color.Black;
        DoubleBuffered   = true;
        StartPosition    = FormStartPosition.Manual;

        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location   = new Point(screen.Right - FormWidth - 16, screen.Top + 16);
        ClientSize = new Size(FormWidth, HeaderHeight);

        _flashTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _flashTimer.Tick += (_, _) => { _attentionFlash = !_attentionFlash; Invalidate(); };

        _flashStopTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _flashStopTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flashStopTimer.Stop();
            _attentionFlash = false;
            Invalidate();
        };

        // Ticks the elapsed run-time labels while the panel is expanded with a running session.
        _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _tickTimer.Tick += (_, _) => Invalidate();

        // One-shot dwell timer: pops the usage tooltip 150ms after the cursor enters the bar strip.
        _usageHoverTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _usageHoverTimer.Tick += (_, _) =>
        {
            _usageHoverTimer.Stop();
            if (_inUsageStrip && !_dragging)
                ShowUsageTooltip();
        };

        // Collapses the hover-opened dense popup once the cursor has been away for 2 seconds.
        // Re-validated against the live cursor position so a quick out-and-back keeps it open.
        _denseCloseTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _denseCloseTimer.Tick += (_, _) =>
        {
            if (_dense && _denseOpen && !Bounds.Contains(Cursor.Position))
                CloseDensePopup();
            else
                _denseCloseTimer.Stop();
        };

        ContextMenuStrip = BuildContextMenu();
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public void UpdateSessions(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;

        var rows = new List<DisplayRow>();
        foreach (var session in sessions.OrderBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new DisplayRow(session, null));
            foreach (var sub in session.SubAgents)
                rows.Add(new DisplayRow(session, sub));
        }
        _rows = rows;

        // Auto-collapse when all sessions disappear
        if (sessions.Count == 0)
            _expanded = false;

        // Stop flashing if nothing needs attention anymore
        if (_attentionFlash && _sessions.All(s => s.Status != SessionStatus.NeedsAttention && s.Status != SessionStatus.AwaitingInput))
        {
            _flashTimer.Stop();
            _flashStopTimer.Stop();
            _attentionFlash = false;
        }

        RelayoutWindow();
        UpdateTickTimer();
        Invalidate();
    }

    // Latest account-wide rate-limit usage, rendered as the two bars under the banner.
    public void UpdateUsage(UsageInfo usage)
    {
        _usage = usage;
        if (_usageEnabled && _usageTooltip.Visible)
            ShowUsageTooltip();  // refresh the tooltip in place if it's currently open
        Invalidate();
    }

    // Toggles the usage strip on/off. When off, the strip is hidden and reserves no space.
    public void SetUsageEnabled(bool enabled)
    {
        if (_usageEnabled == enabled) return;
        _usageEnabled = enabled;
        if (!enabled)
        {
            _usageHoverTimer.Stop();
            _inUsageStrip = false;
            HideUsageTooltip();
        }
        RelayoutWindow();
        Invalidate();
    }

    // The run-time labels only need a per-second repaint when they're actually on screen.
    private void UpdateTickTimer()
    {
        bool need = ShowFullPanel && _sessions.Any(s => s.Status == SessionStatus.Running);
        if (need && !_tickTimer.Enabled)
            _tickTimer.Start();
        else if (!need && _tickTimer.Enabled)
            _tickTimer.Stop();
    }

    public void TriggerAttention()
    {
        // Auto-surface the project that needs attention. In dense mode that means popping the
        // hover panel open (it auto-closes after 2s); otherwise expand the floating panel.
        if (_dense)
        {
            OpenDensePopup();
        }
        else if (!_expanded && _rows.Count > 0)
        {
            _expanded = true;
            RelayoutWindow();
            UpdateTickTimer();
        }

        _attentionFlash = true;
        _flashTimer.Start();
        _flashStopTimer.Stop();
        _flashStopTimer.Start();
        Invalidate();
    }

    // ── Layout ────────────────────────────────────────────────────────────────
    // Pixel height of a single render row (sub-agent rows are shorter than session rows).
    private static int HeightOf(DisplayRow row) => row.IsSubAgent ? SubRowHeight : RowHeight;

    // Y offset (from the top of the form) of the row at the given index.
    private int RowTop(int index)
    {
        int top = RowsTop;
        for (int i = 0; i < index; i++)
            top += HeightOf(_rows[i]);
        return top;
    }

    // Owns the window's size and position for every mode/state. Floating keeps whatever location
    // it was dragged to; both dense states dock to the right screen edge at the remembered _denseY.
    private void RelayoutWindow()
    {
        if (_dragging) return;  // never fight an in-progress drag

        if (_dense)
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            int w  = _denseOpen ? FormWidth : DenseClosedWidth;
            int h  = _denseOpen ? FullPanelHeight() : DenseStripHeight();
            Location   = new Point(wa.Right - w, ClampDenseY(_denseY, h, wa));
            ClientSize = new Size(w, h);
        }
        else
        {
            int h = _expanded ? FullPanelHeight() : HeaderHeight;
            if (ClientSize.Height != h || ClientSize.Width != FormWidth)
                ClientSize = new Size(FormWidth, h);
        }
    }

    // Height of the full panel (header + optional usage strip + all session rows).
    private int FullPanelHeight()
    {
        int h = HeaderHeight;
        if (_rows.Count > 0)
        {
            if (_usageEnabled)
                h += UsageStripHeight;  // usage bars sit between the header and the rows
            foreach (var row in _rows)
                h += HeightOf(row);
            h += 2;
        }
        return h;
    }

    // Height of the closed dense strip: the icon plus one row per non-zero status.
    private int DenseStripHeight()
    {
        int visible = DenseStatusCounts().Count(c => c.count > 0);
        int h = DenseTopPad + DenseIconSize;
        if (visible > 0)
            h += DenseGap + visible * DenseRowHeight;
        return h + DenseBottomPad;
    }

    // Keeps the dense strip fully on screen vertically as its height changes with the session count.
    private static int ClampDenseY(int y, int height, Rectangle wa) =>
        Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - height));

    // ── Dense mode transitions ─────────────────────────────────────────────────
    public void ToggleDense()
    {
        if (_dense) ExitDense();
        else        EnterDense();
    }

    private void EnterDense()
    {
        if (_dense) return;
        _floatingLoc = Location;                       // remember where floating lives
        if (!_denseYInit) { _denseY = Location.Y; _denseYInit = true; }
        _dense = true;
        _denseOpen = false;
        _hoveredRow = -1;
        HideUsageTooltip();
        RelayoutWindow();
        UpdateTickTimer();
        Invalidate();
    }

    private void ExitDense()
    {
        if (!_dense) return;
        _dense = false;
        _denseOpen = false;
        _denseCloseTimer.Stop();
        HideUsageTooltip();
        Location = _floatingLoc;                       // restore the floating position
        RelayoutWindow();
        UpdateTickTimer();
        Invalidate();
    }

    private void OpenDensePopup()
    {
        if (!_dense || _denseOpen) return;
        _denseOpen = true;
        RelayoutWindow();
        UpdateTickTimer();
        Invalidate();
    }

    private void CloseDensePopup()
    {
        if (!_dense || !_denseOpen) return;
        _denseOpen = false;
        _denseCloseTimer.Stop();
        _hoveredRow = -1;
        HideUsageTooltip();
        RelayoutWindow();
        UpdateTickTimer();
        Invalidate();
    }

    // ── Painting ──────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var path = RoundedRect(bounds, Corner);

        using (var bg = new SolidBrush(BgColor))
            g.FillPath(bg, path);

        var borderColor = _attentionFlash ? BorderAttention : BorderNormal;
        using (var pen = new Pen(borderColor, 1.5f))
            g.DrawPath(pen, path);

        if (_dense && !_denseOpen)
        {
            DrawDenseStrip(g);
            return;
        }

        DrawHeader(g);

        if (ShowFullPanel)
        {
            if (_usageEnabled)
                DrawUsageBars(g);
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(g, i);
        }
    }

    private void DrawHeader(Graphics g)
    {
        int midY = HeaderHeight / 2;

        using var labelFont = new Font("Segoe UI", 8f,  FontStyle.Regular, GraphicsUnit.Point);
        using var countFont = new Font("Segoe UI", 9f,  FontStyle.Bold,    GraphicsUnit.Point);
        using var chevFont  = new Font("Segoe UI", 7.5f,                   GraphicsUnit.Point);
        using var muted     = new SolidBrush(MutedColor);

        // "Claude" label
        var labelSz = g.MeasureString("Claude", labelFont);
        g.DrawString("Claude", labelFont, muted, HorizPad, midY - labelSz.Height / 2);

        // Separator dot
        int sepX = HorizPad + (int)labelSz.Width + 4;
        g.FillEllipse(muted, sepX, midY - 2, 4, 4);
        int x = sepX + 10;

        if (_sessions.Count == 0)
        {
            var sz = g.MeasureString("no sessions", labelFont);
            g.DrawString("no sessions", labelFont, muted, x, midY - sz.Height / 2);
        }
        else
        {
            int running   = _sessions.Count(s => s.Status == SessionStatus.Running);
            int attention = _sessions.Count(s => s.Status == SessionStatus.NeedsAttention);
            int awaiting  = _sessions.Count(s => s.Status == SessionStatus.AwaitingInput);
            int idle      = _sessions.Count(s => s.Status == SessionStatus.Idle);

            x = DrawStatusPill(g, x, midY, awaiting,  AwaitingColor,  AwaitingColor,  countFont);
            x = DrawStatusPill(g, x, midY, running,   RunningColor,   FgColor,        countFont);
            x = DrawStatusPill(g, x, midY, attention, AttentionColor, AttentionColor, countFont);

            if (running == 0 && attention == 0 && awaiting == 0)
                DrawStatusPill(g, x, midY, idle, IdleColor, IdleColor, countFont);
        }

        // Dense toggle icon (always present). Reversed (|<-) while in dense mode, where clicking it
        // leaves dense mode; plain (->|) while floating, where clicking it enters dense mode.
        DrawSideCollapseIcon(g, SideIconRect(), reversed: _dense);

        // Expand chevron — floating mode only (hidden in dense), and only when there's something to expand.
        if (!_dense && _sessions.Count > 0)
        {
            var chevron = _expanded ? "▲" : "▼";
            var chSz    = g.MeasureString(chevron, chevFont);
            g.DrawString(chevron, chevFont, muted,
                ClientSize.Width - HorizPad - chSz.Width,
                midY - chSz.Height / 2);
        }
    }

    // Hit-box for the dense toggle glyph. In dense mode (no chevron) it takes the rightmost slot;
    // in floating mode it sits just left of the chevron column.
    private Rectangle SideIconRect()
    {
        int top   = (HeaderHeight - IconBoxH) / 2;
        int right = ClientSize.Width - HorizPad - (_dense ? 0 : ChevronBoxW + IconGap);
        return new Rectangle(right - IconBoxW, top, IconBoxW, IconBoxH);
    }

    private static int DrawStatusPill(Graphics g, int x, int midY, int count,
                                      Color dotColor, Color textColor, Font font)
    {
        if (count == 0) return x;

        using var dotBrush  = new SolidBrush(dotColor);
        using var textBrush = new SolidBrush(textColor);

        g.FillEllipse(dotBrush, x, midY - 4, 8, 8);
        x += 12;

        var label = count.ToString();
        var sz    = g.MeasureString(label, font);
        g.DrawString(label, font, textBrush, x, midY - sz.Height / 2);
        return x + (int)sz.Width + 8;
    }

    // ── Dense strip ─────────────────────────────────────────────────────────────
    // The four statuses in top-to-bottom display order, paired with their dot colour.
    private (Color color, int count)[] DenseStatusCounts() =>
    [
        (RunningColor,   _sessions.Count(s => s.Status == SessionStatus.Running)),
        (AwaitingColor,  _sessions.Count(s => s.Status == SessionStatus.AwaitingInput)),
        (AttentionColor, _sessions.Count(s => s.Status == SessionStatus.NeedsAttention)),
        (IdleColor,      _sessions.Count(s => s.Status == SessionStatus.Idle)),
    ];

    // The closed dense view: the claude-watch icon, then one centered "dot + count" row for each
    // status that has at least one session. With no sessions at all, only the icon shows.
    private void DrawDenseStrip(Graphics g)
    {
        int cx = ClientSize.Width / 2;

        if (_icon != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_icon, new Rectangle(cx - DenseIconSize / 2, DenseTopPad, DenseIconSize, DenseIconSize));
        }

        using var countFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        const int Dot = 8;
        int y = DenseTopPad + DenseIconSize + DenseGap;

        foreach (var (color, count) in DenseStatusCounts())
        {
            if (count == 0) continue;

            var label  = count.ToString();
            var sz     = g.MeasureString(label, countFont);
            int groupW = Dot + 4 + (int)sz.Width;
            int startX = cx - groupW / 2;
            int midY   = y + DenseRowHeight / 2;

            using (var brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, startX, midY - Dot / 2, Dot, Dot);
                g.DrawString(label, countFont, brush, startX + Dot + 4, midY - sz.Height / 2);
            }
            y += DenseRowHeight;
        }
    }

    // Draws the dense toggle glyph: an arrow into a pipe. Plain "->|" collapses to the right edge
    // (enter dense); the reversed "|<-" expands back out (leave dense). Pure GDI so it themes and
    // scales with the rest of the header, like the chevrons and the mode badge.
    private static void DrawSideCollapseIcon(Graphics g, Rectangle r, bool reversed)
    {
        using var pen = new Pen(MutedColor, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        int midY     = r.Top + r.Height / 2;
        int pad      = 3;
        int left     = r.Left + pad;
        int right    = r.Right - pad;
        int headLen  = 4;

        if (!reversed)
        {
            int pipeX    = right;
            int shaftEnd = pipeX - 2;
            g.DrawLine(pen, left, midY, shaftEnd, midY);                       // shaft
            g.DrawLine(pen, shaftEnd - headLen, midY - headLen, shaftEnd, midY); // arrowhead
            g.DrawLine(pen, shaftEnd - headLen, midY + headLen, shaftEnd, midY);
            g.DrawLine(pen, pipeX, r.Top + pad, pipeX, r.Bottom - pad);        // pipe
        }
        else
        {
            int pipeX    = left;
            int shaftEnd = pipeX + 2;
            g.DrawLine(pen, right, midY, shaftEnd, midY);                      // shaft
            g.DrawLine(pen, shaftEnd + headLen, midY - headLen, shaftEnd, midY); // arrowhead
            g.DrawLine(pen, shaftEnd + headLen, midY + headLen, shaftEnd, midY);
            g.DrawLine(pen, pipeX, r.Top + pad, pipeX, r.Bottom - pad);        // pipe
        }
    }

    private static Bitmap? LoadEmbeddedBitmap(string resourceName)
    {
        try
        {
            using var stream = typeof(OverlayForm).Assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null;
        }
        catch { return null; }
    }

    // ── Usage bars ─────────────────────────────────────────────────────────────
    // Two always-visible bars below the banner: the 5-hour ("Session") and 7-day ("Weekly")
    // rate-limit windows. Dimmed when the data is stale/unavailable.
    private void DrawUsageBars(Graphics g)
    {
        bool stale = _usage.IsStale(DateTime.Now);

        using var capFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var pctFont = new Font("Segoe UI", 7.5f, FontStyle.Bold,    GraphicsUnit.Point);

        int top = HeaderHeight + 2;
        DrawUsageBar(g, top,                 "Session", _usage.FiveHourPercent, stale, capFont, pctFont);
        DrawUsageBar(g, top + BarRowHeight,  "Weekly",  _usage.SevenDayPercent, stale, capFont, pctFont);
    }

    private void DrawUsageBar(Graphics g, int rowTop, string caption, double? percent,
                              bool stale, Font capFont, Font pctFont)
    {
        const int CaptionW = 46;
        const int PctW     = 34;
        const int TrackH   = 7;

        int midY = rowTop + BarRowHeight / 2;

        // Caption (left)
        Color capColor = stale ? Blend(MutedColor, BgColor, 0.5f) : MutedColor;
        using (var capBrush = new SolidBrush(capColor))
        {
            var capSz = g.MeasureString(caption, capFont);
            g.DrawString(caption, capFont, capBrush, HorizPad, midY - capSz.Height / 2);
        }

        // Track
        int trackLeft  = HorizPad + CaptionW;
        int trackRight = ClientSize.Width - HorizPad - PctW;
        int trackW     = Math.Max(0, trackRight - trackLeft);
        int trackY     = midY - TrackH / 2;

        Color trackColor = stale ? Blend(UsageTrackColor, BgColor, 0.4f) : UsageTrackColor;
        using (var trackBrush = new SolidBrush(trackColor))
            FillRoundedBar(g, trackBrush, trackLeft, trackY, trackW, TrackH);

        // Fill + percentage text
        string pctText;
        Color textColor;
        if (percent is { } p)
        {
            double clamped = Math.Clamp(p, 0, 100);
            Color barColor = UsageColor(clamped);
            if (stale) barColor = Blend(barColor, BgColor, 0.5f);

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

        using var textBrush = new SolidBrush(textColor);
        var txtSz = g.MeasureString(pctText, pctFont);
        g.DrawString(pctText, pctFont, textBrush,
            ClientSize.Width - HorizPad - txtSz.Width, midY - txtSz.Height / 2);
    }

    // Colour thresholds: <50 green, 50–75 yellow, 75–90 orange, 90+ red.
    private static Color UsageColor(double pct) => pct switch
    {
        < 50 => RunningColor,
        < 75 => AwaitingColor,
        < 90 => AttentionColor,
        _    => UsageRedColor,
    };

    // Blends two opaque colours (t = weight of b), so dimmed bars stay crisp over the panel bg.
    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R * (1 - t) + b.R * t),
        (int)(a.G * (1 - t) + b.G * t),
        (int)(a.B * (1 - t) + b.B * t));

    private static void FillRoundedBar(Graphics g, Brush brush, int x, int y, int w, int h)
    {
        if (w <= 0) return;
        int r = Math.Min(h / 2, w / 2);
        if (r <= 0) { g.FillRectangle(brush, x, y, w, h); return; }
        using var path = RoundedRect(new Rectangle(x, y, w, h), r);
        g.FillPath(brush, path);
    }

    private void DrawRow(Graphics g, int rowIdx)
    {
        if (_rows[rowIdx].IsSubAgent)
            DrawSubAgentRow(g, rowIdx);
        else
            DrawSessionRow(g, rowIdx);
    }

    private void DrawSubAgentRow(Graphics g, int rowIdx)
    {
        var sub  = _rows[rowIdx].Sub!;
        int top  = RowTop(rowIdx);
        int midY = top + SubRowHeight / 2;

        if (rowIdx == _hoveredRow)
        {
            using var hoverBrush = new SolidBrush(RowHoverColor);
            g.FillRectangle(hoverBrush, 1, top, ClientSize.Width - 2, SubRowHeight);
        }

        // Tree connector: a stub dropping from the parent row down to this child's dot.
        int branchX = HorizPad + 4;            // aligns under the parent status dot
        int dotX    = HorizPad + SubIndent;
        using (var treePen = new Pen(TreeLineColor, 1f))
        {
            g.DrawLine(treePen, branchX, top - SubRowHeight / 2, branchX, midY);
            g.DrawLine(treePen, branchX, midY, dotX - 2, midY);
        }

        using var dotBrush = new SolidBrush(SubAgentColor);
        g.FillEllipse(dotBrush, dotX, midY - 3, 6, 6);

        using var nameFont   = new Font("Segoe UI", 8f, GraphicsUnit.Point);
        using var statusFont = new Font("Segoe UI", 7f, GraphicsUnit.Point);
        using var fgBrush    = new SolidBrush(FgColor);
        using var subBrush   = new SolidBrush(SubAgentColor);

        const string statusText = "running";
        var statusSz   = g.MeasureString(statusText, statusFont);
        int labelX     = dotX + 12;
        int labelMaxW  = ClientSize.Width - labelX - HorizPad - (int)statusSz.Width - 6;
        var labelTrunc = TruncateString(g, sub.Description, nameFont, labelMaxW);
        var labelSz    = g.MeasureString(labelTrunc, nameFont);

        g.DrawString(labelTrunc, nameFont, fgBrush, labelX, midY - labelSz.Height / 2);

        int statusX = ClientSize.Width - HorizPad - (int)statusSz.Width;
        g.DrawString(statusText, statusFont, subBrush, statusX, midY - statusSz.Height / 2);
    }

    private void DrawSessionRow(Graphics g, int rowIdx)
    {
        var session = _rows[rowIdx].Session;
        int top     = RowTop(rowIdx);
        int midY    = top + RowHeight / 2;

        using var sepPen = new Pen(SepColor, 1f);
        g.DrawLine(sepPen, HorizPad, top, ClientSize.Width - HorizPad, top);

        if (rowIdx == _hoveredRow)
        {
            using var hoverBrush = new SolidBrush(RowHoverColor);
            g.FillRectangle(hoverBrush, 1, top + 1, ClientSize.Width - 2, RowHeight - 1);
        }

        // A running session gets a second, dimmer line: the parsed tool call on the left and the
        // elapsed run time on the right. Without either the project name stays vertically centred.
        bool running   = session.Status == SessionStatus.Running;
        var activity   = running ? session.Activity : null;
        var elapsed    = running ? session.RunningElapsedLabel() : null;
        bool twoLine   = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        int nameMidY   = twoLine ? top + RowHeight / 2 - 8 : midY;

        var dotColor = session.Status switch
        {
            SessionStatus.Running        => RunningColor,
            SessionStatus.NeedsAttention => AttentionColor,
            SessionStatus.AwaitingInput  => AwaitingColor,
            _                            => IdleColor,
        };

        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, HorizPad, nameMidY - 4, 8, 8);

        using var nameFont     = new Font("Segoe UI", 8.5f, GraphicsUnit.Point);
        using var statusFont   = new Font("Segoe UI", 7.5f, GraphicsUnit.Point);
        using var activityFont = new Font("Segoe UI", 7.5f, GraphicsUnit.Point);
        using var fgBrush      = new SolidBrush(FgColor);
        using var mutedBrush   = new SolidBrush(MutedColor);
        using var attnBrush    = new SolidBrush(AttentionColor);
        using var awaitBrush   = new SolidBrush(AwaitingColor);

        var statusText = session.Status switch
        {
            SessionStatus.Running        => "running",
            SessionStatus.NeedsAttention => "done ↩",
            SessionStatus.AwaitingInput  => "input ↩",
            _                            => "idle",
        };

        Brush statusBrush = session.Status switch
        {
            SessionStatus.NeedsAttention => attnBrush,
            SessionStatus.AwaitingInput  => awaitBrush,
            _                            => mutedBrush,
        };

        int badgeWidth   = session.Mode != PermissionMode.Normal ? 16 : 0;
        var statusSz     = g.MeasureString(statusText, statusFont);
        int nameMaxWidth = ClientSize.Width - HorizPad * 3 - 8 - (int)statusSz.Width - badgeWidth;
        var nameTrunc    = TruncateString(g, session.ProjectName, nameFont, nameMaxWidth);
        var nameSz       = g.MeasureString(nameTrunc, nameFont);

        g.DrawString(nameTrunc, nameFont, fgBrush,
            HorizPad + 14, nameMidY - nameSz.Height / 2);

        int statusX = ClientSize.Width - HorizPad - (int)statusSz.Width;
        g.DrawString(statusText, statusFont, statusBrush,
            statusX, nameMidY - statusSz.Height / 2);

        if (session.Mode != PermissionMode.Normal)
            DrawModeBadge(g, session.Mode, statusX - badgeWidth, nameMidY);

        if (twoLine)
        {
            int activityMidY = top + RowHeight / 2 + 9;
            int lineLeft     = HorizPad + 14;

            // Elapsed run time, right-aligned and dim.
            int elapsedW = 0;
            if (!string.IsNullOrEmpty(elapsed))
            {
                var elapsedSz = g.MeasureString(elapsed, activityFont);
                elapsedW = (int)elapsedSz.Width;
                g.DrawString(elapsed, activityFont, mutedBrush,
                    ClientSize.Width - HorizPad - elapsedW, activityMidY - elapsedSz.Height / 2);
            }

            // Activity phrase fills the remaining width to the left of the elapsed time.
            if (!string.IsNullOrEmpty(activity))
            {
                int activityMaxW  = ClientSize.Width - lineLeft - HorizPad - (elapsedW > 0 ? elapsedW + 6 : 0);
                var activityTrunc = TruncateString(g, activity, activityFont, activityMaxW);
                var activitySz    = g.MeasureString(activityTrunc, activityFont);
                g.DrawString(activityTrunc, activityFont, mutedBrush,
                    lineLeft, activityMidY - activitySz.Height / 2);
            }
        }
    }

    private static Color ModeColor(PermissionMode mode) => mode switch
    {
        PermissionMode.AcceptEdits => Color.FromArgb(167, 139, 250),
        PermissionMode.Plan        => Color.FromArgb(96,  165, 250),
        PermissionMode.Auto        => Color.FromArgb(250, 204, 21),
        PermissionMode.Bypass      => Color.FromArgb(239, 68,  68),
        _                          => Color.Transparent,
    };

    private static void DrawModeBadge(Graphics g, PermissionMode mode, int x, int midY)
    {
        using var brush = new SolidBrush(ModeColor(mode));

        // All permission modes render as a fast-forward (double-chevron) badge, distinguished by
        // colour (Plan is blue). A pause-style badge read too much like an idle session.
        g.FillPolygon(brush, new[] { new Point(x,     midY - 4), new Point(x + 5,  midY), new Point(x,     midY + 4) });
        g.FillPolygon(brush, new[] { new Point(x + 6, midY - 4), new Point(x + 11, midY), new Point(x + 6, midY + 4) });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string TruncateString(Graphics g, string text, Font font, int maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth) return text;
        while (text.Length > 0 && g.MeasureString(text + "…", font).Width > maxWidth)
            text = text[..^1];
        return text + "…";
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X,         r.Y,          d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        p.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        p.CloseFigure();
        return p;
    }

    // ── Mouse interaction ────────────────────────────────────────────────────
    protected override void OnMouseDown(MouseEventArgs e)
    {
        // The closed dense strip is draggable anywhere; otherwise only the header is a drag handle.
        bool inDragHandle = (_dense && !_denseOpen) || e.Y < HeaderHeight;
        if (e.Button == MouseButtons.Left && inDragHandle)
        {
            _dragging       = true;
            _wasDrag        = false;
            _dragStartScreen = PointToScreen(e.Location);
            _formStartLoc   = Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            var cur = PointToScreen(e.Location);
            int dx  = cur.X - _dragStartScreen.X;
            int dy  = cur.Y - _dragStartScreen.Y;

            if (!_wasDrag && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
                _wasDrag = true;

            if (_wasDrag)
            {
                if (_dense)
                {
                    // Dense stays hugging the right screen edge; only the vertical position moves.
                    var wa   = Screen.PrimaryScreen!.WorkingArea;
                    int newY = ClampDenseY(_formStartLoc.Y + dy, Height, wa);
                    Location = new Point(wa.Right - Width, newY);
                    _denseY  = newY;
                }
                else
                {
                    Location = new Point(_formStartLoc.X + dx, _formStartLoc.Y + dy);
                }
            }
        }
        else
        {
            int newHover = HitTestRow(e.Location);
            if (newHover != _hoveredRow)
            {
                _hoveredRow = newHover;
                Invalidate();
            }

            // Dwell over the usage strip (only present when the full panel shows) pops a details/staleness tooltip.
            bool inStrip = ShowFullPanel && _usageEnabled && e.Y >= HeaderHeight && e.Y < RowsTop;
            if (inStrip != _inUsageStrip)
            {
                _inUsageStrip = inStrip;
                if (inStrip)
                {
                    _usageHoverTimer.Stop();
                    _usageHoverTimer.Start();
                }
                else
                {
                    _usageHoverTimer.Stop();
                    HideUsageTooltip();
                }
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_wasDrag)
        {
            bool headerVisible = !(_dense && !_denseOpen);

            if (headerVisible && SideIconRect().Contains(e.Location))
            {
                // The dense toggle: enter dense from floating, or leave it from the open popup.
                ToggleDense();
            }
            else
            {
                int row = HitTestRow(e.Location);
                if (row >= 0)
                {
                    // Sub-agent rows resolve to their parent session — the sub-agent runs in the
                    // parent's process, so focusing means focusing the parent terminal.
                    var pid = _rows[row].Session.Pid;
                    SessionFocused?.Invoke(pid);
                    if (int.TryParse(pid, out int pidInt))
                        NativeMethods.FocusTerminalForProcess(pidInt);
                }
                else if (!_dense && e.Y < HeaderHeight && _sessions.Count > 0)
                {
                    // Header click toggles expand/collapse — floating mode only.
                    _expanded = !_expanded;
                    RelayoutWindow();
                    UpdateTickTimer();
                    Invalidate();
                }
            }
        }

        _dragging = false;
        _wasDrag  = false;
        base.OnMouseUp(e);
    }

    // Hovering the dense strip pops the full panel open; any re-entry cancels a pending auto-close.
    protected override void OnMouseEnter(EventArgs e)
    {
        if (_dense)
        {
            _denseCloseTimer.Stop();
            OpenDensePopup();
        }
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoveredRow = -1;
        _inUsageStrip = false;
        _usageHoverTimer.Stop();
        HideUsageTooltip();

        // Start the 2-second countdown to collapse the dense popup back to the strip.
        if (_dense && _denseOpen)
            _denseCloseTimer.Start();

        Invalidate();
        base.OnMouseLeave(e);
    }

    // ── Usage tooltip ──────────────────────────────────────────────────────────
    private void ShowUsageTooltip()
    {
        var stripScreen = RectangleToScreen(new Rectangle(0, HeaderHeight, ClientSize.Width, UsageStripHeight));
        _usageTooltip.ShowFor(_usage, stripScreen);
    }

    private void HideUsageTooltip()
    {
        if (_usageTooltip.Visible)
            _usageTooltip.Hide();
    }

    private int HitTestRow(Point p)
    {
        if (!ShowFullPanel || p.Y < RowsTop) return -1;
        int y = RowsTop;
        for (int i = 0; i < _rows.Count; i++)
        {
            int h = HeightOf(_rows[i]);
            if (p.Y >= y && p.Y < y + h)
                return i;
            y += h;
        }
        return -1;
    }

    // ── Context menu ─────────────────────────────────────────────────────────
    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var exit = new ToolStripMenuItem("Exit Claude Watch");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);
        return menu;
    }

    // ── Hot key ────────────────────────────────────────────────────────────────
    // Alt+Shift+W toggles dense mode. Only fires while the overlay has keyboard focus for now;
    // a system-wide registration can be added later.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.Shift | Keys.W))
        {
            ToggleDense();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Window style: no taskbar entry, no Alt+Tab ───────────────────────────
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────────
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flashTimer.Dispose();
            _flashStopTimer.Dispose();
            _tickTimer.Dispose();
            _usageHoverTimer.Dispose();
            _denseCloseTimer.Dispose();
            _usageTooltip.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
