namespace ClaudeWatch;

using System.Diagnostics;

/// <summary>
/// First-class settings window opened by left-clicking the tray icon. A dark-themed window split
/// into a fixed-width left navigation rail and a resizable content area. The nav switches between
/// pages: Getting started (banner, feature overview, permission-mode badge legend), Plugin Control,
/// Usage, Notifications (Windows desktop + external ntfy), and About (info + updates). Reads/writes
/// the shared <see cref="AppSettings"/> instance and drives <see cref="UsageMonitor"/> directly;
/// toggling usage and checking for updates are raised as events so the owning context keeps timers
/// and the overlay in sync.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int NavWidth = 178;
    private const int PagePad  = 16;

    // Slightly darker than the form body so the nav rail reads as a distinct sidebar.
    private static readonly Color NavBg = Color.FromArgb(18, 18, 24);

    private readonly AppSettings  _settings;
    private readonly UsageMonitor _usageMonitor;

    private readonly Bitmap? _icon = LoadEmbeddedBitmap("ClaudeWatch.icon.png");

    // Shell.
    private FlowLayoutPanel _navPanel    = null!;
    private Panel           _contentHost = null!;
    private readonly Dictionary<string, FlowLayoutPanel> _pages = new();
    private readonly List<(string key, Panel item, Label label, Panel accent)> _navItems = new();
    private string _currentKey = "";

    // Fluid-width bookkeeping. The window is resizable, so every full-width control is registered
    // here and re-sized whenever the content area changes. Width controls get their Width set to the
    // available width minus an optional inset; wrap labels get their MaximumSize updated so AutoSize
    // re-wraps to the new width.
    private readonly List<(Control c, int inset)> _fluidWidth = new();
    private readonly List<Label> _fluidWrap = new();

    // Usage section.
    private ToggleSwitch     _usageToggle        = null!;
    private ToggleSwitch     _expectedRateToggle = null!;
    private UsageBarsControl _usageBars          = null!;
    private Button           _usageRefreshBtn    = null!;

    // Notifications section. The master toggle gates the per-type sub-rows (toggle + Test button),
    // which dim while it's off.
    private ToggleSwitch _notifyMasterToggle = null!;
    private readonly List<(Label label, ToggleSwitch toggle, Button test)> _notifySubRows = new();

    // External notifications (ntfy) section. The host/topic boxes stay editable regardless of the
    // toggle, so they can be set up (and tested) before the feature is switched on.
    private ToggleSwitch _externalToggle = null!;
    private TextBox      _ntfyHostBox    = null!;
    private TextBox      _ntfyTopicBox   = null!;
    private QrCodeForm?  _topicQrForm;
    // Sub-row toggle: include the claude.ai deep link as an action for remote-controlled sessions.
    // Dimmed while the external master toggle is off.
    private ToggleSwitch _remoteLinkToggle = null!;
    private Label        _remoteLinkLabel  = null!;
    // Sub-row toggle: push any session's alert while the screen is locked, without a per-session
    // opt-in. Dimmed while the external master toggle is off.
    private ToggleSwitch _lockNotifyToggle = null!;
    private Label        _lockNotifyLabel  = null!;

    // Automation section. Two independent toggles persisted straight to settings: the SessionStart
    // hook reads auto-start from settings.json, and the owning context reads auto-close live, so
    // neither needs an event back to the owner.
    private ToggleSwitch _autoStartToggle = null!;
    private ToggleSwitch _autoCloseToggle = null!;

    // Quick links section.
    private ToggleSwitch _gitKrakenToggle = null!;
    private ToggleSwitch _slackToggle     = null!;

    private UsageInfo _usage;

    /// <summary>Raised when the user toggles "Show usage limits" (true = enabled).</summary>
    public event Action<bool>? UsageEnabledChanged;

    /// <summary>Raised when the user toggles "Show expected rate marker" (true = enabled).</summary>
    public event Action<bool>? ExpectedRateChanged;

    /// <summary>Raised when the user clicks "Check for Updates".</summary>
    public event EventHandler? CheckForUpdatesRequested;

    /// <summary>Raised when the user clicks a per-type "Test" button, to preview that notification.</summary>
    public event Action<NotificationKind>? TestNotificationRequested;

    /// <summary>Raised when the user toggles external (ntfy) notifications (true = enabled).</summary>
    public event Action<bool>? ExternalNotificationsEnabledChanged;

    /// <summary>Raised when the user clicks "Send test notification" for the external (ntfy) channel.</summary>
    public event Action? TestExternalNotificationRequested;

    /// <summary>Raised when the user toggles "Show GitKraken button" (true = enabled).</summary>
    public event Action<bool>? GitKrakenEnabledChanged;

    /// <summary>Raised when the user toggles "Show Slack button" (true = enabled).</summary>
    public event Action<bool>? SlackEnabledChanged;

    public SettingsForm(AppSettings settings, UsageMonitor usageMonitor, UsageInfo currentUsage)
    {
        _settings     = settings;
        _usageMonitor = usageMonitor;
        _usage        = currentUsage;

        Text            = "Claude Watch Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        MinimizeBox     = true;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Theme.FormBg;
        ForeColor       = Theme.Fg;
        Font            = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        MinimumSize     = new Size(748, 560);
        ClientSize      = new Size(880, 660);
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        BuildLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        foreach (var page in _pages.Values)
            NativeMethods.UseDarkScrollBars(page.Handle);
        ApplyFluidWidth();
    }

    // ── Shell ─────────────────────────────────────────────────────────────────────
    private void BuildLayout()
    {
        _navPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Left,
            Width         = NavWidth,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            BackColor     = NavBg,
            Padding       = new Padding(0, 8, 0, 0),
        };

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.FormBg };
        _contentHost.Resize += (_, _) => ApplyFluidWidth();

        AddPage("start",        "Getting started", BuildGettingStartedPage);
        AddPage("plugin",       "Plugin Control",  BuildPluginPage);
        AddPage("usage",        "Usage Limits",    BuildUsagePage);
        AddPage("notify",       "Notifications",   BuildNotificationsPage);
        AddPage("auto",         "Automation",      BuildAutomationPage);
        AddPage("quicklinks",   "Quick Links",      BuildQuickLinksPage);
        AddPage("about",        "About",           BuildAboutPage);

        // Add the Fill host first (so it sits behind) and the Left rail second, so the rail claims
        // its edge and the host fills the remainder.
        Controls.Add(_contentHost);
        Controls.Add(_navPanel);

        _usageBars.SetOn(_settings.ShowUsage);
        _usageBars.SetUsage(_usage);

        SelectPage("start");
    }

    // Builds a page panel, runs its content builder, and registers the matching nav item.
    private void AddPage(string key, string title, Action<FlowLayoutPanel> build)
    {
        var page = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoScroll    = true,
            Padding       = new Padding(PagePad),
            BackColor     = Theme.FormBg,
            Visible       = false,
        };
        build(page);
        _pages[key] = page;
        _contentHost.Controls.Add(page);
        AddNavItem(key, title);
    }

    // A single nav rail entry: a full-width row with a left accent bar (shown when selected) and a
    // left-aligned label. Hover lightens the background unless the row is the active page.
    private void AddNavItem(string key, string title)
    {
        var item = new Panel
        {
            Width     = NavWidth,
            Height    = 44,
            Margin    = new Padding(0),
            Cursor    = Cursors.Hand,
            BackColor = NavBg,
        };
        var accent = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Theme.Accent, Visible = false };
        var label  = new Label
        {
            Text      = title,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(16, 0, 8, 0),
            ForeColor = Theme.Muted,
            BackColor = NavBg,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };
        item.Controls.Add(label);
        item.Controls.Add(accent);

        void Select() => SelectPage(key);
        item.Click  += (_, _) => Select();
        label.Click += (_, _) => Select();

        void Enter()
        {
            if (_currentKey == key) return;
            item.BackColor = Theme.ButtonBg;
            label.BackColor = Theme.ButtonBg;
        }
        void Leave()
        {
            if (_currentKey == key) return;
            item.BackColor = NavBg;
            label.BackColor = NavBg;
        }
        item.MouseEnter  += (_, _) => Enter();
        item.MouseLeave  += (_, _) => Leave();
        label.MouseEnter += (_, _) => Enter();
        label.MouseLeave += (_, _) => Leave();

        _navPanel.Controls.Add(item);
        _navItems.Add((key, item, label, accent));
    }

    // Shows the chosen page (hiding the rest) and restyles the nav rail to mark it active.
    private void SelectPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        _currentKey = key;

        foreach (var kv in _pages)
            kv.Value.Visible = kv.Key == key;
        page.BringToFront();

        foreach (var (k, item, label, accent) in _navItems)
        {
            bool sel = k == key;
            accent.Visible  = sel;
            item.BackColor  = sel ? Theme.ButtonBg : NavBg;
            label.BackColor = sel ? Theme.ButtonBg : NavBg;
            label.ForeColor = sel ? Theme.Title    : Theme.Muted;
        }

        ApplyFluidWidth();
    }

    // The width available to full-width page controls: the content area minus page padding and a
    // reserved vertical-scrollbar gutter (so a scrolling page never also shows a horizontal bar).
    private int FluidWidth()
    {
        int w = _contentHost.ClientSize.Width - PagePad * 2 - (SystemInformation.VerticalScrollBarWidth + 4);
        return Math.Max(200, w);
    }

    // Re-flows every registered full-width control to the current available width.
    private void ApplyFluidWidth()
    {
        if (_contentHost is null) return;
        int w = FluidWidth();
        foreach (var (c, inset) in _fluidWidth)
            c.Width = Math.Max(40, w - inset);
        foreach (var l in _fluidWrap)
            l.MaximumSize = new Size(w, 0);
    }

    // ── Getting started ─────────────────────────────────────────────────────────────
    private void BuildGettingStartedPage(FlowLayoutPanel page)
    {
        BuildBanner(page);

        page.Controls.Add(SectionTitle("What it does"));
        page.Controls.Add(BodyText(
            "•  See every active Claude Code session in one floating overlay — Idle, Running, or " +
            "Needs Attention at a glance. Click a session to jump to its terminal; drag the overlay " +
            "to dock it on the left or right."));
        page.Controls.Add(BodyText(
            "•  Get a desktop notification the moment a session finishes or is waiting on you."));
        page.Controls.Add(BodyText(
            "•  Push those same alerts to your phone or other devices via ntfy, so you're covered " +
            "when you're away from your desk."));
        page.Controls.Add(BodyText(
            "•  Keep an eye on your 5-hour and weekly usage limits without leaving your desktop."));
        page.Controls.Add(BodyText(
            "•  Install the companion Claude Code plugin for live permission-mode badges, /afk, and " +
            "/history."));

        page.Controls.Add(Separator());

        page.Controls.Add(SectionTitle("Permission mode badges"));
        page.Controls.Add(BodyText(
            "When the Claude Code plugin is installed, each session's live permission mode is shown " +
            "as a coloured badge next to that session in the overlay:"));
        var legend = new ModeLegend { Margin = new Padding(0, 2, 0, 8) };
        _fluidWidth.Add((legend, 0));
        page.Controls.Add(legend);
    }

    // Centred app banner: the logo, the app name, and the tagline, all horizontally centred and
    // re-laid-out whenever the banner's width changes.
    private void BuildBanner(FlowLayoutPanel page)
    {
        var banner = new Panel { Height = 156, Margin = new Padding(0, 8, 0, 8), BackColor = Theme.FormBg };

        PictureBox? pic = _icon != null
            ? new PictureBox { Image = _icon, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64) }
            : null;
        var name = new Label
        {
            Text      = "Claude Watch",
            AutoSize  = true,
            ForeColor = Theme.Title,
            Font      = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point),
        };
        var tag = new Label
        {
            Text      = "Never miss what Claude's working on",
            AutoSize  = true,
            ForeColor = Theme.Muted,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };
        if (pic != null) banner.Controls.Add(pic);
        banner.Controls.Add(name);
        banner.Controls.Add(tag);

        void Layout()
        {
            int cx = banner.Width / 2;
            int y  = 14;
            if (pic != null) { pic.Location = new Point(cx - pic.Width / 2, y); y += pic.Height + 10; }
            name.Location = new Point(cx - name.Width / 2, y); y += name.Height + 4;
            tag.Location  = new Point(cx - tag.Width  / 2, y);
        }
        banner.Resize += (_, _) => Layout();
        _fluidWidth.Add((banner, 0));
        Layout();

        page.Controls.Add(banner);
    }

    // ── Plugin Control ────────────────────────────────────────────────────────────
    // The install commands for the claude-watch Claude Code plugin (marketplace ref name@marketplace).
    private const string PluginInstallCommands =
        "/plugin marketplace add ArcticGizmo/claude-watch\n/plugin install claude-watch@claude-watch";

    // Status of the claude-watch plugin and the single action button (Enable / Update / Up to date).
    private Label   _pluginStatusLabel = null!;
    private Button  _pluginActionBtn   = null!;
    private Spinner _pluginSpinner     = null!;
    private PluginStatus _pluginStatus = PluginStatus.UpToDate;

    // One-click install/update of the claude-watch plugin via the claude CLI. The button's label and
    // enabled-state follow an async status check (spinner shown while it runs); the manual commands
    // remain below as a fallback when the CLI isn't on PATH.
    private void BuildPluginPage(FlowLayoutPanel page)
    {
        page.Controls.Add(SectionTitle("Plugin Control"));

        page.Controls.Add(BodyText(
            "Claude Watch pairs with a small Claude Code plugin. With it installed you get:"));
        page.Controls.Add(BodyText(
            "•  Live permission-mode badges next to each session in the overlay — Plan, Accept edits, " +
            "Auto, and Bypass."));
        page.Controls.Add(BodyText(
            "•  /afk — toggle external (phone) notifications for the current session without leaving " +
            "Claude Code."));
        page.Controls.Add(BodyText(
            "•  /history — open the current session's history in Claude Watch."));
        page.Controls.Add(BodyText(
            "Claude Watch can add the marketplace and install the plugin for you. If a newer version " +
            "is published later, use Update to pull it in."));

        // Action row: the Enable/Update button with a spinner beside it while a check or install runs.
        var row = ButtonRow();
        _pluginActionBtn = MakeButton("Enable");
        _pluginActionBtn.Enabled = false;
        _pluginActionBtn.Click += async (_, _) => await RunPluginActionAsync();
        row.Controls.Add(_pluginActionBtn);

        _pluginSpinner = new Spinner { Margin = new Padding(2, 4, 0, 0) };
        row.Controls.Add(_pluginSpinner);
        page.Controls.Add(row);

        _pluginStatusLabel = BodyText("Checking plugin status…");
        page.Controls.Add(_pluginStatusLabel);

        // Manual fallback for when the CLI isn't reachable from the app.
        page.Controls.Add(FieldCaption("Or run these in any Claude Code session:"));
        page.Controls.Add(CodeBlock(PluginInstallCommands));
        var copyRow = ButtonRow();
        var copyBtn = MakeButton("Copy install commands");
        copyBtn.Click += (_, _) => { try { Clipboard.SetText(PluginInstallCommands); } catch { } };
        copyRow.Controls.Add(copyBtn);
        page.Controls.Add(copyRow);

        // Kick off the initial status check (don't block the UI thread building the form).
        _ = RefreshPluginStatusAsync();
    }

    // Runs the async status check, driving the spinner and then the button/label.
    private async Task RefreshPluginStatusAsync()
    {
        SetPluginBusy("Checking plugin status…");
        var status = await new PluginManager().GetStatusAsync();
        if (IsDisposed) return;
        ApplyPluginStatus(status);
    }

    // Runs Enable or Update depending on the current status, then re-checks to refresh the button.
    private async Task RunPluginActionAsync()
    {
        var mgr = new PluginManager();
        bool updating = _pluginStatus == PluginStatus.UpdateAvailable;
        SetPluginBusy(updating ? "Updating the plugin…" : "Installing the plugin…");

        var (ok, message) = updating ? await mgr.UpdateAsync() : await mgr.EnableAsync();
        if (IsDisposed) return;

        if (!ok)
        {
            // Surface the failure but re-enable the button so the user can retry.
            _pluginSpinner.Spinning = false;
            _pluginStatusLabel.Text = message;
            _pluginActionBtn.Enabled = true;
            return;
        }

        // Re-check so the button settles to "Up to date" (or surfaces any remaining work).
        await RefreshPluginStatusAsync();
        if (!IsDisposed)
            _pluginStatusLabel.Text = message;
    }

    // Shows the spinner and disables the button while an async plugin operation is in flight.
    private void SetPluginBusy(string message)
    {
        _pluginSpinner.Spinning  = true;
        _pluginActionBtn.Enabled = false;
        _pluginStatusLabel.Text  = message;
    }

    // Maps a resolved status onto the button label/enabled-state and the status caption.
    private void ApplyPluginStatus(PluginStatus status)
    {
        _pluginStatus = status;
        _pluginSpinner.Spinning = false;

        switch (status)
        {
            case PluginStatus.NeedsEnable:
                _pluginActionBtn.Text    = "Enable";
                _pluginActionBtn.Enabled = true;
                _pluginStatusLabel.Text  = "Not installed yet.";
                break;
            case PluginStatus.UpdateAvailable:
                _pluginActionBtn.Text    = "Update";
                _pluginActionBtn.Enabled = true;
                _pluginStatusLabel.Text  = "A newer version is available.";
                break;
            case PluginStatus.UpToDate:
                _pluginActionBtn.Text    = "Up to date";
                _pluginActionBtn.Enabled = false;
                _pluginStatusLabel.Text  = "Installed and up to date.";
                break;
            case PluginStatus.CliMissing:
                _pluginActionBtn.Text    = "Enable";
                _pluginActionBtn.Enabled = false;
                _pluginStatusLabel.Text  = "claude CLI not found on PATH — run the commands below manually.";
                break;
        }
    }

    // ── Usage ───────────────────────────────────────────────────────────────────────
    private void BuildUsagePage(FlowLayoutPanel page)
    {
        _usageToggle = MakeToggle();
        _usageToggle.Checked = _settings.ShowUsage;
        _usageToggle.CheckedChanged += (_, _) =>
        {
            UsageEnabledChanged?.Invoke(_usageToggle.Checked);
            _usageRefreshBtn.Enabled    = _usageToggle.Checked;
            _expectedRateToggle.Enabled = _usageToggle.Checked;
            _usageBars.SetOn(_usageToggle.Checked);
        };
        page.Controls.Add(TitleRow("Usage limits", _usageToggle));

        page.Controls.Add(BodyText("Your account-wide 5-hour and weekly rate-limit usage."));

        _expectedRateToggle = MakeToggle();
        _expectedRateToggle.Checked = _settings.ShowExpectedUsageRate;
        _expectedRateToggle.Enabled = _settings.ShowUsage;
        _expectedRateToggle.CheckedChanged += (_, _) =>
        {
            ExpectedRateChanged?.Invoke(_expectedRateToggle.Checked);
            _usageBars.SetShowExpectedRate(_expectedRateToggle.Checked);
        };
        page.Controls.Add(TitleRow("Show expected rate", _expectedRateToggle));

        _usageBars = new UsageBarsControl { Margin = new Padding(0, 2, 0, 6) };
        _fluidWidth.Add((_usageBars, 0));
        page.Controls.Add(_usageBars);

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
        page.Controls.Add(row);
    }

    // ── Notifications (Windows desktop + external ntfy) ──────────────────────────────
    private void BuildNotificationsPage(FlowLayoutPanel page)
    {
        _notifyMasterToggle = MakeToggle();
        _notifyMasterToggle.Checked = _settings.NotificationsEnabled;
        _notifyMasterToggle.CheckedChanged += (_, _) =>
        {
            _settings.NotificationsEnabled = _notifyMasterToggle.Checked;
            _settings.Save();
            ApplyNotifyEnabled();
        };
        page.Controls.Add(TitleRow("Notifications", _notifyMasterToggle));

        page.Controls.Add(BodyText(
            "Windows desktop notifications when a session needs you. Turn the whole feature off, " +
            "or just the types you don't want. Use Test to preview one."));

        page.Controls.Add(BuildNotifyRow(
            "Done — a session finished working",
            _settings.NotifyOnDone,
            v => { _settings.NotifyOnDone = v; _settings.Save(); },
            NotificationKind.Done));

        page.Controls.Add(BuildNotifyRow(
            "Waiting for input — a session is blocked on a prompt",
            _settings.NotifyOnWaitingInput,
            v => { _settings.NotifyOnWaitingInput = v; _settings.Save(); },
            NotificationKind.WaitingForInput));

        ApplyNotifyEnabled();

        page.Controls.Add(Separator());

        BuildExternalSection(page);
    }

    // An indented sub-row for one notification type: a label, a "Test" button, and a toggle on the
    // right. The trio is tracked so ApplyNotifyEnabled can dim it when the master switch is off.
    private Panel BuildNotifyRow(string text, bool initial, Action<bool> onChanged, NotificationKind kind)
    {
        var row = new Panel
        {
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

        var test = MakeButton("Test");
        test.AutoSize  = false;
        test.Size      = new Size(56, 24);
        test.Margin    = new Padding(0);
        test.Padding   = new Padding(0);
        test.TextAlign = ContentAlignment.MiddleCenter;
        test.Click   += (_, _) => TestNotificationRequested?.Invoke(kind);

        row.Controls.Add(label);
        row.Controls.Add(test);
        row.Controls.Add(toggle);

        // Right-align the toggle and Test button to the row's current width whenever it changes.
        void Position()
        {
            toggle.Location = new Point(row.Width - toggle.Width, (row.Height - toggle.Height) / 2);
            test.Location   = new Point(toggle.Left - test.Width - 12, (row.Height - test.Height) / 2);
        }
        row.Resize += (_, _) => Position();
        _fluidWidth.Add((row, 0));
        Position();

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
    private void BuildExternalSection(FlowLayoutPanel page)
    {
        _externalToggle = MakeToggle();
        _externalToggle.Checked = _settings.ExternalNotificationsEnabled;
        _externalToggle.CheckedChanged += (_, _) =>
        {
            _settings.ExternalNotificationsEnabled = _externalToggle.Checked;
            _settings.Save();
            ApplyExternalEnabled();
            ExternalNotificationsEnabledChanged?.Invoke(_externalToggle.Checked);
        };
        page.Controls.Add(TitleRow("External notifications", _externalToggle));

        page.Controls.Add(BodyText(
            "Also push \"Done\" and \"Waiting for input\" alerts to your phone or other devices via " +
            "ntfy. Enter your server and topic below, then enable it per session by right-clicking " +
            "that session in the overlay."));

        // Default the host to the public server, but only in-memory until the box is edited — opening
        // settings shouldn't silently rewrite settings.json.
        string host = string.IsNullOrWhiteSpace(_settings.NtfyHost) ? "https://ntfy.sh" : _settings.NtfyHost!;
        _settings.NtfyHost = host;

        page.Controls.Add(FieldCaption("Server URL"));
        _ntfyHostBox = MakeTextBox(host);
        _fluidWidth.Add((_ntfyHostBox, 0));
        _ntfyHostBox.TextChanged += (_, _) => _settings.NtfyHost = _ntfyHostBox.Text;
        _ntfyHostBox.Leave       += (_, _) => _settings.Save();
        page.Controls.Add(_ntfyHostBox);

        page.Controls.Add(FieldCaption("Topic"));

        // Topic box with two helpers beside it: "Generate" mints a hard-to-guess 64-char topic, and
        // "QR code" shows an ntfy:// subscribe link for scanning on a phone.
        var topicRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 0, 0, 8),
        };

        _ntfyTopicBox = MakeTextBox(_settings.NtfyTopic ?? "");
        _ntfyTopicBox.Margin = new Padding(0, 0, 8, 0);
        // The topic box shares its row with the Generate (86) + QR (78) buttons and their margins,
        // so it gets the available width less ~180px.
        _fluidWidth.Add((_ntfyTopicBox, 180));
        _ntfyTopicBox.TextChanged += (_, _) => _settings.NtfyTopic = _ntfyTopicBox.Text;
        _ntfyTopicBox.Leave       += (_, _) => _settings.Save();

        var genBtn = MakeButton("Generate");
        genBtn.AutoSize  = false;
        genBtn.Size      = new Size(86, 24);
        genBtn.Margin    = new Padding(0, 0, 8, 0);
        genBtn.Padding   = new Padding(0);
        genBtn.TextAlign = ContentAlignment.MiddleCenter;
        genBtn.Click += (_, _) =>
        {
            _ntfyTopicBox.Text = GenerateTopic();   // raises TextChanged -> updates _settings.NtfyTopic
            _settings.Save();
        };

        var qrBtn = MakeButton("QR code");
        qrBtn.AutoSize  = false;
        qrBtn.Size      = new Size(78, 24);
        qrBtn.Margin    = new Padding(0);
        qrBtn.Padding   = new Padding(0);
        qrBtn.TextAlign = ContentAlignment.MiddleCenter;
        qrBtn.Click += (_, _) => ShowTopicQr();

        topicRow.Controls.Add(_ntfyTopicBox);
        topicRow.Controls.Add(genBtn);
        topicRow.Controls.Add(qrBtn);
        page.Controls.Add(topicRow);

        page.Controls.Add(BuildLockNotifyRow());
        page.Controls.Add(BuildRemoteLinkRow());

        var row = ButtonRow();
        row.Margin = new Padding(0, 4, 0, 4);
        var testBtn = MakeButton("Send test notification");
        testBtn.Click += (_, _) => { _settings.Save(); TestExternalNotificationRequested?.Invoke(); };
        row.Controls.Add(testBtn);
        page.Controls.Add(row);

        ApplyExternalEnabled();
    }

    // An indented sub-row for the AFK override: while the screen is locked, push every session's
    // alert without needing the per-session right-click opt-in. Dimmed while the external master
    // toggle is off, since no push is sent then anyway.
    private Panel BuildLockNotifyRow()
    {
        var row = new Panel
        {
            Height = 30,
            Margin = new Padding(0, 2, 0, 4),
        };

        _lockNotifyLabel = new Label
        {
            Text      = "Notify any session while my screen is locked",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        _lockNotifyToggle = MakeToggle();
        _lockNotifyToggle.Checked = _settings.NotifyWhenLocked;
        _lockNotifyToggle.CheckedChanged += (_, _) =>
        {
            _settings.NotifyWhenLocked = _lockNotifyToggle.Checked;
            _settings.Save();
        };

        row.Controls.Add(_lockNotifyLabel);
        row.Controls.Add(_lockNotifyToggle);

        void Position() =>
            _lockNotifyToggle.Location = new Point(row.Width - _lockNotifyToggle.Width, (row.Height - _lockNotifyToggle.Height) / 2);
        row.Resize += (_, _) => Position();
        _fluidWidth.Add((row, 0));
        Position();
        return row;
    }

    // An indented sub-row that opts remote-controlled sessions into carrying a claude.ai "Open
    // session" deep link in their push. Dimmed (like the per-type notify rows) while the external
    // master toggle is off, since no push is sent then anyway.
    private Panel BuildRemoteLinkRow()
    {
        var row = new Panel
        {
            Height = 30,
            Margin = new Padding(0, 2, 0, 4),
        };

        _remoteLinkLabel = new Label
        {
            Text      = "Include a claude.ai link for remote-controlled sessions",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Location  = new Point(16, 7),
        };

        _remoteLinkToggle = MakeToggle();
        _remoteLinkToggle.Checked = _settings.ExternalNotificationsIncludeRemoteLink;
        _remoteLinkToggle.CheckedChanged += (_, _) =>
        {
            _settings.ExternalNotificationsIncludeRemoteLink = _remoteLinkToggle.Checked;
            _settings.Save();
        };

        row.Controls.Add(_remoteLinkLabel);
        row.Controls.Add(_remoteLinkToggle);

        void Position() =>
            _remoteLinkToggle.Location = new Point(row.Width - _remoteLinkToggle.Width, (row.Height - _remoteLinkToggle.Height) / 2);
        row.Resize += (_, _) => Position();
        _fluidWidth.Add((row, 0));
        Position();
        return row;
    }

    // Dims the remote-link sub-row whenever the external master switch is off.
    private void ApplyExternalEnabled()
    {
        bool on = _externalToggle.Checked;
        _remoteLinkToggle.Enabled  = on;
        _remoteLinkLabel.ForeColor = on ? Theme.Fg : Theme.Muted;
        _lockNotifyToggle.Enabled  = on;
        _lockNotifyLabel.ForeColor = on ? Theme.Fg : Theme.Muted;
    }

    // Mints a hard-to-guess topic of the form "claude-watch-{random}", padded with random
    // alphanumerics to a total length of 64 — long enough that the topic doubles as the secret.
    private static string GenerateTopic()
    {
        const string prefix = "claude-watch-";
        const string chars  = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var buf = new char[64];
        prefix.CopyTo(0, buf, 0, prefix.Length);
        for (int i = prefix.Length; i < buf.Length; i++)
            buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    // Shows a QR card encoding ntfy://<host>/<topic> (host with any scheme stripped), so the topic
    // can be subscribed to by scanning it in the ntfy phone app. Only one card is shown at a time.
    private void ShowTopicQr()
    {
        var topic = _ntfyTopicBox.Text.Trim();
        if (topic.Length == 0) return;

        var host = _ntfyHostBox.Text.Trim();
        int scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        host = host.Trim('/');

        var url = $"ntfy://{host}/{topic}";

        _topicQrForm?.Close();
        _topicQrForm = new QrCodeForm("ntfy subscription", url);
        _topicQrForm.FormClosed += (_, _) => _topicQrForm = null;
        _topicQrForm.CenterOn(Screen.FromControl(this));
        _topicQrForm.Show();
        _topicQrForm.Activate();
    }

    // ── Automation ────────────────────────────────────────────────────────────────
    private void BuildAutomationPage(FlowLayoutPanel page)
    {
        _autoStartToggle = MakeToggle();
        _autoStartToggle.Checked = _settings.AutoStartOnFirstSession;
        _autoStartToggle.CheckedChanged += (_, _) =>
        {
            _settings.AutoStartOnFirstSession = _autoStartToggle.Checked;
            _settings.Save();
        };
        page.Controls.Add(TitleRow("Start automatically", _autoStartToggle));

        page.Controls.Add(BodyText(
            "Launch Claude Watch in the background when a Claude Code session opens and it isn't " +
            "already running. Requires the installed app — the plugin starts it via the " +
            "\"claude-watch\" command on your PATH, so sessions run from a dev build (dotnet run) " +
            "won't trigger it."));

        page.Controls.Add(Separator());

        _autoCloseToggle = MakeToggle();
        _autoCloseToggle.Checked = _settings.AutoCloseAfterLastSession;
        _autoCloseToggle.CheckedChanged += (_, _) =>
        {
            _settings.AutoCloseAfterLastSession = _autoCloseToggle.Checked;
            _settings.Save();
        };
        page.Controls.Add(TitleRow("Close automatically", _autoCloseToggle));

        page.Controls.Add(BodyText(
            "Exit Claude Watch a short while after the last Claude Code session ends — but only when " +
            "it was started automatically by the option above. A window you opened yourself stays open."));
    }

    // ── Quick links ───────────────────────────────────────────────────────────────
    private void BuildQuickLinksPage(FlowLayoutPanel page)
    {
        page.Controls.Add(BodyText(
            "Show quick-link icons below the usage bars in the overlay. " +
            "Click an icon to open that app or bring it to focus if it is already running."));

        page.Controls.Add(Separator());

        _gitKrakenToggle = MakeToggle();
        _gitKrakenToggle.Checked = _settings.ShowGitKraken;
        _gitKrakenToggle.CheckedChanged += (_, _) =>
            GitKrakenEnabledChanged?.Invoke(_gitKrakenToggle.Checked);
        page.Controls.Add(TitleRow("GitKraken", _gitKrakenToggle));

        page.Controls.Add(Separator());

        _slackToggle = MakeToggle();
        _slackToggle.Checked = _settings.ShowSlack;
        _slackToggle.CheckedChanged += (_, _) =>
            SlackEnabledChanged?.Invoke(_slackToggle.Checked);
        page.Controls.Add(TitleRow("Slack", _slackToggle));
    }

    // ── About ─────────────────────────────────────────────────────────────────────
    private void BuildAboutPage(FlowLayoutPanel page)
    {
        page.Controls.Add(SectionTitle("About"));

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
        page.Controls.Add(header);

        page.Controls.Add(LinkRow("GitHub repository", AppInfo.RepoUrl));
        page.Controls.Add(LinkRow("Report an issue on GitHub", AppInfo.IssuesUrl));

        page.Controls.Add(Separator());

        page.Controls.Add(SectionTitle("Updates"));
        page.Controls.Add(BodyText($"Currently running v{AppInfo.Version}."));

        var row = ButtonRow();
        row.Margin = new Padding(0, 0, 0, 24);  // breathing room at the bottom of the page
        var checkBtn = MakeButton("Check for Updates");
        checkBtn.Click += (_, _) => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
        row.Controls.Add(checkBtn);
        page.Controls.Add(row);
    }

    // ── Public updates from the owner ────────────────────────────────────────────
    /// <summary>Pushes a fresh usage reading in (e.g. after the context's periodic poll).</summary>
    public void UpdateUsage(UsageInfo usage)
    {
        _usage = usage;
        if (!IsDisposed)
            _usageBars.SetUsage(usage);
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

    // A section header with a right-justified toggle on the same row. The toggle is re-positioned
    // to the row's right edge whenever the row width changes.
    private Panel TitleRow(string title, ToggleSwitch toggle)
    {
        var row = new Panel
        {
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
        row.Controls.Add(label);
        row.Controls.Add(toggle);

        void Position() => toggle.Location = new Point(row.Width - toggle.Width, (row.Height - toggle.Height) / 2);
        row.Resize += (_, _) => Position();
        _fluidWidth.Add((row, 0));
        Position();
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

    // A dark-themed single-line text box matching the rest of the settings surface. Width is managed
    // by the fluid-layout pass; callers register it in _fluidWidth.
    private TextBox MakeTextBox(string value) => new()
    {
        Text        = value,
        Width       = 480,
        BackColor   = Theme.ButtonBg,
        ForeColor   = Theme.Fg,
        BorderStyle = BorderStyle.FixedSingle,
        Font        = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
        Margin      = new Padding(0, 0, 0, 8),
    };

    // A wrapping body paragraph; registered so its wrap width tracks the content area.
    private Label BodyText(string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),  // updated by ApplyFluidWidth
            ForeColor   = Theme.Muted,
            Margin      = new Padding(0, 0, 0, 6),
        };
        _fluidWrap.Add(l);
        return l;
    }

    // A monospace, boxed block for copy-pasteable commands.
    private Label CodeBlock(string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            Font        = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor   = Theme.Fg,
            BackColor   = Color.FromArgb(34, 34, 44),
            Padding     = new Padding(10, 8, 10, 8),
            Margin      = new Padding(0, 0, 0, 8),
        };
        _fluidWrap.Add(l);
        return l;
    }

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

    private Panel Separator()
    {
        var p = new Panel
        {
            Height    = 1,
            Width     = 480,
            BackColor = Theme.Border,
            Margin    = new Padding(0, 12, 0, 12),
        };
        _fluidWidth.Add((p, 0));
        return p;
    }

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
        {
            _icon?.Dispose();
            _topicQrForm?.Close();
        }
        base.Dispose(disposing);
    }
}
