using System.Windows;
using SmartMailboxCopy.App.Localization;

namespace SmartMailboxCopy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Opt(string n) { var i = Array.IndexOf(e.Args, n); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        var demoMode = e.Args.Contains("--demo");

        // Language and theme are applied before any window exists. Demo mode never reads the saved settings: it takes --lang and --theme
        // (default English, light) so the screenshots always come out the same.
        var settings = demoMode ? new AppRegistrationStore() : AppRegistrationStore.Load();
        Loc.Apply(demoMode ? Opt("--lang") ?? "en-US" : settings.Language);
        ThemeManager.Apply(demoMode ? ThemeManager.Parse(Opt("--theme") ?? "Light") : ThemeManager.Parse(settings.Theme));

        if (!demoMode) { new MainWindow().Show(); return; }

        // Demo mode: fictional data, no network, nothing saved. See Demo.cs.
        var demo = new DemoOptions(Opt("--step") ?? "0", Opt("--variant") ?? "", Opt("--screenshot"));
        if (demo.Step == "registrations" || demo.Step == "settings")
        {
            Window dlg = demo.Step == "settings" ? new SettingsDialog(new AppRegistrationStore()) : new RegistrationsDialog(DemoData.Store());
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
