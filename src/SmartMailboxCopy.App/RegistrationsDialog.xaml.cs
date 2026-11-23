using System.Windows;
using System.Windows.Controls;

namespace SmartMailboxCopy.App;

public partial class RegistrationsDialog : Window
{
    private readonly AppRegistrationStore _store;
    private bool _loading;

    public RegistrationsDialog(AppRegistrationStore store, string setupHelp)
    {
        InitializeComponent();
        _store = store;
        SetupHelp.Text = setupHelp;
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
        var r = new AppRegistration { Name = "New registration" };
        _store.Registrations.Add(r);
        List.Items.Refresh(); List.SelectedItem = r; NameBox.Focus(); NameBox.SelectAll();
    }

    private void Remove_Click(object s, RoutedEventArgs e)
    {
        if (Current is not { } r) return;
        if (MessageBox.Show(this, $"Remove \"{r.DisplayName}\" from this list?\n\nThis only forgets it here. The app registration in Microsoft Entra is not touched.", "Remove registration",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _store.Registrations.Remove(r);
        List.Items.Refresh();
        if (_store.Registrations.Count > 0) List.SelectedIndex = 0; else List_SelectionChanged(this, null!);
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        foreach (var r in _store.Registrations)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) { MessageBox.Show(this, "Give every registration a name.", "Check the list"); return; }
            if (!Guid.TryParse(r.TenantId, out _) || !Guid.TryParse(r.ClientId, out _))
            { MessageBox.Show(this, $"\"{r.DisplayName}\": the tenant ID and the client ID must both be GUIDs (for example 00000000-0000-0000-0000-000000000000).", "Check the list"); return; }
        }
        try { _store.Save(); DialogResult = true; }
        catch (Exception ex) { MessageBox.Show(this, $"Could not save: {ex.Message}", "Save failed"); }
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
