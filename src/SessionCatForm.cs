namespace ClaudeWatch;

internal sealed class SessionCatForm : Form
{
    internal const int CatSize = 42; // 28 * 1.5

    private static readonly Bitmap SleepImage   = LoadSprite("duck-sleep.png");
    private static readonly Bitmap WorkingImage  = LoadSprite("duck-working.png");
    private static readonly Bitmap AlertImage    = LoadSprite("duck-alert.png");

    private readonly CatTooltipForm _tooltip = new();
    private readonly Action<string> _onFocused;

    public ClaudeSession Session { get; private set; }

    public SessionCatForm(ClaudeSession session, Action<string> onFocused)
    {
        _onFocused = onFocused;
        Session         = session;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        ClientSize      = new Size(CatSize, CatSize);
        Cursor          = Cursors.Hand;
    }

    public void UpdateSession(ClaudeSession session)
    {
        Session = session;
        RefreshSprite();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RefreshSprite();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _tooltip.ShowFor(Session, Location, CatSize);
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

    private void RefreshSprite()
    {
        if (!IsHandleCreated) return;
        NativeMethods.ApplyLayeredBitmap(Handle, StatusImage(Session.Status), CatSize, Location);
    }

    private static Bitmap StatusImage(SessionStatus s) => s switch
    {
        SessionStatus.Running        => WorkingImage,
        SessionStatus.NeedsAttention => AlertImage,
        _                            => SleepImage,
    };

    private static Bitmap LoadSprite(string name)
    {
        var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"ClaudeWatch.sprites.{name}")!;
        return new Bitmap(stream);
    }
}
