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

    // Grace period after the last session ends before an auto-started tray closes itself, so a quick
    // session restart/compact (or opening the next session) doesn't tear the tray down and back up.
    private const int AutoCloseGraceMs = 20_000;

    private readonly OverlayForm _overlay;
    private readonly SessionMonitor _monitor;
    private readonly UsageMonitor _usageMonitor = new();
    private readonly System.Windows.Forms.Timer _reconcileTimer;
    private readonly System.Windows.Forms.Timer _deadlineTimer;
    private readonly System.Windows.Forms.Timer _usageTimer;

    // One-shot grace timer for "auto-close after last session" (see AutoCloseGraceMs).
    private readonly System.Windows.Forms.Timer _autoCloseTimer;

    // Latched once we've observed at least one live session, so the startup race (the tray launches
    // before the opening session's file appears) can't trigger an immediate auto-close.
    private bool _seenSession;
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;

    // Tracks workstation lock state so the AFK override can push any session's alert while locked.
    private readonly LockMonitor _lockMonitor = new();

    // The settings window, lazily created on first open and reused while it stays open.
    private SettingsForm? _settingsForm;

    // The history viewer, lazily created on first open and reused while it stays open.
    private HistoryViewerForm? _historyForm;

    // The most recent set of live sessions, so a freshly-opened history viewer knows which sessions
    // are active without waiting for the next scan.
    private IReadOnlyList<ClaudeSession> _sessions = [];

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
        _overlay.ExternalNotifyToggleRequested += OnToggleExternalNotify;
        _overlay.HistoryRequested += OpenHistoryViewer;

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text    = "Claude Watch",
            Icon    = LoadTrayIcon(),
        };
        // Left-click opens the first-class settings window; right-click shows the slim menu below.
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        var trayMenu = new ContextMenuStrip();

        var header = new ToolStripMenuItem($"Claude Watch — v{AppInfo.Version}") { Enabled = false };
        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        var historyItem = new ToolStripMenuItem("Session history…");
        historyItem.Click += (_, _) => OpenHistoryViewer(null);
        var updateItem = new ToolStripMenuItem("Check for Updates…");
        updateItem.Click += (_, _) => CheckForUpdates();
        var exitItem = new ToolStripMenuItem("Exit Claude Watch");
        exitItem.Click += (_, _) => Exit();

        trayMenu.Items.Add(header);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(settingsItem);
        trayMenu.Items.Add(historyItem);
        trayMenu.Items.Add(updateItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = trayMenu;

        _monitor = new SessionMonitor();
        _monitor.SessionsChanged += OnSessionsChanged;
        _monitor.NeedsAttention  += OnNeedsAttention;
        _monitor.AwaitingInput   += OnAwaitingInput;
        // The plugin's /history command drops a one-shot trigger file the monitor turns into this event.
        _monitor.OpenHistoryRequested += OpenHistoryViewer;
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

        // Fires once, AutoCloseGraceMs after the last session ends. If still no sessions by then,
        // an auto-started tray exits. Armed/cancelled from OnSessionsChanged.
        _autoCloseTimer = new System.Windows.Forms.Timer { Interval = AutoCloseGraceMs };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            if (_sessions.Count == 0)
                Exit();
        };

        _overlay.Show();
        _overlay.SetUsageEnabled(_settings.ShowUsage);
        _overlay.SetExternalNotificationsAvailable(_settings.ExternalNotificationsEnabled);
        _monitor.Scan();

        if (_settings.ShowUsage)
        {
            _usageTimer.Start();
            RefreshUsage();
        }

        // First launch after an install: add the marketplace and install the Claude Code plugin in
        // the background so the user doesn't have to. Failures are silently skipped (treated as ok).
        if (Program.IsFirstRun)
            AutoInstallPlugin();
    }

    // Fire-and-forget plugin install on first run. Shows a tray balloon up front so the work is
    // visible, then a quiet success balloon; any failure is swallowed (the user can still enable it
    // later from Settings).
    private async void AutoInstallPlugin()
    {
        // Already set up from a previous machine state? Skip the work and the noise.
        var (marketplace, plugin) = PluginManager.ReadInstalledState();
        if (marketplace && plugin)
            return;

        ShowInfoBalloon("Claude Watch",
            "Setting up the Claude Code plugin…", ToolTipIcon.Info);

        try
        {
            var (ok, _) = await new PluginManager().EnableAsync();
            if (ok)
                ShowInfoBalloon("Claude Watch",
                    "Claude Code plugin installed. Restart open sessions to load it.", ToolTipIcon.Info);
        }
        catch { /* best-effort: skip on any failure */ }
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

        _settingsForm = new SettingsForm(_settings, _usageMonitor, _lastUsage);
        _settingsForm.UsageEnabledChanged    += SetUsageEnabled;
        _settingsForm.CheckForUpdatesRequested += (_, _) => CheckForUpdates();
        _settingsForm.TestNotificationRequested += ShowTestNotification;
        _settingsForm.ExternalNotificationsEnabledChanged += SetExternalNotificationsEnabled;
        _settingsForm.TestExternalNotificationRequested   += SendExternalTestNotification;
        _settingsForm.FormClosed             += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    // Opens (or re-focuses) the history viewer and points it at the given session. A null sessionId
    // (from the tray menu) lands on the most-recent session. Seeds the viewer with the current live
    // sessions so active indicators are right immediately.
    private void OpenHistoryViewer(string? sessionId)
    {
        if (_historyForm is { IsDisposed: false })
        {
            if (_historyForm.WindowState == FormWindowState.Minimized)
                _historyForm.WindowState = FormWindowState.Normal;
            _historyForm.SetActiveSessions(_sessions);
            _historyForm.SelectSession(sessionId);
            _historyForm.Activate();
            _historyForm.BringToFront();
            return;
        }

        _historyForm = new HistoryViewerForm();
        _historyForm.FormClosed += (_, _) => _historyForm = null;
        _historyForm.Show();
        _historyForm.SetActiveSessions(_sessions);
        _historyForm.SelectSession(sessionId);
        _historyForm.Activate();
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
        _sessions = sessions;
        _overlay.UpdateSessions(sessions);
        if (_historyForm is { IsDisposed: false })
            _historyForm.SetActiveSessions(sessions);
        // Refresh the overlay's mail glyphs from the per-session opt-in marker files just read.
        PushExternalNotifyGlyphs();
        ArmDeadlineTimer();

        _notifyIcon.Text = sessions.Count switch
        {
            0 => "Claude Watch — No active sessions",
            1 => "Claude Watch — 1 session",
            _ => $"Claude Watch — {sessions.Count} sessions",
        };

        MaybeHandleAutoClose(sessions.Count);
    }

    // Auto-close: only an auto-started tray with the setting on ever closes itself, so a manually-
    // opened window never vanishes under the user. Once at least one session has been seen, dropping
    // back to zero arms the grace timer; any session reappearing cancels it. The setting is read
    // live, so toggling it in settings takes effect immediately.
    private void MaybeHandleAutoClose(int sessionCount)
    {
        if (!Program.AutoStarted || !_settings.AutoCloseAfterLastSession)
        {
            _autoCloseTimer.Stop();
            return;
        }

        if (sessionCount > 0)
        {
            _seenSession = true;
            _autoCloseTimer.Stop();
            return;
        }

        // Zero sessions: hold off until we've actually seen one (don't exit during the startup race),
        // then start the grace countdown.
        if (!_seenSession)
            return;

        // Leave an already-running countdown alone. SessionsChanged fires on every scan (not just
        // on a real change), so the 30s reconcile poll — plus any file write in ~/.claude/sessions
        // (a busy session's .json heartbeat, a .mode rewrite) — re-enters here while still at zero.
        // Re-arming on each of those would reset the grace period so it never elapsed; instead the
        // countdown must measure time since sessions actually hit zero.
        if (_autoCloseTimer.Enabled)
            return;

        _autoCloseTimer.Start();
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
        // The overlay's own attention flash is always on; only the Windows balloon is gated.
        _overlay.TriggerAttention();

        if (_settings.NotificationsEnabled && _settings.NotifyOnDone)
            ShowSessionBalloon(NotificationKind.Done, session.ProjectName, session.Pid);

        MaybeSendExternal(NotificationKind.Done, session);
    }

    private void OnAwaitingInput(ClaudeSession session)
    {
        _overlay.TriggerAttention();

        if (_settings.NotificationsEnabled && _settings.NotifyOnWaitingInput)
            ShowSessionBalloon(NotificationKind.WaitingForInput, session.ProjectName, session.Pid);

        MaybeSendExternal(NotificationKind.WaitingForInput, session);
    }

    // Shows the desktop balloon for a session notification. A null pid means there's no real
    // session behind it (a settings "Test"), so a click won't try to focus a terminal.
    private void ShowSessionBalloon(NotificationKind kind, string projectName, string? pid)
    {
        _lastNotifiedPid = pid;
        switch (kind)
        {
            case NotificationKind.Done:
                _notifyIcon.BalloonTipTitle = "Claude Code — Done";
                _notifyIcon.BalloonTipText  = $"Waiting for you in {projectName}";
                _notifyIcon.BalloonTipIcon  = ToolTipIcon.Info;
                break;
            case NotificationKind.WaitingForInput:
                _notifyIcon.BalloonTipTitle = "Claude Code — Waiting for Input";
                _notifyIcon.BalloonTipText  = $"{projectName} needs your response";
                _notifyIcon.BalloonTipIcon  = ToolTipIcon.Warning;
                break;
        }
        _notifyIcon.ShowBalloonTip(8000);
    }

    // Fired by the settings window's per-type "Test" buttons: shows a sample balloon so the user
    // can preview exactly what that notification looks like, regardless of the saved toggles.
    private void ShowTestNotification(NotificationKind kind)
        => ShowSessionBalloon(kind, "example-project", null);

    // ── External (ntfy) notifications ─────────────────────────────────────────────
    // Flips a session's external-notify opt-in from the overlay's right-click menu by writing or
    // deleting its marker file — the same single source of truth the plugin's /afk command toggles,
    // so the two paths can never disagree. The follow-up scan re-reads the file and refreshes glyphs.
    private void OnToggleExternalNotify(string sessionId)
    {
        _monitor.ToggleExternalNotify(sessionId);
        _monitor.Scan();
    }

    // Pushes the set of opted-in sessions (those carrying a marker file, per the latest scan) to the
    // overlay so its mail glyphs and right-click wording match.
    private void PushExternalNotifyGlyphs()
        => _overlay.SetExternalNotifySessions(
            _sessions.Where(s => s.ExternalNotify).Select(s => s.SessionId).ToHashSet());

    // Mirrors the master switch into the overlay (it gates the glyph and the right-click item) and
    // persists it. The host/topic are saved by the settings window itself.
    private void SetExternalNotificationsEnabled(bool enabled)
    {
        _settings.ExternalNotificationsEnabled = enabled;
        _settings.Save();
        _overlay.SetExternalNotificationsAvailable(enabled);
    }

    // Pushes an external notification for a session, but only when the feature is on and that session
    // has opted in. Independent of the Windows-balloon per-type toggles above.
    private void MaybeSendExternal(NotificationKind kind, ClaudeSession session)
    {
        // A session pushes if it opted in (its marker file, set via right-click or /afk) OR the
        // account-wide AFK override is on and the screen is currently locked. The master switch gates both.
        bool optedIn   = session.ExternalNotify;
        bool afkActive = _settings.NotifyWhenLocked && _lockMonitor.IsLocked;
        if (!_settings.ExternalNotificationsEnabled || (!optedIn && !afkActive))
            return;

        var (title, body, tags) = kind == NotificationKind.Done
            ? ("Claude Code — Done", $"Waiting for you in {session.ProjectName}", "white_check_mark")
            : ("Claude Code — Waiting for Input", $"{session.ProjectName} needs your response", "bell");

        var host = _settings.NtfyHost;
        var topic = _settings.NtfyTopic;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(topic))
            return;

        // Attach an "Open session" action only when the session is remote-controlled (so the deep
        // link actually resolves) and the user has opted into including it.
        string? actionUrl = _settings.ExternalNotificationsIncludeRemoteLink && session.RemoteControlled
            ? $"https://claude.ai/code/{session.BridgeSessionId}"
            : null;

        // Fire-and-forget: a failed push must never stall or crash the monitor callback.
        _ = NtfyNotifier.SendAsync(host, topic, title, body, tags, actionUrl, "Open session");
    }

    // The settings window's "Send test notification": pushes a sample to the configured ntfy
    // host/topic and reports the outcome via a tray balloon, so misconfiguration is visible.
    private async void SendExternalTestNotification()
    {
        var host = _settings.NtfyHost;
        var topic = _settings.NtfyTopic;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(topic))
        {
            ShowInfoBalloon("Claude Watch — ntfy", "Enter a server URL and topic first.", ToolTipIcon.Warning);
            return;
        }

        var (ok, error) = await NtfyNotifier.SendAsync(
            host, topic, "Claude Watch — Test", "External notifications are working.", "bell");

        ShowInfoBalloon("Claude Watch — ntfy",
            ok ? "Test notification sent." : $"Failed to send: {error}",
            ok ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    // A tray balloon not tied to any session (so a click won't focus a stale terminal).
    private void ShowInfoBalloon(string title, string text, ToolTipIcon icon)
    {
        _lastNotifiedPid = null;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText  = text;
        _notifyIcon.BalloonTipIcon  = icon;
        _notifyIcon.ShowBalloonTip(5000);
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

    // Loads the multi-resolution app icon and picks the frame that best fits the tray at the
    // current DPI (the .ico ships a true 16px image), so the orange logo stays crisp and colour-
    // accurate instead of being downscaled from the 32px PNG.
    private static Icon LoadTrayIcon()
    {
        using var stream = typeof(OverlayApplicationContext).Assembly.GetManifestResourceStream("ClaudeWatch.icon.ico")!;
        return new Icon(stream, SystemInformation.SmallIconSize);
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
        _autoCloseTimer.Stop();
        _notifyIcon.Visible = false;
        if (_settingsForm is { IsDisposed: false })
            _settingsForm.Close();
        if (_historyForm is { IsDisposed: false })
            _historyForm.Close();
        _overlay.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reconcileTimer.Dispose();
            _deadlineTimer.Dispose();
            _usageTimer.Dispose();
            _autoCloseTimer.Dispose();
            _monitor.Dispose();
            _lockMonitor.Dispose();
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
            _settingsForm?.Dispose();
            _historyForm?.Dispose();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
