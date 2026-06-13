namespace ClaudeWatch;

internal sealed class SessionIndicatorForm : Form
{
    internal const int IndicatorSize = 42;

    private readonly IndicatorTooltipForm _tooltip = new();
    private readonly Action<string> _onFocused;
    private IndicatorStyle _style;

    public ClaudeSession Session { get; private set; }

    public SessionIndicatorForm(ClaudeSession session, IndicatorStyle style, Action<string> onFocused)
    {
        _onFocused = onFocused;
        _style     = style;
        Session    = session;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        ClientSize      = new Size(IndicatorSize, IndicatorSize);
        Cursor          = Cursors.Hand;
    }

    public void UpdateSession(ClaudeSession session)
    {
        Session = session;
        Refresh();
    }

    public void UpdateStyle(IndicatorStyle style)
    {
        if (_style == style) return;
        _style = style;
        Refresh();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Refresh();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _tooltip.ShowFor(Session, Location, IndicatorSize);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _tooltip.Hide();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _onFocused(Session.Pid);
            if (int.TryParse(Session.Pid, out int pid))
                NativeMethods.FocusTerminalForProcess(pid);
        }
        base.OnMouseUp(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tooltip.Dispose();
        base.Dispose(disposing);
    }

    private new void Refresh()
    {
        if (!IsHandleCreated) return;
        _style.Apply(Handle, Session.Status, IndicatorSize, Location);
    }
}
