using System.Windows;
using Microsoft.Win32;

namespace SmartMailboxCopy.App;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Swaps the color ResourceDictionary (Themes/Colors.Light.xaml or Colors.Dark.xaml) in the application's resources at runtime. Every
/// style references the palette through DynamicResource, so the running window restyles immediately.
/// </summary>
public static class ThemeManager
{
    public static void Apply(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark || (theme == AppTheme.System && IsSystemDark());
        var replacement = new ResourceDictionary { Source = new Uri($"Themes/Colors.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };

        var dicts = Application.Current.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Colors.", StringComparison.OrdinalIgnoreCase) == true);
        if (existing != null) dicts[dicts.IndexOf(existing)] = replacement; else dicts.Insert(0, replacement);
    }

    public static AppTheme Parse(string? name) => Enum.TryParse<AppTheme>(name, true, out var t) ? t : AppTheme.System;

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>A brush from the current palette, for the few places that set a color from code.</summary>
    public static System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)Application.Current.FindResource(key);
}
