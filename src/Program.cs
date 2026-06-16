using ClaudeWatch;
using Velopack;

VelopackApp.Build().Run();

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.Run(new OverlayApplicationContext());
