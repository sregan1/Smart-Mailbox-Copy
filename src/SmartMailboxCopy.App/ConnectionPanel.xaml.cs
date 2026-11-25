using System.Windows;
using System.Windows.Controls;
using SmartMailboxCopy.App.Localization;

namespace SmartMailboxCopy.App;

/// <summary>The Source and Target steps: pick a saved app registration, see what it needs, and test the connection.</summary>
public partial class ConnectionPanel : UserControl
{
    public ConnectionPanel() => InitializeComponent();

    public event Action? ManageRequested, TestRequested, SelectionChangedByUser;
    private bool _syncing;

    public AppRegistration? Selected => Combo.SelectedItem as AppRegistration;

    public void Setup(string intro, string permissionsIntro, string permissions)
    {
        IntroText.Text = intro; PermissionsIntro.Text = permissionsIntro; PermissionsText.Text = permissions;
    }

    public void SetRegistrations(IEnumerable<AppRegistration> regs, AppRegistration? keep)
    {
        _syncing = true;
        Combo.ItemsSource = null; Combo.ItemsSource = regs.ToList();
        Combo.SelectedItem = keep != null && regs.Contains(keep) ? keep : null;
        _syncing = false;
        ShowDetail();
    }

    public void Select(AppRegistration? r) { _syncing = true; Combo.SelectedItem = r; _syncing = false; ShowDetail(); }

    public void SetResult(string text, bool? ok)
    {
        ResultText.Text = text;
        ResultText.Foreground = ThemeManager.Brush(ok == true ? "SuccessBrush" : ok == false ? "DangerBrush" : "TextTertiaryBrush");
    }

    public void SetChecks(string text, bool allRequiredOk)
    {
        CheckText.Text = text;
        CheckText.Foreground = ThemeManager.Brush(allRequiredOk ? "SuccessBrush" : "DangerBrush");
    }

    public void SetBusy(bool busy) { TestButton.IsEnabled = !busy; Combo.IsEnabled = !busy; }

    private void ShowDetail()
    {
        Detail.Text = Selected is { } r
            ? Loc.T("Conn_Detail", r.TenantId, r.ClientId, Loc.T(r.HasClientSecret ? "Conn_SecretSaved" : "Conn_SecretMissing"))
            : Loc.T("Conn_NothingSelected");
    }

    private void Combo_Changed(object s, SelectionChangedEventArgs e)
    {
        ShowDetail();
        if (_syncing) return;
        SetResult("", null); SetChecks("", true);
        SelectionChangedByUser?.Invoke();
    }

    private void Manage_Click(object s, RoutedEventArgs e) => ManageRequested?.Invoke();
    private void Test_Click(object s, RoutedEventArgs e) => TestRequested?.Invoke();
}
