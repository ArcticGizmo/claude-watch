using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeWatch;

internal sealed class IndicatorTooltipForm : Form
{
    private const int HorizPad = 10;
    private const int VertPad  = 6;
    private const int Corner   = 6;

    private static readonly Color BgColor       = Color.FromArgb(20,  20,  28);
    private static readonly Color BorderColor   = Color.FromArgb(60,  60,  80);
    private static readonly Color FgColor       = Color.FromArgb(225, 225, 235);
    private static readonly Color RunningColor   = Color.FromArgb(34,  197, 94);
    private static readonly Color AttnColor      = Color.FromArgb(251, 146, 60);
    private static readonly Color AwaitingColor  = Color.FromArgb(250, 204, 21);
    private static readonly Color IdleColor      = Color.FromArgb(100, 116, 139);

    private string         _projectName = "";
    private string         _statusText  = "";
    private Color          _statusColor = IdleColor;
    private PermissionMode _mode        = PermissionMode.Normal;

    public IndicatorTooltipForm()
    {
        FormBorderStyle   = FormBorderStyle.None;
        ShowInTaskbar     = false;
        TopMost           = true;
        AllowTransparency = true;
        BackColor         = Color.Black;
        TransparencyKey   = Color.Black;
        DoubleBuffered    = true;
        StartPosition     = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            return cp;
        }
    }

    public void ShowFor(ClaudeSession session, Point indicatorLocation, int indicatorSize)
    {
        _projectName = session.ProjectName;
        _mode        = session.Mode;
        (_statusText, _statusColor) = session.Status switch
        {
            SessionStatus.Running        => ("running",          RunningColor),
            SessionStatus.NeedsAttention => ("needs attention",  AttnColor),
            SessionStatus.AwaitingInput  => ("awaiting input",   AwaitingColor),
            _                            => ("idle",             IdleColor),
        };

        using var nameFont   = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        using var statusFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        using var modeFont   = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        using var g          = CreateGraphics();

        var nameSz   = g.MeasureString(_projectName, nameFont);
        var statusSz = g.MeasureString(_statusText,  statusFont);

        float contentWidth = Math.Max(nameSz.Width, statusSz.Width);
        int h = (int)nameSz.Height + (int)statusSz.Height + VertPad * 2 + 4;

        if (_mode != PermissionMode.Normal)
        {
            var modeLabel = ModeLabel(_mode);
            var modeSz    = g.MeasureString(modeLabel, modeFont);
            contentWidth  = Math.Max(contentWidth, modeSz.Width + 14); // 14 = badge width + gap
            h            += (int)modeSz.Height + 2;
        }

        int w = (int)contentWidth + HorizPad * 2 + 2;

        ClientSize = new Size(w, h);
        Location   = new Point(indicatorLocation.X + indicatorSize - w, indicatorLocation.Y - h - 4);

        Invalidate();
        Show();
        BringToFront();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var path = RoundedRect(bounds, Corner);

        using (var bg = new SolidBrush(BgColor))
            g.FillPath(bg, path);
        using (var pen = new Pen(BorderColor, 1.5f))
            g.DrawPath(pen, path);

        using var nameFont    = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        using var statusFont  = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        using var modeFont    = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        using var fgBrush     = new SolidBrush(FgColor);
        using var statusBrush = new SolidBrush(_statusColor);

        var nameSz   = g.MeasureString(_projectName, nameFont);
        var statusSz = g.MeasureString(_statusText,  statusFont);
        g.DrawString(_projectName, nameFont,   fgBrush,     HorizPad, VertPad);
        g.DrawString(_statusText,  statusFont, statusBrush, HorizPad, VertPad + nameSz.Height + 2);

        if (_mode != PermissionMode.Normal)
        {
            float modeY   = VertPad + nameSz.Height + 2 + statusSz.Height + 4;
            var modeColor = ModeColor(_mode);
            using var modeBrush = new SolidBrush(modeColor);
            DrawModeBadge(g, _mode, HorizPad, (int)(modeY + modeFont.GetHeight(g) / 2));
            g.DrawString(ModeLabel(_mode), modeFont, modeBrush, HorizPad + 14, modeY);
        }
    }

    private static string ModeLabel(PermissionMode mode) => mode switch
    {
        PermissionMode.AcceptEdits => "accept edits",
        PermissionMode.Plan        => "plan mode",
        PermissionMode.Auto        => "auto",
        PermissionMode.Bypass      => "bypass",
        _                          => "",
    };

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
        var color = ModeColor(mode);
        using var brush = new SolidBrush(color);

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
}
