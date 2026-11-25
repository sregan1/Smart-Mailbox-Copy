using System.Windows;
using System.Windows.Controls;
using SmartMailboxCopy.App.Localization;

namespace SmartMailboxCopy.App;

public partial class RegistrationsDialog : Window
{
    private readonly AppRegistrationStore _store;
    private bool _loading;

    public RegistrationsDialog(AppRegistrationStore store)
    {
        InitializeComponent();
        _store = store;
        SetupHelp.Text = Loc.T("Reg_SetupHelp");
        List.ItemsSource = _store.Registrations;
        if (_store.Registrations.Count > 0) List.SelectedIndex = 0;
    }

    private AppRegistration? Current => List.SelectedItem as AppRegistration;

    private void List_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        _loading = true;
        var r = Current;
        Form.IsEnabled = RemoveButton.IsEnabled = r != null;
        NameBox.Text = r?.Name ?? ""; TenantBox.Text = r?.TenantId ?? ""; ClientBox.Text = r?.ClientId ?? "";
        SecretBox.Password = ""; SecretSaved.Visibility = r?.HasClientSecret == true ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    private void Field_Changed(object s, TextChangedEventArgs e)
    {
        if (_loading || Current is not { } r) return;
        r.Name = NameBox.Text; r.TenantId = TenantBox.Text.Trim(); r.ClientId = ClientBox.Text.Trim();
        List.Items.Refresh();
    }

    private void Secret_Changed(object s, RoutedEventArgs e)
    {
        if (_loading || Current is not { } r || SecretBox.Password == "") return;
        r.SetClientSecret(SecretBox.Password);
        SecretSaved.Visibility = Visibility.Visible;
    }

    private void ClearSecret_Click(object s, RoutedEventArgs e)
    {
        if (Current is not { } r) return;
        r.SetClientSecret(null); SecretBox.Password = ""; SecretSaved.Visibility = Visibility.Collapsed;
    }

    private void Add_Click(object s, RoutedEventArgs e)
    {
        var r = new AppRegistration { Name = Loc.T("Reg_New") };
        _store.Registrations.Add(r);
        List.Items.Refresh(); List.SelectedItem = r; NameBox.Focus(); NameBox.SelectAll();
    }

    private void Remove_Click(object s, RoutedEventArgs e)
    {
        if (Current is not { } r) return;
        if (MessageBox.Show(this, Loc.T("Reg_RemoveText", r.DisplayName), Loc.T("Reg_RemoveTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _store.Registrations.Remove(r);
        List.Items.Refresh();
        if (_store.Registrations.Count > 0) List.SelectedIndex = 0; else List_SelectionChanged(this, null!);
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        foreach (var r in _store.Registrations)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) { MessageBox.Show(this, Loc.T("Reg_NeedName"), Loc.T("Reg_CheckTitle")); return; }
            if (!Guid.TryParse(r.TenantId, out _) || !Guid.TryParse(r.ClientId, out _))
            { MessageBox.Show(this, Loc.T("Reg_NeedGuid", r.DisplayName), Loc.T("Reg_CheckTitle")); return; }
        }
        try { _store.Save(); DialogResult = true; }
        catch (Exception ex) { MessageBox.Show(this, Loc.T("Reg_SaveFailed", ex.Message), Loc.T("Reg_SaveFailedTitle")); }
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
