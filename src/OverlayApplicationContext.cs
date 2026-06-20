using Velopack;
using Velopack.Sources;

namespace ClaudeWatch;

internal sealed class OverlayApplicationContext : ApplicationContext
{
    // FileSystemWatcher can silently drop events on buffer overflow, so a slow
    // reconciliation scan keeps state honest even if a change notification is missed.
    private const int ReconcileIntervalMs = 30_000;

    // Account-wide rate-limit usage changes slowly; poll on startup then every 5 minutes.
    private const int UsageIntervalMs = 300_000;

    private readonly OverlayForm _overlay;
    private readonly SessionMonitor _monitor;
    private readonly UsageMonitor _usageMonitor = new();
    private readonly System.Windows.Forms.Timer _reconcileTimer;
    private readonly System.Windows.Forms.Timer _deadlineTimer;
    private readonly System.Windows.Forms.Timer _usageTimer;
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private readonly PluginManager _pluginManager = new();

    // The settings window, lazily created on first open and reused while it stays open.
    private SettingsForm? _settingsForm;

    // Most recent usage reading, so a freshly-opened settings window can show it without waiting
    // for the next poll. Empty until the first successful (or attempted) fetch.
    private UsageInfo _lastUsage = UsageInfo.Empty;

    // PID of the session whose notification was last shown, so a balloon click
    // can focus the right terminal.
    private string? _lastNotifiedPid;

    public OverlayApplicationContext()
    {
        _settings = AppSettings.Load();

        _overlay = new OverlayForm();
        _overlay.FormClosed     += (_, _) => ExitThread();
        _overlay.ExitRequested  += (_, _) => Exit();
        _overlay.SessionFocused += AcknowledgeSession;

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text    = "Claude Watch",
            Icon    = LoadEmbeddedIcon("ClaudeWatch.icon.png"),
        };
        // Left-click opens the first-class settings window; right-click shows the slim menu below.
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        var trayMenu = new ContextMenuStrip();

        var header = new ToolStripMenuItem($"Claude Watch — v{AppInfo.Version}") { Enabled = false };
        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        var updateItem = new ToolStripMenuItem("Check for Updates…");
        updateItem.Click += (_, _) => CheckForUpdates();
        var exitItem = new ToolStripMenuItem("Exit Claude Watch");
        exitItem.Click += (_, _) => Exit();

        trayMenu.Items.Add(header);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(settingsItem);
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

        // Periodic account-usage refresh for the overlay's session/weekly bars. Only runs while
        // the feature is enabled — when off, no OAuth query is ever made.
        _usageTimer = new System.Windows.Forms.Timer { Interval = UsageIntervalMs };
        _usageTimer.Tick += (_, _) => RefreshUsage();

        _overlay.Show();
        _overlay.SetUsageEnabled(_settings.ShowUsage);
        _monitor.Scan();

        if (_settings.ShowUsage)
        {
            _usageTimer.Start();
            RefreshUsage();
        }
    }

    // Opens (or re-focuses) the settings window, wiring it to the shared state and callbacks.
    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            _settingsForm.BringToFront();
            return;
        }

        _settingsForm = new SettingsForm(_settings, _pluginManager, _usageMonitor, _lastUsage);
        _settingsForm.UsageEnabledChanged    += SetUsageEnabled;
        _settingsForm.CheckForUpdatesRequested += (_, _) => CheckForUpdates();
        _settingsForm.FormClosed             += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    // Toggles the usage bars. Disabling stops all polling so no OAuth query ever goes out;
    // enabling kicks off an immediate refresh and resumes the timer.
    private void SetUsageEnabled(bool enabled)
    {
        if (_settings.ShowUsage == enabled)
            return;

        _settings.ShowUsage = enabled;
        _settings.Save();
        _overlay.SetUsageEnabled(enabled);

        if (enabled)
        {
            _usageTimer.Start();
            RefreshUsage();
        }
        else
        {
            _usageTimer.Stop();
        }
    }

    // Fetches usage off the UI thread, then pushes the result back onto it for rendering in both
    // the overlay and (if open) the settings window. Caches the latest reading for new windows.
    private async void RefreshUsage()
    {
        if (!_settings.ShowUsage) return;
        var info = await _usageMonitor.FetchAsync();
        _lastUsage = info;
        try
        {
            if (_overlay.IsHandleCreated && !_overlay.IsDisposed)
                _overlay.BeginInvoke((Action)(() =>
                {
                    _overlay.UpdateUsage(info);
                    if (_settingsForm is { IsDisposed: false })
                        _settingsForm.UpdateUsage(info);
                }));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions)
    {
        _overlay.UpdateSessions(sessions);
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

    private void AcknowledgeSession(string pid)
    {
        _monitor.Acknowledge(pid);
        _overlay.BeginInvoke(_monitor.Scan);
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
        _usageTimer.Stop();
        _notifyIcon.Visible = false;
        if (_settingsForm is { IsDisposed: false })
            _settingsForm.Close();
        _overlay.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reconcileTimer.Dispose();
            _deadlineTimer.Dispose();
            _usageTimer.Dispose();
            _monitor.Dispose();
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
            _settingsForm?.Dispose();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
