namespace ClaudeWatch;

internal sealed class SessionCatForm : Form
{
    private const int CatSize = 28;

    private static readonly Color RunningColor   = Color.FromArgb(34,  197, 94);
    private static readonly Color AttentionColor = Color.FromArgb(251, 146, 60);
    private static readonly Color IdleColor      = Color.FromArgb(100, 116, 139);

    private readonly ToolTip _toolTip = new();

    public ClaudeSession Session { get; private set; }

    public SessionCatForm(ClaudeSession session)
    {
        Session         = session;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        ClientSize      = new Size(CatSize, CatSize);
        Cursor          = Cursors.Hand;
        BackColor       = StatusColor(session.Status);
        UpdateTooltip();
    }

    public void UpdateSession(ClaudeSession session)
    {
        Session   = session;
        BackColor = StatusColor(session.Status);
        UpdateTooltip();
    }

    private void UpdateTooltip() =>
        _toolTip.SetToolTip(this, $"{Session.ProjectName} — {StatusLabel(Session.Status)}");

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && int.TryParse(Session.Pid, out int pid))
            NativeMethods.FocusTerminalForProcess(pid);
        base.OnMouseUp(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW — no taskbar / Alt+Tab entry
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private static Color StatusColor(SessionStatus s) => s switch
    {
        SessionStatus.Running        => RunningColor,
        SessionStatus.NeedsAttention => AttentionColor,
        _                            => IdleColor,
    };

    private static string StatusLabel(SessionStatus s) => s switch
    {
        SessionStatus.Running        => "running",
        SessionStatus.NeedsAttention => "needs attention",
        _                            => "idle",
    };
}
