using System.Windows;

namespace SmartMailboxCopy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Opt(string n) { var i = Array.IndexOf(e.Args, n); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }

        if (!e.Args.Contains("--demo")) { new MainWindow().Show(); return; }

        // Demo mode: fictional data, no network, nothing saved. See Demo.cs.
        var demo = new DemoOptions(Opt("--step") ?? "0", Opt("--variant") ?? "", Opt("--screenshot"));
        if (demo.Step == "registrations")
        {
            var dlg = new RegistrationsDialog(DemoData.Store(), SmartMailboxCopy.App.MainWindow.RegSetupHelp);
            MainWindow = dlg; dlg.Show();
            if (demo.ScreenshotPath != null)
                dlg.ContentRendered += async (_, _) =>
                {
                    await Task.Delay(1200);
                    ScreenshotWriter.Save(dlg, demo.ScreenshotPath);
                    dlg.Close();
                };
            return;
        }
        new MainWindow(demo).Show();
    }
}
