using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmartMailboxCopy.App;

/// <summary>
/// Demo mode (<c>--demo</c>): the wizard runs on fictional Contoso and Fabrikam data with no network calls and never reads or writes the
/// user's saved settings. Used for the screenshots in the user guide (<c>--step N --screenshot file.png</c>) and for trying the screens out.
/// </summary>
public sealed record DemoOptions(string Step, string Variant, string? ScreenshotPath);

internal static class DemoData
{
    public static AppRegistrationStore Store()
    {
        var s = new AppRegistrationStore();
        var a = new AppRegistration { Name = "Contoso (source)", TenantId = "11111111-2222-3333-4444-555555555555", ClientId = "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb" };
        var b = new AppRegistration { Name = "Fabrikam (target)", TenantId = "66666666-7777-8888-9999-000000000000", ClientId = "cccccccc-4444-5555-6666-dddddddddddd" };
        a.SetClientSecret("demo"); b.SetClientSecret("demo");
        s.Registrations.AddRange(new[] { a, b });
        return s;
    }

    private static readonly string[] Users =
    {
        "Alex Rivera", "Priya Shah", "Jordan Lee", "Morgan Chen", "Sam Patel", "Taylor Brooks", "Casey Nguyen",
        "Riley Evans", "Jamie Ortiz", "Drew Kim", "Avery Stone", "Quinn Murphy",
    };

    public static readonly string[] Picked = { "alex.rivera@contoso.com", "priya.shah@contoso.com", "jordan.lee@contoso.com", "morgan.chen@contoso.com", "sam.patel@contoso.com", "taylor.brooks@contoso.com" };

    private static string Addr(string name, string domain) => name.ToLowerInvariant().Replace(' ', '.') + "@" + domain;

    public static List<DirectoryMailbox> Source()
    {
        var l = Users.Select(n => new DirectoryMailbox(Guid.NewGuid().ToString(), n, Addr(n, "contoso.com"), Addr(n, "contoso.com"), MailboxKind.User, n != "Riley Evans")).ToList();
        foreach (var r in new[] { "Conference Room 1", "Support Team", "Facilities" })
            l.Add(new DirectoryMailbox(Guid.NewGuid().ToString(), r, Addr(r, "contoso.com"), Addr(r, "contoso.com"), MailboxKind.UnlicensedResource, true));
        l.Add(new DirectoryMailbox(Guid.NewGuid().ToString(), "Pat Partner (guest)", "pat.partner_example.com#EXT#@contoso.onmicrosoft.com", "pat.partner@example.com", MailboxKind.Guest, true));
        return l.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<DirectoryMailbox> Target() =>
        Users.Where(n => n != "Jordan Lee").Select(n => new DirectoryMailbox(Guid.NewGuid().ToString(), n, Addr(n, "fabrikam.com"), Addr(n, "fabrikam.com"), MailboxKind.User, true)).ToList();
}

/// <summary>Saves a window's rendered contents (without the title bar) as a PNG, at the screen's own DPI.</summary>
internal static class ScreenshotWriter
{
    public static void Save(FrameworkElement el, string path)
    {
        if (el is Window win && win.Content is FrameworkElement inner) el = inner;   // the content only: no title bar, no blank strip
        var dpi = VisualTreeHelper.GetDpi(el);
        var w = (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX); var h = (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var fs = File.Create(path); enc.Save(fs);
    }
}
