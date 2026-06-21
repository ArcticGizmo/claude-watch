using ClaudeWatch;
using Velopack;

internal static class Program
{
    // Must be STA: the WinForms clipboard (and other OLE-backed features) throw on an MTA thread,
    // and top-level statements don't emit [STAThread] on the generated entry point.
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new OverlayApplicationContext());
    }
}
