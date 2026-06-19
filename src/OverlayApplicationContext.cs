using Velopack;
using Velopack.Sources;

namespace ClaudeWatch;

internal sealed class OverlayApplicationContext : ApplicationContext
{
    private const int IndicatorGap = 6;

    // FileSystemWatcher can silently drop events on buffer overflow, so a slow
    // reconciliation scan keeps state honest even if a change notification is missed.
    private const int ReconcileIntervalMs = 30_000;

    private readonly OverlayForm _overlay;
    private readonly SessionMonitor _monitor;
    private readonly System.Windows.Forms.Timer _reconcileTimer;
    private readonly System.Windows.Forms.Timer _deadlineTimer;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<string, SessionIndicatorForm> _indicators = new();
    private readonly AppSettings _settings;
    private readonly ToolStripMenuItem _displayMenu;

    private IReadOnlyList<ClaudeSession> _currentSessions = [];
    private IndicatorStyle _currentStyle;

    // PID of the session whose notification was last shown, so a balloon click
    // can focus the right terminal.
    private string? _lastNotifiedPid;

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
            Icon    = LoadEmbeddedIcon("ClaudeWatch.sprites.icon.png"),
        };
        _notifyIcon.DoubleClick += (_, _) => { _overlay.BringToFront(); _overlay.TopMost = true; };
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        _displayMenu = BuildDisplayMenu();

        var trayMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show Overlay");
        showItem.Click += (_, _) => { _overlay.BringToFront(); _overlay.TopMost = true; };
        var exitItem = new ToolStripMenuItem("Exit Claude Watch");
        exitItem.Click += (_, _) => Exit();
        var updateItem = new ToolStripMenuItem("Check for Updates...");
        updateItem.Click += (_, _) => CheckForUpdates();

        trayMenu.Items.Add(showItem);
        trayMenu.Items.Add(_displayMenu);
        trayMenu.Items.Add(updateItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = trayMenu;

        _monitor = new SessionMonitor();
        _monitor.SessionsChanged += OnSessionsChanged;
        _monitor.NeedsAttention  += OnNeedsAttention;
        _monitor.AwaitingInput   += OnAwaitingInput;
        // Fires on a thread-pool thread (watcher / process-exit callbacks); marshal to the UI thread.
        _monitor.ChangeDetected  += RequestScan;

        // One-shot timer that fires the moment a "needs attention" window lapses back to idle —
        // a purely time-based transition with no corresponding file change to drive it.
        _deadlineTimer = new System.Windows.Forms.Timer();
        _deadlineTimer.Tick += (_, _) => { _deadlineTimer.Stop(); _monitor.Scan(); };

        // Low-frequency safety net against dropped FileSystemWatcher events.
        _reconcileTimer = new System.Windows.Forms.Timer { Interval = ReconcileIntervalMs };
        _reconcileTimer.Tick += (_, _) => _monitor.Scan();
        _reconcileTimer.Start();

        _overlay.Show();
        _monitor.Scan();
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
        ArmDeadlineTimer();

        _notifyIcon.Text = sessions.Count switch
        {
            0 => "Claude Watch — No active sessions",
            1 => "Claude Watch — 1 session",
            _ => $"Claude Watch — {sessions.Count} sessions",
        };
    }

    // Marshals a re-scan onto the UI thread. SessionMonitor raises ChangeDetected from
    // FileSystemWatcher and Process.Exited callbacks, which run on thread-pool threads.
    private void RequestScan()
    {
        try
        {
            if (_overlay.IsHandleCreated && !_overlay.IsDisposed)
                _overlay.BeginInvoke((Action)(() => _monitor.Scan()));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // Arms the one-shot timer for the next needs-attention deadline reported by the monitor.
    // Called from OnSessionsChanged (i.e. after every Scan), so it always reflects current state.
    private void ArmDeadlineTimer()
    {
        _deadlineTimer.Stop();

        var deadline = _monitor.NextNeedsAttentionDeadline;
        if (deadline == null)
            return;

        var ms = (deadline.Value - DateTime.Now).TotalMilliseconds;
        // Fire on the next message-loop tick if already due (never re-scan re-entrantly here).
        _deadlineTimer.Interval = (int)Math.Clamp(ms, 1, int.MaxValue);
        _deadlineTimer.Start();
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

        _lastNotifiedPid = session.Pid;
        _notifyIcon.BalloonTipTitle = "Claude Code — Done";
        _notifyIcon.BalloonTipText  = $"Waiting for you in {session.ProjectName}";
        _notifyIcon.BalloonTipIcon  = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(8000);
    }

    private void OnAwaitingInput(ClaudeSession session)
    {
        _overlay.TriggerAttention();

        _lastNotifiedPid = session.Pid;
        _notifyIcon.BalloonTipTitle = "Claude Code — Waiting for Input";
        _notifyIcon.BalloonTipText  = $"{session.ProjectName} needs your response";
        _notifyIcon.BalloonTipIcon  = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(8000);
    }

    // Clicking the desktop notification focuses the terminal for the session that
    // raised it and acknowledges the alert, mirroring an overlay/indicator click.
    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        var pid = _lastNotifiedPid;
        if (pid == null) return;

        if (int.TryParse(pid, out int pidInt))
            NativeMethods.FocusTerminalForProcess(pidInt);

        AcknowledgeSession(pid);
    }

    private static Icon LoadEmbeddedIcon(string resourceName)
    {
        using var stream = typeof(OverlayApplicationContext).Assembly.GetManifestResourceStream(resourceName)!;
        using var bmp = new Bitmap(stream);
        var hIcon = bmp.GetHicon();
        try
        {
            using var rawIcon = Icon.FromHandle(hIcon);
            using var ms = new MemoryStream();
            rawIcon.Save(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private async void CheckForUpdates()
    {
        // Update balloons aren't tied to a session; don't let a click focus a stale terminal.
        _lastNotifiedPid = null;
        try
        {
            var mgr = new UpdateManager(new GithubSource("https://github.com/ArcticGizmo/claude-watch", null, false));
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                _notifyIcon.BalloonTipTitle = "Claude Watch";
                _notifyIcon.BalloonTipText  = "You're on the latest version.";
                _notifyIcon.BalloonTipIcon  = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(4000);
                return;
            }

            _notifyIcon.BalloonTipTitle = "Claude Watch — Updating";
            _notifyIcon.BalloonTipText  = $"Downloading v{update.TargetFullRelease.Version}…";
            _notifyIcon.BalloonTipIcon  = ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(5000);

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            _notifyIcon.BalloonTipTitle = "Claude Watch — Update Failed";
            _notifyIcon.BalloonTipText  = ex.Message;
            _notifyIcon.BalloonTipIcon  = ToolTipIcon.Error;
            _notifyIcon.ShowBalloonTip(6000);
        }
    }

    private void Exit()
    {
        _reconcileTimer.Stop();
        _deadlineTimer.Stop();
        _notifyIcon.Visible = false;
        foreach (var indicator in _indicators.Values) indicator.Close();
        _overlay.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reconcileTimer.Dispose();
            _deadlineTimer.Dispose();
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
