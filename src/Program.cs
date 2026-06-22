using ClaudeWatch;
using Velopack;

internal static class Program
{
    // Per-user-session name: only one tray runs per desktop login, which is what we want.
    private const string SingleInstanceMutexName = @"Local\ClaudeWatch_SingleInstance";

    // Held for the whole process lifetime so the single-instance mutex is never finalized (and thus
    // released) while the tray is running. A static field keeps it rooted for the GC.
    private static Mutex? _instanceMutex;

    // Must be STA: the WinForms clipboard (and other OLE-backed features) throw on an MTA thread,
    // and top-level statements don't emit [STAThread] on the generated entry point.
    [STAThread]
    private static void Main(string[] args)
    {
        // `claude-watch.exe handle <event>` runs as a CLI for the plugin's hooks (read stdin, act,
        // print, exit) and never starts the tray UI.
        if (args.Length > 0 && string.Equals(args[0], "handle", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(CliHandler.Run(args));
            return;
        }

        VelopackApp
            .Build()
            .OnAfterInstallFastCallback(_ => PathRegistration.Register())
            .OnAfterUpdateFastCallback(_ => PathRegistration.Register())
            .OnBeforeUninstallFastCallback(_ => PathRegistration.Unregister())
            .Run();

        // Single-instance guard: launching claude-watch again (Start Menu, PATH, etc.) while a tray
        // is already running just exits, instead of stacking confusing duplicate overlays.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
            return; // another tray instance already owns the mutex

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new OverlayApplicationContext());

        GC.KeepAlive(_instanceMutex);
    }
}
