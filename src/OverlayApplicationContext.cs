namespace ClaudeWatch;

internal sealed class OverlayApplicationContext : ApplicationContext
{
    private const int CatSize = 28;
    private const int CatGap  = 6;

    private readonly OverlayForm _overlay;
    private readonly SessionMonitor _monitor;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<string, SessionCatForm> _cats = new();

    public OverlayApplicationContext()
    {
        _overlay = new OverlayForm();
        _overlay.FormClosed    += (_, _) => ExitThread();
        _overlay.ExitRequested += (_, _) => Exit();
        _overlay.SessionFocused += AcknowledgeSession;

        // Tray icon: provides balloon-tip notifications and a fallback right-click exit
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text    = "Claude Watch",
            Icon    = IconRenderer.Create(0, SessionStatus.Idle),
        };
        _notifyIcon.DoubleClick += (_, _) => { _overlay.BringToFront(); _overlay.TopMost = true; };

        var trayMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show Overlay");
        showItem.Click += (_, _) => { _overlay.BringToFront(); _overlay.TopMost = true; };
        var exitItem = new ToolStripMenuItem("Exit Claude Watch");
        exitItem.Click += (_, _) => Exit();
        trayMenu.Items.Add(showItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = trayMenu;

        _monitor = new SessionMonitor();
        _monitor.SessionsChanged += OnSessionsChanged;
        _monitor.NeedsAttention  += OnNeedsAttention;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += (_, _) => _monitor.Scan();
        _pollTimer.Start();

        _monitor.Scan();
        _overlay.Show();
    }

    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions)
    {
        _overlay.UpdateSessions(sessions);
        UpdateCats(sessions);

        var worst   = sessions.Count == 0
            ? SessionStatus.Idle
            : (SessionStatus)sessions.Max(s => (int)s.Status);

        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = IconRenderer.Create(sessions.Count, worst);
        oldIcon?.Dispose();

        _notifyIcon.Text = sessions.Count switch
        {
            0 => "Claude Watch — No active sessions",
            1 => "Claude Watch — 1 session",
            _ => $"Claude Watch — {sessions.Count} sessions",
        };
    }

    private void UpdateCats(IReadOnlyList<ClaudeSession> sessions)
    {
        var activePids = sessions.Select(s => s.Pid).ToHashSet();

        foreach (var pid in _cats.Keys.Where(k => !activePids.Contains(k)).ToList())
        {
            _cats[pid].Close();
            _cats[pid].Dispose();
            _cats.Remove(pid);
        }

        foreach (var session in sessions)
        {
            if (_cats.TryGetValue(session.Pid, out var cat))
                cat.UpdateSession(session);
            else
            {
                var form = new SessionCatForm(session, AcknowledgeSession);
                form.Show();
                _cats[session.Pid] = form;
            }
        }

        RepositionCats(sessions);
    }

    private void AcknowledgeSession(string pid)
    {
        _monitor.Acknowledge(pid);
        _overlay.BeginInvoke(_monitor.Scan);
    }

    private void RepositionCats(IReadOnlyList<ClaudeSession> sessions)
    {
        if (sessions.Count == 0) return;

        var ordered = sessions
            .OrderByDescending(s => (int)s.Status)
            .ThenByDescending(s => s.LastUpdated)
            .ToList();

        var screen  = Screen.PrimaryScreen!.WorkingArea;
        int rightX  = screen.Right - 16;
        int y       = screen.Bottom - CatSize;

        for (int i = 0; i < ordered.Count; i++)
        {
            if (_cats.TryGetValue(ordered[i].Pid, out var cat))
                cat.Location = new Point(rightX - (i + 1) * CatSize - i * CatGap, y);
        }
    }

    private void OnNeedsAttention(ClaudeSession session)
    {
        _overlay.TriggerAttention();

        _notifyIcon.BalloonTipTitle = "Claude Code — Done";
        _notifyIcon.BalloonTipText  = $"Waiting for you in {session.ProjectName}";
        _notifyIcon.BalloonTipIcon  = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(8000);
    }

    private void Exit()
    {
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        foreach (var cat in _cats.Values) cat.Close();
        _overlay.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _monitor.Dispose();
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
            foreach (var cat in _cats.Values) cat.Dispose();
            _cats.Clear();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
