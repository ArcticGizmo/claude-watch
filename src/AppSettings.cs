namespace ClaudeWatch;

using System.Text.Json;

internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeWatch", "settings.json");

    // Whether to show (and fetch, via the OAuth /usage endpoint) the session/weekly usage bars.
    // Defaults to true; a missing key in an older settings file keeps this default.
    public bool ShowUsage { get; set; } = true;

    // When on, a thin marker on each usage bar shows where consumption should be given the
    // elapsed time in the current window (e.g. after 2 days of a 7-day period the marker sits
    // at ~28%). Defaults to true; only visible while ShowUsage is also true.
    public bool ShowExpectedUsageRate { get; set; } = true;

    // Master switch for Windows desktop (toast/balloon) notifications. When off, no session
    // balloon is ever shown; the overlay's own attention flash is unaffected. The per-type
    // switches below only take effect while this is on.
    public bool NotificationsEnabled { get; set; } = true;

    // Per-type switches: "Done" fires when a session finishes working (busy -> idle);
    // "WaitingForInput" fires when a session is blocked on a prompt (e.g. a permission request).
    public bool NotifyOnDone { get; set; } = true;
    public bool NotifyOnWaitingInput { get; set; } = true;

    // External notifications via ntfy (https://ntfy.sh). The master switch gates whether any
    // external push is sent and whether the per-session toggle is offered in the overlay; the
    // host and topic stay saved and editable while it's off. Which sessions actually push is an
    // in-memory, per-session opt-in (right-click a session) and isn't persisted here.
    public bool ExternalNotificationsEnabled { get; set; }
    public string? NtfyHost  { get; set; }
    public string? NtfyTopic { get; set; }

    // Account-wide AFK override: when on, *any* session's external push fires while the Windows
    // session is locked, even sessions that haven't been individually opted in via the overlay's
    // right-click menu. Still gated by ExternalNotificationsEnabled (and the host/topic). Off by
    // default. See [[LockMonitor]].
    public bool NotifyWhenLocked { get; set; }

    // When on, a remote-controlled session's external push carries a "view" action that opens the
    // session on claude.ai (https://claude.ai/code/{bridgeSessionId}). Off by default — not
    // everyone wants the deep link in their notifications — and only relevant while the session
    // is actually connected via /remote-control. Gated by ExternalNotificationsEnabled.
    public bool ExternalNotificationsIncludeRemoteLink { get; set; }

    // Automation. AutoStart: the plugin's SessionStart hook reads this value (the tray usually
    // isn't running when a session opens) and launches the installed claude-watch when on. AutoClose:
    // the running tray exits a short grace period after the last session ends — but only when it was
    // itself auto-started, so a manually-opened window never vanishes under the user. Both off by
    // default. See the plugin's invoke.ps1 ("start" action) and [[OverlayApplicationContext]].
    public bool AutoStartOnFirstSession  { get; set; }
    public bool AutoCloseAfterLastSession { get; set; }

    // Quick links. Icons displayed below the usage bars; each opens the app or focuses it.
    public bool ShowGitKraken { get; set; }
    public bool ShowSlack     { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
