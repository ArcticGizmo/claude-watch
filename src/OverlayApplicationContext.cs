namespace ClaudeWatch;

internal sealed class OverlayApplicationContext : ApplicationContext
{
    private const int IndicatorGap = 6;

    private readonly OverlayForm _overlay;
    private readonly SessionMonitor _monitor;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<string, SessionIndicatorForm> _indicators = new();
    private readonly AppSettings _settings;
    private readonly ToolStripMenuItem _displayMenu;

    private IReadOnlyList<ClaudeSession> _currentSessions = [];
    private IndicatorStyle _currentStyle;

    public OverlayApplicationContext()
    {
        _settings     = AppSettings.Load();
        _currentStyle = IndicatorStyle.FromName(_settings.DisplayStyle);

        _overlay = new OverlayForm();
        _overlay.FormClosed     += (_, _) => ExitThread();
        _overlay.ExitRequested  += (_, _) => Exit();
        _overlay.SessionFocused += AcknowledgeSession;

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text    = "Claude Watch",
            Icon    = IconRenderer.Create(0, SessionStatus.Idle),
        };
        _notifyIcon.DoubleClick += (_, _) => { _overlay.BringToFront(); _overlay.TopMost = true; };

        _displayMenu = BuildDisplayMenu();

        var trayMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show Overlay");
        showItem.Click += (_, _) => { _overlay.BringToFront(); _overlay.TopMost = true; };
        var exitItem = new ToolStripMenuItem("Exit Claude Watch");
        exitItem.Click += (_, _) => Exit();
        trayMenu.Items.Add(showItem);
        trayMenu.Items.Add(_displayMenu);
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

    private ToolStripMenuItem BuildDisplayMenu()
    {
        var menu = new ToolStripMenuItem("Display");
        foreach (var style in IndicatorStyle.All)
        {
            var item = new ToolStripMenuItem(style.Name)
            {
                Checked      = style == _currentStyle,
                CheckOnClick = false,
                Tag          = style,
            };
            item.Click += OnDisplayStyleSelected;
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private void OnDisplayStyleSelected(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Tag is not IndicatorStyle style) return;
        if (style == _currentStyle) return;

        _currentStyle          = style;
        _settings.DisplayStyle = style.Name;
        _settings.Save();

        foreach (ToolStripMenuItem child in _displayMenu.DropDownItems)
            child.Checked = child.Tag == style;

        UpdateIndicators(_currentSessions);
    }

    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions)
    {
        _overlay.UpdateSessions(sessions);
        UpdateIndicators(sessions);

        var worst = sessions.Count == 0
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

    private void UpdateIndicators(IReadOnlyList<ClaudeSession> sessions)
    {
        _currentSessions = sessions;

        if (!_currentStyle.ShowForms)
        {
            foreach (var indicator in _indicators.Values) { indicator.Close(); indicator.Dispose(); }
            _indicators.Clear();
            return;
        }

        var activePids = sessions.Select(s => s.Pid).ToHashSet();

        foreach (var pid in _indicators.Keys.Where(k => !activePids.Contains(k)).ToList())
        {
            _indicators[pid].Close();
            _indicators[pid].Dispose();
            _indicators.Remove(pid);
        }

        foreach (var session in sessions)
        {
            if (_indicators.TryGetValue(session.Pid, out var indicator))
            {
                indicator.UpdateStyle(_currentStyle);
                indicator.UpdateSession(session);
            }
            else
            {
                var form = new SessionIndicatorForm(session, _currentStyle, AcknowledgeSession);
                form.Show();
                _indicators[session.Pid] = form;
            }
        }

        RepositionIndicators(sessions);
    }

    private void AcknowledgeSession(string pid)
    {
        _monitor.Acknowledge(pid);
        _overlay.BeginInvoke(_monitor.Scan);
    }

    private void RepositionIndicators(IReadOnlyList<ClaudeSession> sessions)
    {
        if (sessions.Count == 0) return;

        var ordered = sessions
            .OrderByDescending(s => s.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var screen = Screen.PrimaryScreen!.WorkingArea;
        int size   = SessionIndicatorForm.IndicatorSize;
        int rightX = screen.Right - 16;
        int y      = screen.Bottom - size;

        for (int i = 0; i < ordered.Count; i++)
        {
            if (_indicators.TryGetValue(ordered[i].Pid, out var indicator))
                indicator.Location = new Point(rightX - (i + 1) * size - i * IndicatorGap, y);
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
        foreach (var indicator in _indicators.Values) indicator.Close();
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
            foreach (var indicator in _indicators.Values) indicator.Dispose();
            _indicators.Clear();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
