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
    private const int FormWidth     = 280;
    private const int CompactHeight = 44;
    private const int RowHeight     = 30;
    private const int SubRowHeight  = 24;
    private const int SubIndent     = 22;
    private const int HorizPad      = 12;
    private const int Corner        = 10;

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

    private readonly System.Windows.Forms.Timer _flashTimer;
    private readonly System.Windows.Forms.Timer _flashStopTimer;

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
        ClientSize = new Size(FormWidth, CompactHeight);

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

        UpdateHeight();
        Invalidate();
    }

    public void TriggerAttention()
    {
        // Auto-expand so the user can see which project needs attention
        if (!_expanded && _rows.Count > 0)
        {
            _expanded = true;
            UpdateHeight();
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
        int top = CompactHeight;
        for (int i = 0; i < index; i++)
            top += HeightOf(_rows[i]);
        return top;
    }

    private void UpdateHeight()
    {
        int h = CompactHeight;
        if (_expanded && _rows.Count > 0)
        {
            foreach (var row in _rows)
                h += HeightOf(row);
            h += 2;
        }

        if (ClientSize.Height != h)
            ClientSize = new Size(FormWidth, h);
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

        DrawHeader(g);

        if (_expanded)
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(g, i);
    }

    private void DrawHeader(Graphics g)
    {
        int midY = CompactHeight / 2;

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

        // Chevron — only when there are sessions to expand
        if (_sessions.Count > 0)
        {
            var chevron = _expanded ? "▲" : "▼";
            var chSz    = g.MeasureString(chevron, chevFont);
            g.DrawString(chevron, chevFont, muted,
                ClientSize.Width - HorizPad - chSz.Width,
                midY - chSz.Height / 2);
        }
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

        var dotColor = session.Status switch
        {
            SessionStatus.Running        => RunningColor,
            SessionStatus.NeedsAttention => AttentionColor,
            SessionStatus.AwaitingInput  => AwaitingColor,
            _                            => IdleColor,
        };

        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, HorizPad, midY - 4, 8, 8);

        using var nameFont   = new Font("Segoe UI", 8.5f, GraphicsUnit.Point);
        using var statusFont = new Font("Segoe UI", 7.5f, GraphicsUnit.Point);
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
            HorizPad + 14, midY - nameSz.Height / 2);

        int statusX = ClientSize.Width - HorizPad - (int)statusSz.Width;
        g.DrawString(statusText, statusFont, statusBrush,
            statusX, midY - statusSz.Height / 2);

        if (session.Mode != PermissionMode.Normal)
            DrawModeBadge(g, session.Mode, statusX - badgeWidth, midY);
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

        if (mode == PermissionMode.Plan)
        {
            g.FillRectangle(brush, x,     midY - 4, 3, 8);
            g.FillRectangle(brush, x + 5, midY - 4, 3, 8);
        }
        else
        {
            g.FillPolygon(brush, new[] { new Point(x,     midY - 4), new Point(x + 5,  midY), new Point(x,     midY + 4) });
            g.FillPolygon(brush, new[] { new Point(x + 6, midY - 4), new Point(x + 11, midY), new Point(x + 6, midY + 4) });
        }
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
        if (e.Button == MouseButtons.Left && e.Y < CompactHeight)
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
                Location = new Point(_formStartLoc.X + dx, _formStartLoc.Y + dy);
        }
        else
        {
            int newHover = HitTestRow(e.Location);
            if (newHover != _hoveredRow)
            {
                _hoveredRow = newHover;
                Invalidate();
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_wasDrag)
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
            else if (e.Y < CompactHeight && _sessions.Count > 0)
            {
                _expanded = !_expanded;
                UpdateHeight();
                Invalidate();
            }
        }

        _dragging = false;
        _wasDrag  = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoveredRow = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    private int HitTestRow(Point p)
    {
        if (!_expanded || p.Y < CompactHeight) return -1;
        int y = CompactHeight;
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
        }
        base.Dispose(disposing);
    }
}
