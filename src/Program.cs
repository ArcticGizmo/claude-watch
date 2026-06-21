using ClaudeWatch;
using Velopack;

internal static class Program
{
    // Must be STA: the WinForms clipboard (and other OLE-backed features) throw on an MTA thread,
    // and top-level statements don't emit [STAThread] on the generated entry point.
    [STAThread]
    private static void Main(string[] args)
    {
        // `ClaudeWatch.exe handle <event>` runs as a CLI for the plugin's hooks (read stdin, act,
        // print, exit) and never starts the tray UI.
        if (args.Length > 0 && string.Equals(args[0], "handle", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(CliHandler.Run(args));
            return;
        }

        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => PathRegistration.Register())
            .OnAfterUpdateFastCallback(_ => PathRegistration.Register())
            .OnBeforeUninstallFastCallback(_ => PathRegistration.Unregister())
            .Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new OverlayApplicationContext());
    }
}
