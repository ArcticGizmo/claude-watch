namespace ClaudeWatch;

using System.Diagnostics;

/// <summary>
/// First-class settings window opened by left-clicking the tray icon. A single dark-themed,
/// vertically-stacked panel with four sections: About, Permission detection (plugin),
/// Usage, and Updates. Reads/writes the shared <see cref="AppSettings"/> instance and drives
/// <see cref="PluginManager"/>/<see cref="UsageMonitor"/> directly; toggling usage and checking
/// for updates are raised as events so the owning context keeps timers and the overlay in sync.
/// </summary>
internal sealed class SettingsForm : Form
{
    // ── Theme ───────────────────────────────────────────────────────────────────
    private static readonly Color FormBg     = Color.FromArgb(24, 24, 32);
    private static readonly Color Fg         = Color.FromArgb(225, 225, 235);
    private static readonly Color Muted      = Color.FromArgb(140, 140, 160);
    private static readonly Color Accent     = Color.FromArgb(96, 165, 250);
    private static readonly Color BorderCol  = Color.FromArgb(45, 45, 60);
    private static readonly Color ButtonBg   = Color.FromArgb(45, 45, 60);
    private static readonly Color ButtonHover= Color.FromArgb(60, 60, 80);

    private const int ContentWidth = 372;  // inner width inside the form padding

    private readonly AppSettings   _settings;
    private readonly PluginManager _pluginManager;
    private readonly UsageMonitor  _usageMonitor;

    private readonly Bitmap? _icon = LoadEmbeddedBitmap("ClaudeWatch.icon.png");

    // Section controls we update after async work.
    private Label    _pluginStatusLabel = null!;
    private Label    _pluginResultLabel = null!;
    private Button   _pluginInstallBtn  = null!;
    private Button   _pluginUninstallBtn= null!;
    private CheckBox _usageCheck        = null!;
    private Label    _usageInfoLabel    = null!;
    private Button   _usageRefreshBtn   = null!;

    private UsageInfo _usage;

    /// <summary>Raised when the user toggles the "Show usage limits" checkbox (true = enabled).</summary>
    public event Action<bool>? UsageEnabledChanged;

    /// <summary>Raised when the user clicks "Check for Updates".</summary>
    public event EventHandler? CheckForUpdatesRequested;

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
        BackColor       = FormBg;
        ForeColor       = Fg;
        Font            = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize      = new Size(ContentWidth + 32, 600);
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        BuildLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    // ── Layout ───────────────────────────────────────────────────────────────────
    private void BuildLayout()
    {
        var root = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoScroll    = true,
            Padding       = new Padding(16),
            BackColor     = FormBg,
        };

        BuildAboutSection(root);
        root.Controls.Add(Separator());
        BuildPluginSection(root);
        root.Controls.Add(Separator());
        BuildUsageSection(root);
        root.Controls.Add(Separator());
        BuildUpdatesSection(root);

        Controls.Add(root);

        // Usage display needs no I/O, so render it immediately.
        RenderUsage();
    }

    // Probe the plugin health once the window is up (it shells out to the CLI, so it's async and
    // must run after the handle exists — doing it in the ctor would invoke before that).
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshPluginStatus();
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
            ForeColor = Fg,
            Margin    = new Padding(0, 2, 0, 0),
        });
        root.Controls.Add(header);

        root.Controls.Add(LinkRow("GitHub repository", AppInfo.RepoUrl));
        root.Controls.Add(LinkRow("Report an issue on GitHub", AppInfo.IssuesUrl));
    }

    private void BuildPluginSection(FlowLayoutPanel root)
    {
        root.Controls.Add(SectionTitle("Permission detection"));
        root.Controls.Add(BodyText(
            "A Claude Code plugin reports each session's live permission mode to Claude Watch."));

        _pluginStatusLabel = BodyText("Status: checking…");
        _pluginStatusLabel.ForeColor = Fg;
        root.Controls.Add(_pluginStatusLabel);

        var buttons = ButtonRow();
        _pluginInstallBtn   = MakeButton("Install / enable");
        _pluginUninstallBtn = MakeButton("Uninstall");
        _pluginInstallBtn.Click   += async (_, _) => await RunPluginAction(_pluginManager.InstallAsync);
        _pluginUninstallBtn.Click += async (_, _) => await RunPluginAction(_pluginManager.UninstallAsync);
        buttons.Controls.Add(_pluginInstallBtn);
        buttons.Controls.Add(_pluginUninstallBtn);
        root.Controls.Add(buttons);

        var diag = ButtonRow();
        var copyBtn    = MakeButton("Copy install commands");
        var refreshBtn = MakeButton("Refresh status");
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(PluginManager.FallbackCommands); } catch { }
            SetPluginResult("Install commands copied — paste them into a Claude Code session.");
        };
        refreshBtn.Click += async (_, _) => await RefreshPluginStatus();
        diag.Controls.Add(copyBtn);
        diag.Controls.Add(refreshBtn);
        root.Controls.Add(diag);

        _pluginResultLabel = BodyText("");
        _pluginResultLabel.Visible = false;
        root.Controls.Add(_pluginResultLabel);
    }

    private void BuildUsageSection(FlowLayoutPanel root)
    {
        root.Controls.Add(SectionTitle("Usage limits"));

        _usageCheck = new CheckBox
        {
            Text       = "Show usage limits in the overlay",
            Checked    = _settings.ShowUsage,
            AutoSize   = true,
            ForeColor  = Fg,
            FlatStyle  = FlatStyle.Flat,
            Margin     = new Padding(0, 0, 0, 6),
        };
        _usageCheck.CheckedChanged += (_, _) =>
        {
            UsageEnabledChanged?.Invoke(_usageCheck.Checked);
            _usageRefreshBtn.Enabled = _usageCheck.Checked;
            RenderUsage();
        };
        root.Controls.Add(_usageCheck);

        _usageInfoLabel = BodyText("");
        root.Controls.Add(_usageInfoLabel);

        var row = ButtonRow();
        _usageRefreshBtn = MakeButton("Refresh");
        _usageRefreshBtn.Enabled = _settings.ShowUsage;
        _usageRefreshBtn.Click += async (_, _) =>
        {
            if (!_settings.ShowUsage) return;
            _usageRefreshBtn.Enabled = false;
            _usageInfoLabel.Text = "Refreshing…";
            _usage = await _usageMonitor.FetchAsync();
            RenderUsage();
            _usageRefreshBtn.Enabled = _settings.ShowUsage;
        };
        row.Controls.Add(_usageRefreshBtn);
        root.Controls.Add(row);
    }

    private void BuildUpdatesSection(FlowLayoutPanel root)
    {
        root.Controls.Add(SectionTitle("Updates"));
        root.Controls.Add(BodyText($"Currently running v{AppInfo.Version}."));

        var row = ButtonRow();
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
            RenderUsage();
    }

    // ── Plugin helpers ────────────────────────────────────────────────────────────
    private async Task RefreshPluginStatus()
    {
        _pluginStatusLabel.Text = "Status: checking…";
        var health = await _pluginManager.GetHealthAsync();
        if (IsDisposed) return;
        _pluginStatusLabel.Text = "Status: " + PluginManager.Describe(health);
    }

    private async Task RunPluginAction(Func<Task<(bool ok, string message)>> action)
    {
        _pluginInstallBtn.Enabled   = false;
        _pluginUninstallBtn.Enabled = false;
        _pluginStatusLabel.Text     = "Status: working…";

        var (ok, message) = await action();
        if (IsDisposed) return;

        SetPluginResult(message, ok);
        _pluginInstallBtn.Enabled   = true;
        _pluginUninstallBtn.Enabled = true;
        await RefreshPluginStatus();
    }

    private void SetPluginResult(string message, bool ok = true)
    {
        _pluginResultLabel.Text      = message;
        _pluginResultLabel.ForeColor = ok ? Muted : Color.FromArgb(248, 113, 113);
        _pluginResultLabel.Visible   = !string.IsNullOrEmpty(message);
    }

    // ── Usage helpers ──────────────────────────────────────────────────────────────
    private void RenderUsage()
    {
        if (!_settings.ShowUsage)
        {
            _usageInfoLabel.Text = "Usage tracking is off. Enable it to see your 5-hour and weekly limits.";
            return;
        }

        if (_usage.LastUpdated == DateTime.MinValue && _usage.FiveHourPercent == null)
        {
            _usageInfoLabel.Text = _usage.Error ?? "No usage data yet.";
            return;
        }

        string session = FormatWindow("Session (5h)", _usage.FiveHourPercent, _usage.FiveHourResetsAt);
        string weekly  = FormatWindow("Weekly",       _usage.SevenDayPercent, _usage.SevenDayResetsAt);

        string footer = _usage.Ok
            ? $"Updated {_usage.LastUpdated:h:mm tt}"
            : $"Stale — {_usage.Error}";

        _usageInfoLabel.Text = $"{session}\n{weekly}\n{footer}";
    }

    private static string FormatWindow(string label, double? percent, DateTime? resetsAt)
    {
        string pct   = percent is { } p ? $"{(int)Math.Round(Math.Clamp(p, 0, 100))}%" : "—";
        string reset = resetsAt is { } r ? $"  ·  resets {r:ddd h:mm tt}" : "";
        return $"{label}: {pct}{reset}";
    }

    // ── Control factories ───────────────────────────────────────────────────────────
    private static Label SectionTitle(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Color.FromArgb(245, 245, 250),
        Font      = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
        Margin    = new Padding(0, 4, 0, 8),
    };

    private static Label BodyText(string text) => new()
    {
        Text        = text,
        AutoSize    = true,
        MaximumSize = new Size(ContentWidth, 0),  // wrap long lines, auto height
        ForeColor   = Muted,
        Margin      = new Padding(0, 0, 0, 6),
    };

    private LinkLabel LinkRow(string text, string url)
    {
        var link = new LinkLabel
        {
            Text              = text,
            AutoSize          = true,
            LinkColor         = Accent,
            ActiveLinkColor   = Color.FromArgb(147, 197, 253),
            VisitedLinkColor  = Accent,
            LinkBehavior      = LinkBehavior.HoverUnderline,
            BackColor         = FormBg,
            Margin            = new Padding(0, 0, 0, 4),
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        return link;
    }

    private static Panel Separator() => new()
    {
        Height    = 1,
        Width     = ContentWidth,
        BackColor = BorderCol,
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
            ForeColor = Fg,
            BackColor = ButtonBg,
            Padding   = new Padding(8, 4, 8, 4),
            Margin    = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor       = BorderCol;
        b.FlatAppearance.MouseOverBackColor = ButtonHover;
        b.FlatAppearance.MouseDownBackColor = BorderCol;
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
