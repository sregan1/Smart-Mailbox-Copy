using System.Windows;
using SmartMailboxCopy.App.Localization;

namespace SmartMailboxCopy.App;

/// <summary>Language and theme. A theme applies at once; a language applies the next time the app starts.</summary>
public partial class SettingsDialog : Window
{
    private sealed record LanguageChoice(string Code, string Text);
    private readonly AppRegistrationStore _store;

    public SettingsDialog(AppRegistrationStore store)
    {
        InitializeComponent();
        _store = store;
        var choices = new List<LanguageChoice> { new("", Loc.T("Set_LanguageSystem")) };
        choices.AddRange(Loc.Languages.Select(l => new LanguageChoice(l.Code, l.NativeName)));
        LanguageBox.ItemsSource = choices;
        LanguageBox.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Code, store.Language, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        switch (ThemeManager.Parse(store.Theme))
        {
            case AppTheme.Light: ThemeLight.IsChecked = true; break;
            case AppTheme.Dark: ThemeDark.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        _store.Language = (LanguageBox.SelectedItem as LanguageChoice)?.Code ?? "";
        var theme = ThemeLight.IsChecked == true ? AppTheme.Light : ThemeDark.IsChecked == true ? AppTheme.Dark : AppTheme.System;
        _store.Theme = theme.ToString();
        try { _store.Save(); }
        catch (Exception ex) { MessageBox.Show(this, Loc.T("Reg_SaveFailed", ex.Message), Loc.T("Reg_SaveFailedTitle")); return; }
        ThemeManager.Apply(theme);
        DialogResult = true;
    }
}
