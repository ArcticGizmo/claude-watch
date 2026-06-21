namespace ClaudeWatch;

using System.Diagnostics;

/// <summary>
/// First-class settings window opened by left-clicking the tray icon. A single dark-themed,
/// vertically-stacked panel with four sections: About, Permission Mode (detection plugin),
/// Usage limits, and Updates. Reads/writes the shared <see cref="AppSettings"/> instance and
/// drives <see cref="PluginManager"/>/<see cref="UsageMonitor"/> directly; toggling usage and
/// checking for updates are raised as events so the owning context keeps timers and the overlay
/// in sync.
/// </summary>
internal sealed class SettingsForm : Form
{
    // Inner content width (~50% wider than the original 372). The client is sized to fit this
    // plus the 16px padding either side and room for a vertical scrollbar, so nothing clips.
    private const int ContentWidth = 552;

    private readonly AppSettings   _settings;
    private readonly PluginManager _pluginManager;
    private readonly UsageMonitor  _usageMonitor;

    private readonly Bitmap? _icon = LoadEmbeddedBitmap("ClaudeWatch.icon.png");

    // Permission Mode section.
    private ToggleSwitch _permToggle   = null!;
    private Spinner      _permSpinner  = null!;
    private Panel        _banner       = null!;
    private Label        _bannerLabel  = null!;
    private Button       _troubleshootBtn = null!;

    // Usage section.
    private ToggleSwitch     _usageToggle = null!;
    private UsageBarsControl _usageBars   = null!;
    private Button           _usageRefreshBtn = null!;

    // Notifications section. The master toggle gates the per-type sub-rows (toggle + Test button),
    // which dim while it's off.
    private ToggleSwitch _notifyMasterToggle = null!;
    private readonly List<(Label label, ToggleSwitch toggle, Button test)> _notifySubRows = new();

    // External notifications (ntfy) section. The host/topic boxes stay editable regardless of the
    // toggle, so they can be set up (and tested) before the feature is switched on.
    private ToggleSwitch _externalToggle = null!;
    private TextBox      _ntfyHostBox    = null!;
    private TextBox      _ntfyTopicBox   = null!;

    private UsageInfo _usage;

    private FlowLayoutPanel _root = null!;

    /// <summary>Raised when the user toggles "Show usage limits" (true = enabled).</summary>
    public event Action<bool>? UsageEnabledChanged;

    /// <summary>Raised when the user clicks "Check for Updates".</summary>
    public event EventHandler? CheckForUpdatesRequested;

    /// <summary>Raised when the user clicks a per-type "Test" button, to preview that notification.</summary>
    public event Action<NotificationKind>? TestNotificationRequested;

    /// <summary>Raised when the user toggles external (ntfy) notifications (true = enabled).</summary>
    public event Action<bool>? ExternalNotificationsEnabledChanged;

    /// <summary>Raised when the user clicks "Send test notification" for the external (ntfy) channel.</summary>
    public event Action? TestExternalNotificationRequested;

    public SettingsForm(AppSettings settings, PluginManager pluginManager,
                        UsageMonitor usageMonitor, UsageInfo currentUsage)
    {
        _settings      = settings;
        _pluginManager = pluginManager;
        _usageMonitor  = usageMonitor;
        _usage         = currentUsage;

        Text            = "Claude Watch Settings";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Theme.FormBg;
        ForeColor       = Theme.Fg;
        Font            = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize      = new Size(ContentWidth + 50, 780);
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        BuildLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    // Async work that shells out to the CLI must run after the handle exists.
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.UseDarkScrollBars(_root.Handle);
        await InitPermissionStateAsync();
    }

    // ── Layout ───────────────────────────────────────────────────────────────────
    private void BuildLayout()
    {
        var root = _root = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoScroll    = true,
            Padding       = new Padding(16),
            BackColor     = Theme.FormBg,
        };

        BuildAboutSection(root);
        root.Controls.Add(Separator());
        BuildPermissionSection(root);
        root.Controls.Add(Separator());
        BuildUsageSection(root);
        root.Controls.Add(Separator());
        BuildNotificationsSection(root);
        root.Controls.Add(Separator());
        BuildExternalSection(root);
        root.Controls.Add(Separator());
        BuildUpdatesSection(root);

        Controls.Add(root);

        _usageBars.SetOn(_settings.ShowUsage);
        _usageBars.SetUsage(_usage);
    }

    private void BuildAboutSection(FlowLayoutPanel root)
    {
        root.Controls.Add(SectionTitle("About"));

        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 0, 0, 6),
        };
        if (_icon != null)
            header.Controls.Add(new PictureBox
            {
                Image    = _icon,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size     = new Size(32, 32),
                Margin   = new Padding(0, 0, 10, 0),
            });
        header.Controls.Add(new Label
        {
            Text      = $"Claude Watch\nv{AppInfo.Version}",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Margin    = new Padding(0, 2, 0, 0),
        });
        root.Controls.Add(header);

        root.Controls.Add(LinkRow("GitHub repository", AppInfo.RepoUrl));
        root.Controls.Add(LinkRow("Report an issue on GitHub", AppInfo.IssuesUrl));
    }

    private void BuildPermissionSection(FlowLayoutPanel root)
    {
        _permToggle = MakeToggle();
        _permToggle.CheckedChanged += OnPermissionToggled;
        _permSpinner = new Spinner();
        root.Controls.Add(TitleRow("Permission Mode", _permToggle, _permSpinner));

        // Start in the busy state — the real toggle position isn't known until the async probe
        // in OnShown completes, so dim the toggle and spin until then.
        SetPermBusy(true);

        root.Controls.Add(BodyText(
            "A Claude Code plugin reports each session's live permission mode to Claude Watch, " +
            "shown as a coloured badge next to that session in the overlay:"));

        root.Controls.Add(new ModeLegend { Width = ContentWidth, Margin = new Padding(0, 2, 0, 8) });

        // Warning banner — only visible when detection is enabled but not actually active.
        _banner = BuildBanner();
        root.Controls.Add(_banner);
    }

    private Panel BuildBanner()
    {
        var banner = new Panel
        {
            Width     = ContentWidth,
            Height    = 88,
            BackColor = Theme.BannerBg,
            Margin    = new Padding(0, 0, 0, 4),
            Visible   = false,
        };
        banner.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.BannerBorder);
            e.Graphics.DrawRectangle(pen, 0, 0, banner.Width - 1, banner.Height - 1);
        };

        _bannerLabel = new Label
        {
            AutoSize    = true,
            MaximumSize = new Size(ContentWidth - 24, 0),
            ForeColor   = Theme.BannerFg,
            BackColor   = Theme.BannerBg,
            Location    = new Point(12, 10),
        };
        banner.Controls.Add(_bannerLabel);

        var buttons = ButtonRow();
        buttons.Location = new Point(8, 48);
        _troubleshootBtn = MakeButton("Troubleshoot");
        _troubleshootBtn.Click += (_, _) => OnTroubleshoot();
        var copyBtn = MakeButton("Copy install commands");
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(PluginManager.FallbackCommands); } catch { }
            _bannerLabel.Text = "Install commands copied — paste them into a Claude Code session, then Troubleshoot.";
        };
        buttons.Controls.Add(_troubleshootBtn);
        buttons.Controls.Add(copyBtn);
        banner.Controls.Add(buttons);

        return banner;
    }

    private void BuildUsageSection(FlowLayoutPanel root)
    {
        _usageToggle = MakeToggle();
        _usageToggle.Checked = _settings.ShowUsage;
        _usageToggle.CheckedChanged += (_, _) =>
        {
            UsageEnabledChanged?.Invoke(_usageToggle.Checked);
            _usageRefreshBtn.Enabled = _usageToggle.Checked;
            _usageBars.SetOn(_usageToggle.Checked);
        };
        root.Controls.Add(TitleRow("Usage limits", _usageToggle));

        root.Controls.Add(BodyText("Your account-wide 5-hour and weekly rate-limit usage."));

        _usageBars = new UsageBarsControl { Width = ContentWidth, Margin = new Padding(0, 2, 0, 6) };
        root.Controls.Add(_usageBars);

        var row = ButtonRow();
        _usageRefreshBtn = MakeButton("Refresh");
        _usageRefreshBtn.Enabled = _settings.ShowUsage;
        _usageRefreshBtn.Click += async (_, _) =>
        {
            if (!_settings.ShowUsage) return;
            _usageRefreshBtn.Enabled = false;
            _usage = await _usageMonitor.FetchAsync();
            if (IsDisposed) return;
            _usageBars.SetUsage(_usage);
            _usageRefreshBtn.Enabled = _settings.ShowUsage;
        };
        row.Controls.Add(_usageRefreshBtn);
        root.Controls.Add(row);
    }

    private void BuildNotificationsSection(FlowLayoutPanel root)
    {
        _notifyMasterToggle = MakeToggle();
        _notifyMasterToggle.Checked = _settings.NotificationsEnabled;
        _notifyMasterToggle.CheckedChanged += (_, _) =>
        {
            _settings.NotificationsEnabled = _notifyMasterToggle.Checked;
            _settings.Save();
            ApplyNotifyEnabled();
        };
        root.Controls.Add(TitleRow("Notifications", _notifyMasterToggle));

        root.Controls.Add(BodyText(
            "Windows desktop notifications when a session needs you. Turn the whole feature off, " +
            "or just the types you don't want. Use Test to preview one."));

        root.Controls.Add(BuildNotifyRow(
            "Done — a session finished working",
            _settings.NotifyOnDone,
            v => { _settings.NotifyOnDone = v; _settings.Save(); },
            NotificationKind.Done));

        root.Controls.Add(BuildNotifyRow(
            "Waiting for input — a session is blocked on a prompt",
            _settings.NotifyOnWaitingInput,
            v => { _settings.NotifyOnWaitingInput = v; _settings.Save(); },
            NotificationKind.WaitingForInput));

        ApplyNotifyEnabled();
    }

    // An indented sub-row for one notification type: a label, a "Test" button, and a toggle on the
    // right. The trio is tracked so ApplyNotifyEnabled can dim it when the master switch is off.
    private Panel BuildNotifyRow(string text, bool initial, Action<bool> onChanged, NotificationKind kind)
    {
        var row = new Panel
        {
            Width  = ContentWidth,
            Height = 30,
            Margin = new Padding(0, 2, 0, 4),
        };

        var label = new Label
        {
            Text      = text,
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        var toggle = MakeToggle();
        toggle.Checked  = initial;
        toggle.CheckedChanged += (_, _) => onChanged(toggle.Checked);
        toggle.Location = new Point(ContentWidth - toggle.Width, (row.Height - toggle.Height) / 2);
        toggle.Anchor   = AnchorStyles.Top | AnchorStyles.Right;

        var test = MakeButton("Test");
        test.AutoSize  = false;
        test.Size      = new Size(56, 24);
        test.Margin    = new Padding(0);
        test.Padding   = new Padding(0);
        test.TextAlign = ContentAlignment.MiddleCenter;
        test.Location  = new Point(toggle.Left - test.Width - 12, (row.Height - test.Height) / 2);
        test.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
        test.Click   += (_, _) => TestNotificationRequested?.Invoke(kind);

        row.Controls.Add(label);
        row.Controls.Add(test);
        row.Controls.Add(toggle);

        _notifySubRows.Add((label, toggle, test));
        return row;
    }

    // Dims the per-type sub-rows (label, toggle, Test) whenever the master switch is off.
    private void ApplyNotifyEnabled()
    {
        bool on = _notifyMasterToggle.Checked;
        foreach (var (label, toggle, test) in _notifySubRows)
        {
            toggle.Enabled   = on;
            test.Enabled     = on;
            label.ForeColor  = on ? Theme.Fg : Theme.Muted;
        }
    }

    // External notifications via ntfy. The toggle only gates whether pushes are sent (and whether
    // the per-session opt-in is offered in the overlay); the host/topic boxes stay enabled either
    // way so they can be filled in and tested before turning the feature on.
    private void BuildExternalSection(FlowLayoutPanel root)
    {
        _externalToggle = MakeToggle();
        _externalToggle.Checked = _settings.ExternalNotificationsEnabled;
        _externalToggle.CheckedChanged += (_, _) =>
        {
            _settings.ExternalNotificationsEnabled = _externalToggle.Checked;
            _settings.Save();
            ExternalNotificationsEnabledChanged?.Invoke(_externalToggle.Checked);
        };
        root.Controls.Add(TitleRow("External notifications", _externalToggle));

        root.Controls.Add(BodyText(
            "Also push \"Done\" and \"Waiting for input\" alerts to your phone or other devices via " +
            "ntfy. Enter your server and topic below, then enable it per session by right-clicking " +
            "that session in the overlay."));

        // Default the host to the public server, but only in-memory until the box is edited — opening
        // settings shouldn't silently rewrite settings.json.
        string host = string.IsNullOrWhiteSpace(_settings.NtfyHost) ? "https://ntfy.sh" : _settings.NtfyHost!;
        _settings.NtfyHost = host;

        root.Controls.Add(FieldCaption("Server URL"));
        _ntfyHostBox = MakeTextBox(host);
        _ntfyHostBox.TextChanged += (_, _) => _settings.NtfyHost = _ntfyHostBox.Text;
        _ntfyHostBox.Leave       += (_, _) => _settings.Save();
        root.Controls.Add(_ntfyHostBox);

        root.Controls.Add(FieldCaption("Topic"));
        _ntfyTopicBox = MakeTextBox(_settings.NtfyTopic ?? "");
        _ntfyTopicBox.TextChanged += (_, _) => _settings.NtfyTopic = _ntfyTopicBox.Text;
        _ntfyTopicBox.Leave       += (_, _) => _settings.Save();
        root.Controls.Add(_ntfyTopicBox);

        var row = ButtonRow();
        row.Margin = new Padding(0, 4, 0, 4);
        var testBtn = MakeButton("Send test notification");
        testBtn.Click += (_, _) => { _settings.Save(); TestExternalNotificationRequested?.Invoke(); };
        row.Controls.Add(testBtn);
        root.Controls.Add(row);
    }

    private void BuildUpdatesSection(FlowLayoutPanel root)
    {
        root.Controls.Add(SectionTitle("Updates"));
        root.Controls.Add(BodyText($"Currently running v{AppInfo.Version}."));

        var row = ButtonRow();
        row.Margin = new Padding(0, 0, 0, 24);  // breathing room at the bottom of the window
        var checkBtn = MakeButton("Check for Updates");
        checkBtn.Click += (_, _) => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
        row.Controls.Add(checkBtn);
        root.Controls.Add(row);
    }

    // ── Public updates from the owner ────────────────────────────────────────────
    /// <summary>Pushes a fresh usage reading in (e.g. after the context's periodic poll).</summary>
    public void UpdateUsage(UsageInfo usage)
    {
        _usage = usage;
        if (!IsDisposed)
            _usageBars.SetUsage(usage);
    }

    // ── Permission Mode logic ────────────────────────────────────────────────────
    // On open: probe the plugin's real state, default the toggle from it (or from saved intent),
    // and surface a banner if the user wants detection on but it isn't actually live.
    private async Task InitPermissionStateAsync()
    {
        var health = await _pluginManager.GetHealthAsync();
        if (IsDisposed) return;

        bool installed = health is PluginHealth.Healthy or PluginHealth.Disabled;
        bool intent    = _settings.PermissionDetectionEnabled ?? installed;
        _permToggle.SetCheckedSilently(intent);
        SetPermBusy(false);
        ApplyHealth(health);
    }

    private async void OnPermissionToggled(object? sender, EventArgs e)
    {
        bool intent = _permToggle.Checked;
        _settings.PermissionDetectionEnabled = intent;
        _settings.Save();

        SetPermBusy(true);
        _bannerLabel.Text = intent ? "Enabling detection…" : "Disabling detection…";
        _banner.Visible   = true;

        if (intent) await _pluginManager.InstallAsync();
        else        await _pluginManager.UninstallAsync();

        var health = await _pluginManager.GetHealthAsync();
        if (IsDisposed) return;

        SetPermBusy(false);
        ApplyHealth(health);
    }

    // Toggles the busy state of the Permission Mode control: dims the switch and spins while an
    // install/uninstall/probe is in flight, so the snap to its real position reads as a load.
    private void SetPermBusy(bool busy)
    {
        _permToggle.Enabled = !busy;
        if (busy) _permSpinner.Start();
        else      _permSpinner.Stop();
    }

    // Re-runs diagnosis: re-checks health and, unless the CLI is missing, attempts an
    // (idempotent) reinstall to fix a partial/disabled state, then refreshes the banner.
    private async void OnTroubleshoot()
    {
        _troubleshootBtn.Enabled = false;
        SetPermBusy(true);
        _bannerLabel.Text = "Diagnosing…";

        var health = await _pluginManager.GetHealthAsync();
        if (health == PluginHealth.CliMissing)
        {
            if (IsDisposed) return;
            _troubleshootBtn.Enabled = true;
            SetPermBusy(false);
            _bannerLabel.Text =
                "Claude CLI not found on PATH. Copy the install commands and run them in a Claude " +
                "Code session, or make sure 'claude' is on your PATH, then Troubleshoot again.";
            return;
        }

        await _pluginManager.InstallAsync();
        health = await _pluginManager.GetHealthAsync();
        if (IsDisposed) return;

        _troubleshootBtn.Enabled = true;
        SetPermBusy(false);
        ApplyHealth(health);
        if (health != PluginHealth.Healthy)
            _bannerLabel.Text =
                "Still not active: " + PluginManager.Describe(health) +
                ". Restart any open Claude Code sessions and Troubleshoot again.";
    }

    // Shows/hides the warning banner based on the user's intent vs. the plugin's real health.
    private void ApplyHealth(PluginHealth health)
    {
        bool issue = _permToggle.Checked && health != PluginHealth.Healthy;
        _banner.Visible = issue;
        if (issue)
            _bannerLabel.Text = "Detection is enabled but not active: " + PluginManager.Describe(health) + ".";
    }

    // ── Control factories ───────────────────────────────────────────────────────────
    private static Label SectionTitle(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Title,
        Font      = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
        Margin    = new Padding(0, 4, 0, 8),
    };

    // A section header with a right-justified toggle on the same row, optionally with a spinner
    // sitting just to the left of the toggle.
    private Panel TitleRow(string title, ToggleSwitch toggle, Spinner? spinner = null)
    {
        var row = new Panel
        {
            Width  = ContentWidth,
            Height = 30,
            Margin = new Padding(0, 4, 0, 8),
        };
        var label = new Label
        {
            Text      = title,
            AutoSize  = true,
            ForeColor = Theme.Title,
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
            Location  = new Point(0, 2),
        };
        toggle.Location = new Point(ContentWidth - toggle.Width, (row.Height - toggle.Height) / 2);
        toggle.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(label);
        row.Controls.Add(toggle);

        if (spinner != null)
        {
            spinner.Location = new Point(toggle.Left - spinner.Width - 10, (row.Height - spinner.Height) / 2);
            spinner.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(spinner);
        }
        return row;
    }

    private static ToggleSwitch MakeToggle() => new() { Margin = new Padding(0) };

    // A small muted caption sitting just above a text field.
    private static Label FieldCaption(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Muted,
        Margin    = new Padding(0, 2, 0, 2),
    };

    // A dark-themed single-line text box matching the rest of the settings surface.
    private static TextBox MakeTextBox(string value) => new()
    {
        Text        = value,
        Width       = ContentWidth,
        BackColor   = Theme.ButtonBg,
        ForeColor   = Theme.Fg,
        BorderStyle = BorderStyle.FixedSingle,
        Font        = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
        Margin      = new Padding(0, 0, 0, 8),
    };

    private static Label BodyText(string text) => new()
    {
        Text        = text,
        AutoSize    = true,
        MaximumSize = new Size(ContentWidth, 0),  // wrap long lines, auto height
        ForeColor   = Theme.Muted,
        Margin      = new Padding(0, 0, 0, 6),
    };

    private LinkLabel LinkRow(string text, string url)
    {
        var link = new LinkLabel
        {
            Text             = text,
            AutoSize         = true,
            LinkColor        = Theme.Accent,
            ActiveLinkColor  = Theme.AccentHover,
            VisitedLinkColor = Theme.Accent,
            LinkBehavior     = LinkBehavior.HoverUnderline,
            BackColor        = Theme.FormBg,
            Margin           = new Padding(0, 0, 0, 4),
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        return link;
    }

    private static Panel Separator() => new()
    {
        Height    = 1,
        Width     = ContentWidth,
        BackColor = Theme.Border,
        Margin    = new Padding(0, 12, 0, 12),
    };

    private static FlowLayoutPanel ButtonRow() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents  = false,
        AutoSize      = true,
        AutoSizeMode  = AutoSizeMode.GrowAndShrink,
        Margin        = new Padding(0, 0, 0, 4),
    };

    private static Button MakeButton(string text)
    {
        var b = new Button
        {
            Text      = text,
            AutoSize  = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Fg,
            BackColor = Theme.ButtonBg,
            Padding   = new Padding(8, 4, 8, 4),
            Margin    = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor        = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
        b.FlatAppearance.MouseDownBackColor = Theme.Border;
        return b;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static Bitmap? LoadEmbeddedBitmap(string resourceName)
    {
        try
        {
            using var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null;
        }
        catch { return null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icon?.Dispose();
        base.Dispose(disposing);
    }
}
