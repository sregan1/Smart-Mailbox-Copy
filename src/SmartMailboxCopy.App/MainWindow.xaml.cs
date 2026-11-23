using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SmartMailboxCopy.App;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 5000;
    private static readonly string AppData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMailboxCopy");

    // ---- what each step says ----
    private static readonly (string Title, string Desc)[] Steps =
    {
        ("Welcome", "Move mail between Microsoft 365 tenants: one mailbox, several, or everyone."),
        ("Source tenant", "Connect to the tenant you are copying FROM. It is only ever read; nothing in it is changed."),
        ("Target tenant", "Connect to the tenant you are copying TO. Mail is added here; nothing already there is changed or deleted."),
        ("Choose mailboxes", "Pick the mailboxes to copy. Use the filters to narrow the list, then tick the ones you want."),
        ("Match mailboxes", "Tell the tool which target mailbox each source mailbox goes into."),
        ("Options", "Choose how the mail lands in the target and which safety checks to run."),
        ("Review", "Check the plan. Nothing has been copied yet."),
        ("Copy", "Copying now. You can stop at any time; finished items are remembered, so you can resume later."),
        ("Results", "What was copied, what was checked, and what to do next."),
    };

    private const string SourcePermsIntro = "In the Microsoft Entra admin center: App registrations > your app > API permissions > Add a permission > Microsoft Graph > Application permissions. Then select Grant admin consent.";
    private const string SourcePerms = "Mail.Read\nMailboxItem.ImportExport.All  (export; use the export permission if the portal lists one)\nMailboxItem.Read.All\nMailboxFolder.Read.All\nUser.Read.All";
    private const string TargetPermsIntro = "Same place as for the source: API permissions > Add a permission > Microsoft Graph > Application permissions, then Grant admin consent.";
    private const string TargetPerms = "Mail.ReadWrite\nMailboxItem.ImportExport.All\nMailboxItem.Read.All\nMailboxFolder.Read.All\nUser.Read.All\nCalendars.ReadWrite  (only to copy additional calendars)\nContacts.ReadWrite  (only to copy additional contact folders)";

    internal const string RegSetupHelp =
        "To create one: Microsoft Entra admin center > App registrations > New registration (single tenant). Open API permissions and add the Application permissions shown on the Source and Target steps, then Grant admin consent. " +
        "Open Certificates & secrets, create a New client secret, and copy its Value straight away (it is shown only once). Copy the Directory (tenant) ID and Application (client) ID from the Overview page into this form.";

    private AppRegistrationStore _regs = null!;
    private readonly ObservableCollection<NavItem> _nav = new();
    private readonly ObservableCollection<MailboxItem> _boxes = new();
    private readonly ICollectionView _boxView;
    private readonly ObservableCollection<MapItem> _map = new();
    private readonly ObservableCollection<RunRow> _runRows = new();
    private readonly ObservableCollection<FolderRow> _folders = new();
    private readonly Dictionary<string, FolderRow> _byPath = new();
    private readonly ObservableCollection<LogLine> _log = new();
    private readonly ICollectionView _logView;
    private readonly FrameworkElement[] _panels;
    private HashSet<string> _targetAddrs = new(StringComparer.OrdinalIgnoreCase);
    private List<DirectoryMailbox> _targetBoxes = new();
    private int _step, _warnings, _errors;
    private bool _ready, _navSync, _running, _finished, _sourceOk, _targetOk, _mapStale = true;
    private CancellationTokenSource? _cts;
    private RunLog? _runLog;
    private string? _resultsPath;

    // ---- estimate and progress ----
    // Speeds measured on the test mailbox (493 small messages, 0 throttling): copy about 0.29 s per message, field-by-field verify about 0.24 s per
    // message, and about 20 s of fixed work per mailbox (reading the tree, locating folders, reconciling). Large attachments are slower.
    private const double SecPerMessageCopy = 0.29, SecPerMessageVerify = 0.24, SecPerMailbox = 20;
    private const double OverheadShare = 0.08;   // share of a mailbox's work before the copy starts
    private readonly Dictionary<string, (int Items, long Bytes)> _est = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCts;
    private bool _scanning;
    private string _estRange = "";
    private double[] _weights = Array.Empty<double>(), _prog = Array.Empty<double>();
    private bool[] _known = Array.Empty<bool>();
    private int _cur;
    private bool _verifyOn;
    private DateTime _runStart;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly System.Text.RegularExpressions.Regex ComparedRx = new(@"compared (\d+)/(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PhaseRx = new(@"^\[(\d)/6\]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private double CopyShare => _verifyOn ? 0.50 : 0.92;

    public MainWindow(DemoOptions? demo = null)
    {
        InitializeComponent();
        _demo = demo != null;
        _regs = _demo ? DemoData.Store() : AppRegistrationStore.Load();   // demo mode never reads or writes the real settings
        _panels = new FrameworkElement[] { Step0, SourcePanel, TargetPanel, Step3, Step4, Step5, Step6, Step7, Step8 };
        for (var i = 0; i < Steps.Length; i++) _nav.Add(new NavItem { Title = Steps[i].Title, Number = i + 1 });
        NavList.ItemsSource = _nav;

        _boxView = CollectionViewSource.GetDefaultView(_boxes);
        _boxView.Filter = PassesFilter;
        MailboxGrid.ItemsSource = _boxView;
        MapGrid.ItemsSource = _map;
        RunGrid.ItemsSource = _runRows;
        FolderGrid.ItemsSource = _folders;
        _logView = CollectionViewSource.GetDefaultView(_log);
        _logView.Filter = o => o is LogLine l && l.Level >= LogLevel.Info && (OnlyProblems.IsChecked != true || l.Level >= LogLevel.Warn);
        LogList.ItemsSource = _logView;

        SourcePanel.Setup("The source is the tenant the mail currently lives in. Smart Mailbox Copy signs in with an app registration (no person signs in) and reads the mailboxes you choose.", SourcePermsIntro, SourcePerms);
        TargetPanel.Setup("The target is the tenant the mail is moving to. The mailboxes must already exist there, with licenses. The tool only adds mail.", TargetPermsIntro, TargetPerms);
        SourcePanel.ManageRequested += ManageRegs; TargetPanel.ManageRequested += ManageRegs;
        SourcePanel.TestRequested += async () => await ConnectAsync(SourcePanel, SessionRole.Source);
        TargetPanel.TestRequested += async () => await ConnectAsync(TargetPanel, SessionRole.Target);
        SourcePanel.SelectionChangedByUser += () => { _sourceOk = false; _boxes.Clear(); _map.Clear(); _mapStale = true; UpdateNav(); };
        TargetPanel.SelectionChangedByUser += () => { _targetOk = false; _targetBoxes = new(); _targetAddrs.Clear(); _mapStale = true; UpdateNav(); };

        if (!_demo) ImportLegacyConfig();
        RefreshRegistrations(null, null);
        Protected.Text = string.Join(Environment.NewLine, _regs.ProtectedMailboxes);
        LeaveOutCal.Text = string.Join(Environment.NewLine, _regs.LeaveOutCalendars);
        if (_regs.LastContainer != "") ContainerName.Text = _regs.LastContainer;
        _ready = true;
        GoTo(0);
        if (demo != null) ApplyDemo(demo);
    }

    // =============================== registrations ===============================

    private void ManageRegs_Click(object s, RoutedEventArgs e) => ManageRegs();

    private void ManageRegs()
    {
        var keepS = SourcePanel.Selected; var keepT = TargetPanel.Selected;
        if (new RegistrationsDialog(_regs, RegSetupHelp) { Owner = this }.ShowDialog() != true) _regs = AppRegistrationStore.Load();   // cancelled: drop in-memory edits
        RefreshRegistrations(keepS, keepT);
    }

    private void RefreshRegistrations(AppRegistration? keepS, AppRegistration? keepT)
    {
        // a registration object may have been replaced by a reload: find the same one by tenant and client ID
        AppRegistration? Same(AppRegistration? r) => r == null ? null : _regs.Find(r.TenantId, r.ClientId);
        SourcePanel.SetRegistrations(_regs.Registrations, Same(keepS) ?? (_regs.Registrations.Count > 0 && keepS == null ? _regs.Registrations[0] : null));
        TargetPanel.SetRegistrations(_regs.Registrations, Same(keepT) ?? (_regs.Registrations.Count > 1 && keepT == null ? _regs.Registrations[1] : _regs.Registrations.Count > 0 && keepT == null ? _regs.Registrations[0] : null));
        _sourceOk = _targetOk = false; UpdateNav();
    }

    /// <summary>First run: bring over the tenant, app and secret already in an old config file so nothing has to be retyped.</summary>
    private void ImportLegacyConfig()
    {
        if (_regs.Registrations.Count > 0) return;
        var path = CopyConfig.Find(Directory.GetCurrentDirectory()) ?? CopyConfig.Find(AppContext.BaseDirectory);
        if (path == null) return;
        try
        {
            var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            string C(string n) { var v = root.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : ""; return v.StartsWith('<') ? "" : v; }
            void Add(string name, string side)
            {
                if (!Guid.TryParse(C(side + "Tenant"), out _) || !Guid.TryParse(C(side + "ClientId"), out _) || _regs.Find(C(side + "Tenant"), C(side + "ClientId")) != null) return;
                var r = new AppRegistration { Name = name, TenantId = C(side + "Tenant"), ClientId = C(side + "ClientId") };
                if (C(side + "ClientSecret") != "") r.SetClientSecret(C(side + "ClientSecret"));
                _regs.Registrations.Add(r);
            }
            Add("Source tenant app", "source"); Add("Target tenant app", "target");
            if (root.TryGetProperty("protectedMailboxes", out var pm) && pm.ValueKind == JsonValueKind.Array)
                _regs.ProtectedMailboxes = pm.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "" && !x.StartsWith('<')).ToList();
            if (_regs.Registrations.Count > 0) _regs.Save();
        }
        catch { }
    }

    private static string Friendly(string msg)
    {
        if (msg.Contains("7000215")) return "The client secret is not valid. It may be the secret's ID instead of its Value, or a different app's secret. Create a new secret and update it under Manage.";
        if (msg.Contains("7000222")) return "The client secret has expired. Create a new one and update it under Manage.";
        if (msg.Contains("700016")) return "That application (client) ID was not found in this tenant. Check the tenant and client IDs under Manage.";
        if (msg.Contains("90002") || msg.Contains("90013")) return "That tenant ID was not found. Check it under Manage.";
        if (msg.Contains("403") || msg.Contains("Authorization_RequestDenied")) return "The app is missing the User.Read.All application permission, or admin consent has not been granted.";
        return msg.Length > 260 ? msg[..260] + "…" : msg;
    }

    /// <summary>Compares the application permissions the app was granted with what this role needs. Optional ones only matter for additional calendars and contact folders.</summary>
    internal static (string Text, bool AllRequiredOk) CheckRoles(SessionRole role, IReadOnlyList<string> granted)
    {
        bool Has(string name) => granted.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool Any(string prefix) => granted.Any(g => g.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var rows = new List<(string Label, bool Ok, bool Required, string Why)>();
        if (role == SessionRole.Source) rows.Add(("Mail.Read", Has("Mail.Read") || Has("Mail.ReadWrite"), true, "read mail"));
        else rows.Add(("Mail.ReadWrite", Has("Mail.ReadWrite"), true, "create folders in the target"));
        rows.Add(("MailboxItem.* (import/export and read)", Any("MailboxItem."), true, "export and import mail items"));
        rows.Add(("MailboxFolder.Read.All", Any("MailboxFolder."), true, "read the folder tree"));
        rows.Add(("User.Read.All", Has("User.Read.All"), true, "list mailboxes"));
        if (role == SessionRole.Target)
        {
            rows.Add(("Calendars.ReadWrite", Has("Calendars.ReadWrite"), false, "only to copy additional calendars"));
            rows.Add(("Contacts.ReadWrite", Has("Contacts.ReadWrite"), false, "only to copy additional contact folders"));
        }
        var sb = new StringBuilder("Application permissions granted to this app:\n");
        foreach (var r in rows) sb.AppendLine(r.Ok ? $"  ✓ {r.Label}" : r.Required ? $"  ✗ {r.Label}  MISSING: needed to {r.Why}" : $"  – {r.Label}  not granted ({r.Why})");
        if (rows.Any(r => !r.Ok && r.Required)) sb.Append("Add the missing permissions as Application permissions and grant admin consent. They can take a few minutes to appear.");
        return (sb.ToString().TrimEnd(), rows.All(r => r.Ok || !r.Required));
    }

    private async Task ConnectAsync(ConnectionPanel panel, SessionRole role)
    {
        var reg = panel.Selected;
        if (reg == null) { panel.SetResult("Choose an app registration first.", false); return; }
        var secret = reg.GetClientSecret();
        if (secret == null) { panel.SetResult("This registration has no client secret saved on this PC. Choose Manage and add it.", false); return; }
        panel.SetBusy(true); panel.SetResult("Connecting…", null);
        var ok = false;
        try
        {
            var side = new SideConfig(reg.TenantId, reg.ClientId, "", reg.ClientId, secret);
            var log = new RunLog(); log.Hide(secret);
            var dir = new MailboxDirectory(new CopyConfig(side, side), role, true, log);
            var list = await Task.Run(() => dir.ListAsync());
            var granted = await Task.Run(() => dir.GetGrantedRolesAsync());
            var (checkText, checkOk) = CheckRoles(role, granted);
            panel.SetChecks(checkText, checkOk);
            if (role == SessionRole.Source) LoadSourceBoxes(list);
            else { _targetBoxes = list; _targetAddrs = list.SelectMany(m => new[] { m.Upn, m.Mail }).ToHashSet(StringComparer.OrdinalIgnoreCase); }
            _mapStale = true; ok = true;
            panel.SetResult($"Connected. {list.Count} accounts with a mailbox address found.", true);
        }
        catch (Exception ex) { panel.SetResult(Friendly(ex.Message), false); }
        finally
        {
            if (role == SessionRole.Source) _sourceOk = ok; else _targetOk = ok;
            panel.SetBusy(false); UpdateNav();
        }
    }

    // =============================== choose mailboxes ===============================

    private void LoadSourceBoxes(List<DirectoryMailbox> list)
    {
        _boxes.Clear(); _map.Clear();
        foreach (var d in list)
        {
            var m = new MailboxItem { Name = d.DisplayName, Upn = d.Upn, Mail = d.Mail, Kind = d.Kind, Enabled = d.Enabled };
            m.PropertyChanged += (_, _) => { _mapStale = true; UpdateSelectionText(); UpdateNav(); };
            _boxes.Add(m);
        }
        var domains = list.Select(d => d.Domain).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        DomainFilter.ItemsSource = new[] { "All domains" }.Concat(domains).ToList();
        DomainFilter.SelectedIndex = 0;
        _boxView.Refresh(); UpdateSelectionText();
    }

    private bool PassesFilter(object o)
    {
        if (o is not MailboxItem m) return false;
        if (!(m.Kind == MailboxKind.User && KindUsers.IsChecked == true || m.Kind == MailboxKind.UnlicensedResource && KindResources.IsChecked == true || m.Kind == MailboxKind.Guest && KindGuests.IsChecked == true)) return false;
        if (EnabledOnly.IsChecked == true && !m.Enabled) return false;
        if (DomainFilter.SelectedIndex > 0 && !string.Equals(m.Domain, DomainFilter.SelectedItem as string, StringComparison.OrdinalIgnoreCase)) return false;
        var q = SearchBox.Text.Trim();
        return q == "" || m.Upn.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object s, RoutedEventArgs e) { if (_ready) { _boxView.Refresh(); UpdateSelectionText(); } }
    private void Search_Changed(object s, TextChangedEventArgs e) { if (_ready) { _boxView.Refresh(); UpdateSelectionText(); } }
    private IEnumerable<MailboxItem> Shown() => _boxView.Cast<MailboxItem>();
    private void SelectShown_Click(object s, RoutedEventArgs e) { foreach (var m in Shown().ToList()) m.Selected = true; }
    private void UnselectShown_Click(object s, RoutedEventArgs e) { foreach (var m in Shown().ToList()) m.Selected = false; }
    private async void RefreshSource_Click(object s, RoutedEventArgs e) => await ConnectAsync(SourcePanel, SessionRole.Source);

    private void UpdateSelectionText()
    {
        var sel = _boxes.Count(m => m.Selected); var shown = Shown().Count(); var hidden = _boxes.Count(m => m.Selected && !PassesFilter(m));
        SelectionText.Text = $"{sel} selected · {shown} shown of {_boxes.Count}" + (hidden > 0 ? $" · {hidden} selected but hidden by the filters" : "");
    }

    // =============================== match mailboxes ===============================

    private void BuildMap()
    {
        var existing = _map.ToDictionary(m => m.Source, StringComparer.OrdinalIgnoreCase);
        var domains = _targetBoxes.Select(m => m.Domain).Where(d => d != "").ToList();
        TargetDomain.ItemsSource = domains.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
        if (TargetDomain.Text.Trim() == "")
            TargetDomain.Text = domains.GroupBy(d => d, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        _map.Clear();
        foreach (var m in _boxes.Where(b => b.Selected))
        {
            if (existing.TryGetValue(m.Upn, out var old)) { _map.Add(old); continue; }
            var item = new MapItem { Source = m.Upn, Target = Suggest(m.Upn) };
            item.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(MapItem.Target)) { RecheckAll(); UpdateNav(); } };
            _map.Add(item);
        }
        RecheckAll(); _mapStale = false;
    }

    private string Suggest(string source) => TargetDomain.Text.Trim() == "" ? "" : MailboxMap.DomainSwap(new[] { source }, TargetDomain.Text.Trim())[0].Target;

    private void RecheckAll()
    {
        var dupes = _map.GroupBy(m => m.Target.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Key != "" && g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _map)
        {
            var t = m.Target.Trim();
            m.Status = t == "" ? "Enter a target address" : !t.Contains('@') ? "Not an email address" : dupes.Contains(t) ? "Another row uses this target"
                : _targetAddrs.Contains(t) ? "Ready" : "Not found in the target tenant";
            m.Ready = m.Status == "Ready";
        }
        var ready = _map.Count(m => m.Ready);
        MapSummary.Text = $"{ready} of {_map.Count} ready to copy" + (ready < _map.Count ? $" · {_map.Count - ready} will be left out until fixed" : "");
    }

    private void ApplyDomain_Click(object s, RoutedEventArgs e) { foreach (var m in _map) m.Target = Suggest(m.Source); RecheckAll(); UpdateNav(); }

    private void ImportMap_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files|*.*", Title = "Import a source,target map" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var rows = MailboxMap.Load(dlg.FileName); var matched = 0;
            foreach (var r in rows) if (_map.FirstOrDefault(m => string.Equals(m.Source, r.Source, StringComparison.OrdinalIgnoreCase)) is { } m) { m.Target = r.Target; matched++; }
            RecheckAll(); UpdateNav();
            MessageBox.Show(this, $"{matched} of {rows.Count} rows in the file matched a mailbox you selected. The others were ignored.", "Map imported");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not read the map", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ExportMap_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "mailbox-map.csv", Title = "Export the map" };
        if (dlg.ShowDialog(this) == true) MailboxMap.Save(dlg.FileName, _map.Select(m => new MapRow(m.Source, m.Target)));
    }

    // =============================== options and review ===============================

    private bool PimOnlyOn => PimOnly.IsChecked == true;
    private bool Merge => PimOnlyOn || ModeMerge.IsChecked == true;
    private bool WantsPim => Merge && (PimOnlyOn || IncludePim.IsChecked == true);
    private HashSet<string> LeaveOutSet() => LeaveOutCal.Text.Split('\n', '\r').Select(x => x.Trim()).Where(x => x != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private List<string> LeaveOutList() => LeaveOutSet().ToList();
    private List<string> ProtectedList() => Protected.Text.Split('\n', '\r', ';', ',').Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Mode_Changed(object s, RoutedEventArgs e)
    {
        if (!_ready) return;
        MailSection.Visibility = PimOnlyOn ? Visibility.Collapsed : Visibility.Visible;
        ContainerName.IsEnabled = !Merge;
        IncludePim.IsEnabled = ModeMerge.IsChecked == true && !PimOnlyOn;
        AllowNonEmpty.IsEnabled = Merge;
        Dedupe.IsEnabled = !PimOnlyOn; Verify.IsEnabled = !PimOnlyOn;
        UpdateFilterHeader();
        UpdateNav();
    }

    private void UpdateFilterHeader()
    {
        var n = 0;
        n += new[] { FolderInbox, FolderSent, FolderDrafts, FolderDeleted, FolderJunk, FolderArchive, FolderUser }.Count(c => c.IsChecked == true) > 0 ? 1 : 0;
        n += DateFilter.IsChecked == true ? 1 : 0; n += SizeFilter.IsChecked == true ? 1 : 0;
        FilterExpander.Header = n == 0 ? "Limit what is copied (optional): nothing is limited, everything is copied"
                                      : $"Limit what is copied (optional): {n} limit{(n == 1 ? "" : "s")} on";
    }

    private void Option_Changed(object s, RoutedEventArgs e)
    {
        if (!_ready) return;
        SinceDate.IsEnabled = DateFilter.IsChecked == true; MaxMb.IsEnabled = SizeFilter.IsChecked == true;
        if (DateFilter.IsChecked == true && SinceDate.SelectedDate == null) SinceDate.SelectedDate = DateTime.Today.AddYears(-1);
        UpdateFilterHeader();
        UpdateNav();
    }
    private void DateChanged(object s, SelectionChangedEventArgs e) { if (_ready) { UpdateFilterHeader(); UpdateNav(); } }

    private sealed record Filters(HashSet<string> Exclude, bool ExcludeUser, DateTime? Since, long MaxBytes);

    private Filters ReadFilters()
    {
        var ex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(CheckBox box, string wk) { if (box.IsChecked == true) ex.Add(wk); }
        Add(FolderInbox, "inbox"); Add(FolderSent, "sentitems"); Add(FolderDrafts, "drafts"); Add(FolderDeleted, "deleteditems"); Add(FolderJunk, "junkemail"); Add(FolderArchive, "archive");
        DateTime? since = DateFilter.IsChecked == true && SinceDate.SelectedDate is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;
        var bytes = SizeFilter.IsChecked == true && long.TryParse(MaxMb.Text.Trim(), out var mb) && mb > 0 ? mb * 1048576 : 0;
        return new Filters(ex, FolderUser.IsChecked == true, since, bytes);
    }

    private bool FiltersValid() =>
        (DateFilter.IsChecked != true || SinceDate.SelectedDate != null)
        && (SizeFilter.IsChecked != true || (long.TryParse(MaxMb.Text.Trim(), out var mb) && mb > 0))
        && (PimOnlyOn || new[] { FolderInbox, FolderSent, FolderDrafts, FolderDeleted, FolderJunk, FolderArchive, FolderUser }.Any(c => c.IsChecked != true));
    private void Option_Changed(object s, TextChangedEventArgs e) { if (_ready) UpdateNav(); }

    private void BuildReview()
    {
        var ready = _map.Where(m => m.Ready).ToList(); var left = _map.Count - ready.Count;
        var sb = new StringBuilder();
        sb.AppendLine($"Copy {ready.Count} mailbox{(ready.Count == 1 ? "" : "es")} from \"{SourcePanel.Selected?.DisplayName}\" to \"{TargetPanel.Selected?.DisplayName}\".");
        if (PimOnlyOn) sb.AppendLine("Only calendar events and contacts are copied, into each target mailbox's default Calendar and Contacts. No mail is copied.");
        else sb.AppendLine(Merge ? "Mail goes into each target mailbox's real folders (Inbox, Sent Items, Drafts…); other folders are recreated beside them."
                            : $"Mail only goes into a new folder named \"{ContainerName.Text.Trim()}\" in each target mailbox, folder structure included.");
        if (WantsPim && LeaveOutSet().Count > 0) sb.AppendLine($"Calendars and contact folders left out by name: {LeaveOutSet().Count}.");
        if (!PimOnlyOn) sb.AppendLine(WantsPim ? "Calendar events and contacts: copied into each target mailbox's default Calendar and Contacts." : "Calendar events and contacts: not copied.");
        if (!PimOnlyOn) sb.AppendLine(Dedupe.IsChecked == true ? "Mail already in the target is skipped." : "Mail already in the target is NOT checked for.");
        if (!PimOnlyOn)
        {
            var f = ReadFilters(); var omitted = new List<string>();
            foreach (var (box, name) in new[] { (FolderInbox, "Inbox"), (FolderSent, "Sent Items"), (FolderDrafts, "Drafts"), (FolderDeleted, "Deleted Items"), (FolderJunk, "Junk Email"), (FolderArchive, "Archive"), (FolderUser, "the folders people created") })
                if (box.IsChecked == true) omitted.Add(name);
            if (omitted.Count > 0) sb.AppendLine("Left out: " + string.Join(", ", omitted) + ".");
            if (f.Since is { } sd) sb.AppendLine($"Only mail created on or after {sd:yyyy-MM-dd}.");
            if (f.MaxBytes > 0) sb.AppendLine($"Messages larger than {f.MaxBytes / 1048576} MB are skipped.");
        }
        if (!PimOnlyOn) sb.AppendLine(Verify.IsChecked == true ? "Every copied message is compared with its source afterward." : "No comparison afterward.");
        sb.Append("Mailboxes are copied one after another. Nothing in the source is changed.");
        PlanText.Text = sb.ToString();

        var warn = new List<string>();
        if (left > 0) warn.Add($"{left} mailbox{(left == 1 ? "" : "es")} will be left out because no matching target mailbox was found. Go back to Match mailboxes to fix them.");
        var resources = ready.Count(r => _boxes.FirstOrDefault(b => string.Equals(b.Upn, r.Source, StringComparison.OrdinalIgnoreCase))?.Kind == MailboxKind.UnlicensedResource);
        if (resources > 0) warn.Add($"{resources} selected mailbox{(resources == 1 ? " has" : "es have")} no license (shared, room or equipment). Their mail folders are copied, and so is their calendar when calendar copying is on. Resource settings such as booking policies are not.");
        if (Merge) warn.Add(PimOnlyOn ? "This adds calendar events and contacts permanently. This tool has no way to remove them." : "Merging adds items to the real folders permanently. This tool has no way to remove them.");
        WarnCard.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = string.Join("\n\n", warn.Select(w => "• " + w));
        Understand.Visibility = Merge ? Visibility.Visible : Visibility.Collapsed; Understand.IsChecked = false;
    }

    // =============================== wizard navigation ===============================

    private bool CanNext() => _step switch
    {
        1 => _sourceOk && SourcePanel.Selected != null,
        2 => _targetOk && TargetPanel.Selected != null,
        3 => _boxes.Any(b => b.Selected),
        4 => _map.Any(m => m.Ready),
        5 => (Merge || ContainerName.Text.Trim() != "") && FiltersValid(),
        6 => (!Merge || Understand.IsChecked == true) && !_scanning,
        7 => _finished,
        _ => true,
    };

    private string NextHint() => _step switch
    {
        1 => _sourceOk ? "" : "Connect to the source tenant to continue.",
        2 => _targetOk ? "" : "Connect to the target tenant to continue.",
        3 => _boxes.Any(b => b.Selected) ? "" : "Tick at least one mailbox to continue.",
        4 => _map.Any(m => m.Ready) ? "" : "At least one row must say Ready to continue.",
        5 => !Merge && ContainerName.Text.Trim() == "" ? "Enter a folder name to continue." : !FiltersValid() ? "Check the mail filters: tick at least one folder, pick a date, and enter a size greater than 0." : "",
        6 => _scanning ? "Estimating the size and time (read only). Start is available when it finishes, or choose Skip the estimate." : !Merge || Understand.IsChecked == true ? "" : "Tick the box above to continue.",
        _ => "",
    };

    private void UpdateNav()
    {
        if (!_ready) return;
        BackButton.Visibility = _step > 0 && _step < 7 && !_running ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step switch { 0 => "Get started", 6 => "Start copying", 7 => "View results", 8 => "New migration", _ => "Next" };
        RunAgainButton.Visibility = _step == 8 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = CanNext();
        NavHint.Text = NextHint();
        foreach (var n in _nav) n.Done = n.Number - 1 < _step;
    }

    private void GoTo(int step)
    {
        if (_scanning && step != 6) _scanCts?.Cancel();
        _step = step;
        for (var i = 0; i < _panels.Length; i++) _panels[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        StepTitle.Text = Steps[step].Title; StepDesc.Text = Steps[step].Desc;
        for (var i = 0; i < _nav.Count; i++) _nav[i].Current = i == step;
        _navSync = true; NavList.SelectedIndex = step; _navSync = false;
        if (_panels[step] is ScrollViewer sv) Dispatcher.BeginInvoke(() => sv.ScrollToTop(), System.Windows.Threading.DispatcherPriority.Loaded);   // always open a screen at its top
        if (step == 3) UpdateSelectionText();
        if (step == 4 && _mapStale) BuildMap();
        if (step == 5) Mode_Changed(this, null!);
        if (step == 6) { SaveSettings(); BuildReview(); if (!_demo) _ = EstimateAsync(); }
        UpdateNav();
    }

    private void SaveSettings()
    {
        if (_demo) return;
        _regs.ProtectedMailboxes = ProtectedList();
        _regs.LeaveOutCalendars = LeaveOutList();
        if (ContainerName.Text.Trim() != "") _regs.LastContainer = ContainerName.Text.Trim();
        try { _regs.Save(); } catch { }
    }

    private void Nav_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_navSync || !_ready) return;
        var i = NavList.SelectedIndex;
        if (i >= 0 && i < _step && !_running) GoTo(i); else { _navSync = true; NavList.SelectedIndex = _step; _navSync = false; }
    }

    private void Back_Click(object s, RoutedEventArgs e) { if (_step > 0 && !_running) GoTo(_step - 1); }

    private async void Next_Click(object s, RoutedEventArgs e)
    {
        if (!CanNext()) return;
        if (_step == 6) await StartRunAsync();
        else if (_step == 7) GoTo(8);
        else if (_step == 8) NewMigration();
        else GoTo(_step + 1);
    }

    // =============================== the run ===============================

    private async Task StartRunAsync()
    {
        var ready = _map.Where(m => m.Ready).ToList();
        var sReg = SourcePanel.Selected!; var tReg = TargetPanel.Selected!;
        var sSecret = sReg.GetClientSecret(); var tSecret = tReg.GetClientSecret();
        if (sSecret == null || tSecret == null) { MessageBox.Show(this, "A client secret is missing on this PC. Add it under App registrations.", "Cannot start", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var prot = ProtectedList();
        var cfg = new CopyConfig(new SideConfig(sReg.TenantId, sReg.ClientId, "", sReg.ClientId, sSecret), new SideConfig(tReg.TenantId, tReg.ClientId, "", tReg.ClientId, tSecret), prot);
        var rows = ready.Select(m => new MapRow(m.Source.Trim(), m.Target.Trim())).ToList();
        var problems = MailboxMap.Validate(rows, string.Equals(sReg.TenantId, tReg.TenantId, StringComparison.OrdinalIgnoreCase), cfg.IsProtected);
        if (problems.Count > 0) { MessageBox.Show(this, "Fix these first:\n\n" + string.Join("\n", problems), "Cannot start", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var merge = Merge; var container = ContainerName.Text.Trim(); var pim = WantsPim; var pimOnly = PimOnlyOn; var nonEmpty = merge && AllowNonEmpty.IsChecked == true;
        var dedupe = Dedupe.IsChecked == true; var verify = Verify.IsChecked == true; var fresh = Fresh.IsChecked == true; var flt = ReadFilters(); var leaveOut = LeaveOutSet();

        var ok = MessageBox.Show(this,
            $"Start copying {rows.Count} mailbox{(rows.Count == 1 ? "" : "es")} now?\n\n" + (merge ? "Mail will be added to the real folders of each target mailbox. It cannot be removed by this tool." : $"Mail will be added in a new folder \"{container}\" in each target mailbox.") + "\n\nNothing in the source is changed. You can stop at any time.",
            "Start the copy?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;

        SaveSettings();
        _log.Clear(); _warnings = _errors = 0; UpdateCounts(); _folders.Clear(); _byPath.Clear(); _runRows.Clear();
        for (var i = 0; i < rows.Count; i++) _runRows.Add(new RunRow { Index = i + 1, Source = rows[i].Source, Target = rows[i].Target });
        _verifyOn = verify; _cur = 0; _runStart = DateTime.Now;
        _prog = new double[rows.Count]; _weights = new double[rows.Count]; _known = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++) if (_est.TryGetValue(rows[i].Source, out var e0)) { _weights[i] = Math.Max(1, e0.Items); _known[i] = true; }
        MailboxBar.Value = 0; TotalsText.Text = ""; PhaseText.Text = ""; EtaText.Text = "Starting…";
        _timer.Tick -= Timer_Tick; _timer.Tick += Timer_Tick; _timer.Start();
        RunHeadline.Text = $"Copying {rows.Count} mailbox{(rows.Count == 1 ? "" : "es")}…";
        OpenResultsButton.Visibility = OpenLogButton.Visibility = OpenItemsButton.Visibility = Visibility.Collapsed; _finished = false; _running = true;
        GoTo(7);

        _runLog = new RunLog();
        _runLog.Entry += (level, text) => Dispatcher.BeginInvoke(() => OnLog(level, text));
        var batch = new BatchCopier(cfg, rows, () => new TreeOptions
        {
            TargetContainer = merge ? "" : container, MergeWellKnown = merge, AllowNonEmpty = nonEmpty, Dedupe = dedupe, IncludePim = pim, PimOnly = pimOnly,
            ExcludeWellKnown = flt.Exclude, ExcludeUserFolders = flt.ExcludeUser, CreatedOnOrAfter = flt.Since, MaxItemBytes = flt.MaxBytes, LeaveOutPimNames = leaveOut,
            AppOnly = true, MaxItems = int.MaxValue, Fresh = fresh, Verify = verify && !pimOnly,
        }, _runLog);
        batch.MailboxChanged += p => Dispatcher.BeginInvoke(() => OnMailbox(p, rows.Count));
        batch.PhaseChanged += (n, p) => Dispatcher.BeginInvoke(() => OnPhase(n, p, rows.Count));
        batch.FoldersDiscovered += (n, list) => Dispatcher.BeginInvoke(() => OnFolders(list));
        batch.FolderChanged += (n, p) => Dispatcher.BeginInvoke(() => OnFolderChanged(p));
        batch.TotalsChanged += (n, t) => Dispatcher.BeginInvoke(() => OnTotals(n, t));

        _cts = new CancellationTokenSource();
        string headline; BatchOutcome? outcome = null; string? runError = null;
        try
        {
            var o = await Task.Run(() => batch.RunAsync(_cts.Token));
            outcome = o;
            _resultsPath = BatchCopier.SaveResults(o);
            var notRun = o.Results.Count(r => r.State == MailboxState.Skipped);
            headline = $"Finished: {o.Done} copied cleanly" + (o.Problems > 0 ? $", {o.Problems} need a look" : "") + (o.Failed > 0 ? $", {o.Failed} failed" : "") + (notRun > 0 ? $", {notRun} not run" : "") + ".";
            if (o.Problems == 0 && o.Failed == 0 && notRun == 0) headline += verify ? " Every message was compared with its source." : "";
            else headline += " Select a row for details, or open the results file.";
        }
        catch (Exception ex) { _runLog.Error($"STOPPED: {ex.GetType().Name}: {ex.Message}", ex); headline = "Stopped by an error: " + ex.Message; runError = ex.Message; }

        _runLog.Info($"\nFinished with {_runLog.Errors} errors and {_runLog.Warnings} warnings.");
        _runLog.SaveReport();
        _running = false; _finished = true; _timer.Stop();
        for (var i = 0; i < _prog.Length; i++) if (_runRows[i].State is MailboxState.Done or MailboxState.Problems or MailboxState.Failed) _prog[i] = 1;
        MailboxBar.Value = _runRows.All(r => r.State is MailboxState.Done or MailboxState.Problems or MailboxState.Failed) ? 1000 : MailboxBar.Value;
        EtaText.Text = $"Took {Dur(DateTime.Now - _runStart)}.";
        RunHeadline.Text = headline; PhaseText.Text = "";
        OpenLogButton.Visibility = Visibility.Visible; OpenResultsButton.Visibility = _resultsPath != null ? Visibility.Visible : Visibility.Collapsed;
        OpenItemsButton.Visibility = File.Exists(_runLog.ItemsPath) ? Visibility.Visible : Visibility.Collapsed;
        ShowResults(outcome, runError, merge, container, pim, verify, DateTime.Now - _runStart);
        GoTo(8);
    }

    // =============================== results screen ===============================

    private string _summaryText = "";

    private void ShowResults(BatchOutcome? o, string? error, bool merge, string container, bool pim, bool verify, TimeSpan took)
    {
        var results = o?.Results ?? (IReadOnlyList<MailboxProgress>)Array.Empty<MailboxProgress>();
        var done = results.Count(r => r.State == MailboxState.Done); var prob = results.Count(r => r.State == MailboxState.Problems);
        var fail = results.Count(r => r.State == MailboxState.Failed); var notRun = results.Count(r => r.State == MailboxState.Skipped);
        var oc = results.Select(r => r.Outcome).Where(x => x != null).Select(x => x!).ToList();
        var copied = oc.Sum(x => x.Imported); var already = oc.Sum(x => x.AlreadyDone); var failedItems = oc.Sum(x => x.Failed);
        var vOk = oc.Where(x => x.Verification != null).Sum(x => x.Verification!.Identical); var vAll = oc.Where(x => x.Verification != null).Sum(x => x.Verification!.Paired);
        var vBad = oc.Where(x => x.Verification != null).Sum(x => x.Verification!.Different + x.Verification!.Unpaired);
        var clean = o != null && prob == 0 && fail == 0 && notRun == 0 && failedItems == 0 && vBad == 0;

        (string Title, string Sub, string Back, string Border) banner =
            error != null ? ("The copy stopped", error, "#FFE9E9", "#C0392B")
            : clean ? ("Copy complete", $"{done} mailbox{(done == 1 ? "" : "es")} copied" + (verify && vAll > 0 ? $", and every one of the {vAll:N0} messages checked was identical to its source." : "."), "#E8F6EC", "#2E8B57")
            : notRun > 0 && fail == 0 && prob == 0 ? ("Copy stopped before it finished", $"{done} mailbox{(done == 1 ? "" : "es")} finished, {notRun} not started. Run again to continue: finished items are not copied twice.", "#FFF6E0", "#E0A800")
            : ("Finished, with items that need a look", $"{done} of {results.Count} mailboxes are clean" + (prob > 0 ? $", {prob} need a look" : "") + (fail > 0 ? $", {fail} failed" : "") + (notRun > 0 ? $", {notRun} not run" : "") + ".", fail > 0 ? "#FFE9E9" : "#FFF6E0", fail > 0 ? "#C0392B" : "#E0A800");
        ResultBanner.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(banner.Back)!;
        ResultBanner.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(banner.Border)!;
        ResultTitle.Text = banner.Title; ResultSub.Text = banner.Sub;

        StatsList.ItemsSource = new[]
        {
            new StatItem("Mailboxes copied", $"{done} of {results.Count}"),
            new StatItem("Messages copied", copied.ToString("N0")),
            new StatItem("Already in target", already.ToString("N0")),
            new StatItem("Failed items", failedItems.ToString("N0")),
            new StatItem("Verified identical", verify && vAll > 0 ? $"{vOk:N0} of {vAll:N0}" : verify ? "none" : "not checked"),
            new StatItem("Time taken", Dur(took)),
        };

        ResultGrid.ItemsSource = results.Select(r => new ResultRow
        {
            Source = r.Row.Source, Target = r.Row.Target,
            Result = r.State switch { MailboxState.Done => "Copied", MailboxState.Problems => "Needs a look", MailboxState.Failed => "Failed", MailboxState.Skipped => "Not run", _ => "" },
            Copied = r.Outcome?.Imported.ToString("N0") ?? "–", AlreadyThere = r.Outcome?.AlreadyDone.ToString("N0") ?? "–", Failed = r.Outcome?.Failed.ToString("N0") ?? "–",
            Verified = r.Outcome?.Verification is { } v ? $"{v.Identical:N0} of {v.Paired:N0}" : verify ? "–" : "not checked",
            Notes = r.State == MailboxState.Done ? "" : r.Message,
            HasProblem = r.State is MailboxState.Problems or MailboxState.Failed,
        }).ToList();

        var steps = new List<string>();
        if (error != null || fail > 0 || prob > 0 || notRun > 0)
            steps.Add("Open the results file or the log to see why, fix the cause (for example a missing permission or a target mailbox that does not exist yet), and choose Run again. Finished items are remembered, so nothing is copied twice.");
        steps.Add(merge ? "Open a few target mailboxes in Outlook and check the Inbox, Sent Items and your own folders. Spot-check messages with attachments."
                        : $"Open a few target mailboxes in Outlook and look for the folder \"{container}\". Spot-check messages with attachments. People can move mail out of it, or leave it there.");
        if (pim) steps.Add("Contacts and calendar are counted but not yet compared field by field. Open the Contacts and Calendar in a few target mailboxes and check them, recurring meetings especially.");
        steps.Add("To pick up mail that arrived after this run, choose Run again with the same settings. Only new mail is copied.");
        steps.Add("When the migration is completely finished, delete the client secrets and remove the app registrations in Microsoft Entra, since they can reach every mailbox in the tenant.");
        NextSteps.Text = string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}"));

        _summaryText = $"Smart Mailbox Copy: {banner.Title}\n{banner.Sub}\nMailboxes copied: {done} of {results.Count}; messages copied: {copied:N0}; already in target: {already:N0}; failed items: {failedItems:N0}; "
            + (verify ? $"verified identical: {vOk:N0} of {vAll:N0}; " : "") + $"time: {Dur(took)}.\n"
            + string.Join("\n", results.Select(r => $"  {r.Row.Source} -> {r.Row.Target}: {r.State}" + (r.Message != "" ? $" ({r.Message})" : "")));
    }

    private void CopySummary_Click(object s, RoutedEventArgs e) { try { Clipboard.SetText(_summaryText); } catch { } }
    private void RunAgain_Click(object s, RoutedEventArgs e) { if (!_running) GoTo(6); }

    private void OnMailbox(MailboxProgress p, int total)
    {
        if (p.Index < 1 || p.Index > _runRows.Count) return;
        var row = _runRows[p.Index - 1]; row.State = p.State; row.Message = p.Message;
        if (p.State == MailboxState.Running) { _cur = p.Index - 1; _folders.Clear(); _byPath.Clear(); TotalsText.Text = ""; }
        if (p.State is MailboxState.Done or MailboxState.Problems or MailboxState.Failed or MailboxState.Skipped) _prog[p.Index - 1] = 1;
        RecalcProgress();
    }

    private void OnPhase(int n, string phase, int total)
    {
        PhaseText.Text = $"Mailbox {n} of {total}: {phase}";
        var m = PhaseRx.Match(phase);
        if (m.Success && n >= 1 && n <= _prog.Length)
        {
            var k = int.Parse(m.Groups[1].Value);
            _prog[n - 1] = k <= 3 ? OverheadShare * k / 3 : k == 4 ? OverheadShare : OverheadShare + CopyShare;
            RecalcProgress();
        }
    }

    private void OnTotals(int n, TreeTotals t)
    {
        TotalsText.Text = $"Mailbox {n}: {t.Imported + t.AlreadyDone + t.Failed} of {t.Total} items processed — {t.Imported} copied, {t.AlreadyDone} already there, {t.Failed} failed";
        if (n < 1 || n > _prog.Length) return;
        if (t.Total > 0 && !_known[n - 1]) { _weights[n - 1] = t.Total; _known[n - 1] = true; }
        if (t.Total > 0) _prog[n - 1] = Math.Max(_prog[n - 1], OverheadShare + CopyShare * Math.Min(1.0, (t.Imported + t.AlreadyDone + t.Failed) / (double)t.Total));
        RecalcProgress();
    }

    private void Timer_Tick(object? s, EventArgs e) { if (_running) RecalcProgress(); }

    /// <summary>Overall progress is each mailbox's progress weighted by its message count (estimated, or the average of the known ones until its tree is read).</summary>
    private void RecalcProgress()
    {
        if (_prog.Length == 0) return;
        var knownW = _weights.Where((w, i) => _known[i]).ToList();
        var fallback = knownW.Count > 0 ? knownW.Average() : 1.0;
        var w = _weights.Select((x, i) => _known[i] ? x : fallback).ToArray();
        var total = w.Sum(); var f = total > 0 ? w.Select((x, i) => x * _prog[i]).Sum() / total : 0;
        f = Math.Clamp(f, 0, 1);
        MailboxBar.Value = f * 1000;
        var elapsed = DateTime.Now - _runStart;
        var finished = _runRows.Count(r => r.State is MailboxState.Done or MailboxState.Problems or MailboxState.Failed or MailboxState.Skipped);
        string eta = f >= 0.03 && elapsed.TotalSeconds >= 20 ? $"about {Dur(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - f) / f))} left"
            : _estRange != "" ? $"estimated {_estRange}" : "estimating the time…";
        EtaText.Text = $"{f:P0} · {finished} of {_runRows.Count} mailboxes finished · {eta} · {Dur(elapsed)} so far";
    }

    private static string Dur(TimeSpan t) =>
        t.TotalSeconds < 60 ? "under a minute" : t.TotalMinutes < 60 ? $"{Math.Round(t.TotalMinutes)} min" : $"{(int)t.TotalHours} h {t.Minutes} min";

    // =============================== estimate (read-only scan of the selected mailboxes) ===============================

    private void SkipEstimate_Click(object s, RoutedEventArgs e) => _scanCts?.Cancel();

    private async Task EstimateAsync()
    {
        _est.Clear(); _estRange = "";
        var rows = _map.Where(m => m.Ready).Select(m => new MapRow(m.Source.Trim(), m.Target.Trim())).ToList();
        var sReg = SourcePanel.Selected; var tReg = TargetPanel.Selected;
        var sSecret = sReg?.GetClientSecret(); var tSecret = tReg?.GetClientSecret();
        if (rows.Count == 0 || sReg == null || tReg == null || sSecret == null || tSecret == null) { EstimateText.Text = "An estimate needs the connections from the earlier steps."; return; }
        var cfg = new CopyConfig(new SideConfig(sReg.TenantId, sReg.ClientId, "", sReg.ClientId, sSecret), new SideConfig(tReg.TenantId, tReg.ClientId, "", tReg.ClientId, tSecret), ProtectedList());
        var flt0 = ReadFilters(); var leaveOut0 = LeaveOutSet(); var pimOnlyScan = PimOnlyOn; var mergeScan = Merge; var pimScan = WantsPim; var verify = Verify.IsChecked == true && !pimOnlyScan;

        _scanning = true; EstimateBar.Visibility = SkipEstimateButton.Visibility = Visibility.Visible; EstimateBar.Value = 0;
        EstimateText.Text = $"Reading the size of {rows.Count} mailbox{(rows.Count == 1 ? "" : "es")} (read only, nothing is changed)…"; UpdateNav();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        var log = new RunLog(); log.Hide(sSecret, tSecret);
        var batch = new BatchCopier(cfg, rows, () => pimOnlyScan ? new TreeOptions { MergeWellKnown = true, IncludePim = true, PimOnly = true, ScanOnly = true, AppOnly = true, MaxItems = int.MaxValue, LeaveOutPimNames = leaveOut0 } : new TreeOptions { TargetContainer = mergeScan ? "" : "scan", MergeWellKnown = mergeScan, IncludePim = pimScan, ScanOnly = true, AppOnly = true, MaxItems = int.MaxValue, ExcludeWellKnown = flt0.Exclude, ExcludeUserFolders = flt0.ExcludeUser, CreatedOnOrAfter = flt0.Since, MaxItemBytes = flt0.MaxBytes, LeaveOutPimNames = leaveOut0 }, log);
        var done = 0;
        batch.MailboxChanged += p => { if (p.State != MailboxState.Running) Dispatcher.BeginInvoke(() => { EstimateBar.Value = ++done / (double)rows.Count; }); };
        BatchOutcome? outcome = null; string? error = null;
        try { outcome = await Task.Run(() => batch.RunAsync(token)); }
        catch (Exception ex) { error = ex.Message; }
        finally { _scanning = false; EstimateBar.Visibility = SkipEstimateButton.Visibility = Visibility.Collapsed; }

        if (token.IsCancellationRequested) { EstimateText.Text = "Estimate skipped. The progress bar will still show an estimate once the copy is under way."; UpdateNav(); return; }
        if (outcome == null) { EstimateText.Text = "The estimate could not be made: " + Friendly(error ?? ""); UpdateNav(); return; }

        foreach (var r in outcome.Results.Where(r => r.Outcome != null)) _est[r.Row.Source] = (r.Outcome!.Listed, r.Outcome.Bytes);
        var items = _est.Values.Sum(v => v.Items); var mb = _est.Values.Sum(v => v.Bytes) / 1048576.0;
        var secs = rows.Count * SecPerMailbox + items * (SecPerMessageCopy + (verify ? SecPerMessageVerify : 0));
        var lo = TimeSpan.FromSeconds(secs * 0.8); var hi = TimeSpan.FromSeconds(secs * 1.8);
        _estRange = Dur(lo) == Dur(hi) ? Dur(hi) : $"{(lo.TotalMinutes < 60 ? Math.Round(lo.TotalMinutes).ToString() : Dur(lo))}–{Dur(hi)}";
        var sb = new StringBuilder();
        sb.AppendLine($"About {items:N0} messages ({mb:N0} MB) across {_est.Count} mailbox{(_est.Count == 1 ? "" : "es")}.");
        sb.AppendLine($"Estimated time: {_estRange}.");
        sb.Append("Based on speeds measured in our tests with ordinary messages; very large attachments take longer. Mailboxes are copied one after another. If you stop, the next run resumes where it left off.");
        var failed = outcome.Results.Where(r => r.State == MailboxState.Failed).ToList();
        if (failed.Count > 0) sb.Append($"\n\n{failed.Count} mailbox{(failed.Count == 1 ? " could" : "es could")} not be read, so {(failed.Count == 1 ? "it is" : "they are")} not in the estimate and will probably fail in the copy too. First reason: {Friendly(failed[0].Message)}");
        EstimateText.Text = sb.ToString();
        UpdateNav();
    }


    private void OnFolders(IReadOnlyList<FolderInfo> list)
    {
        _folders.Clear(); _byPath.Clear();
        foreach (var f in list)
        {
            var row = new FolderRow { Path = f.Path, Display = new string(' ', f.Depth * 3) + f.Path, Kind = f.WellKnown == "" ? "user folder" : f.WellKnown, Items = f.ItemCount };
            _byPath[f.Path] = row; _folders.Add(row);
        }
    }

    private void OnFolderChanged(FolderProgress p)
    {
        if (!_byPath.TryGetValue(p.Path, out var r)) return;
        if (p.Status == "Mismatch") { r.Status = "Counts differ"; return; }
        r.Imported = p.Imported; r.AlreadyDone = p.AlreadyDone; r.Failed = p.Failed; r.Status = p.Status;
    }

    private void Stop_Click(object s, RoutedEventArgs e) { _cts?.Cancel(); StopButton.IsEnabled = false; PhaseText.Text = "Stopping after the current item… finished items are remembered."; }

    private void NewMigration()
    {
        _finished = false; _runRows.Clear(); _folders.Clear(); _byPath.Clear(); _log.Clear();
        OpenResultsButton.Visibility = OpenLogButton.Visibility = OpenItemsButton.Visibility = Visibility.Collapsed; StopButton.IsEnabled = true;
        GoTo(3);
    }

    // =============================== log and files ===============================

    private void OnLog(LogLevel level, string text)
    {
        if (level == LogLevel.Warn) _warnings++; else if (level == LogLevel.Error) _errors++;
        if (_running && _cur < _prog.Length && ComparedRx.Match(text) is { Success: true } cm && int.Parse(cm.Groups[2].Value) > 0)
        {
            var after = OverheadShare + CopyShare;   // the verify phase fills the rest of the mailbox's share
            _prog[_cur] = Math.Max(_prog[_cur], after + (1 - after) * int.Parse(cm.Groups[1].Value) / int.Parse(cm.Groups[2].Value));
            RecalcProgress();
        }
        _log.Add(new LogLine { Level = level, Text = $"{DateTime.Now:HH:mm:ss}  {text}" });
        while (_log.Count > MaxLogLines) _log.RemoveAt(0);
        UpdateCounts();
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void UpdateCounts() => CountsText.Text = $"{_errors} errors, {_warnings} warnings";
    private void LogFilter_Click(object s, RoutedEventArgs e) => _logView.Refresh();

    private void OpenReports_Click(object s, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(AppData, "reports"); Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void OpenResults_Click(object s, RoutedEventArgs e) { if (_resultsPath != null && File.Exists(_resultsPath)) Process.Start(new ProcessStartInfo(_resultsPath) { UseShellExecute = true }); }
    private void OpenItems_Click(object s, RoutedEventArgs e) { if (_runLog != null && File.Exists(_runLog.ItemsPath)) Process.Start(new ProcessStartInfo(_runLog.ItemsPath) { UseShellExecute = true }); }
    private void OpenLog_Click(object s, RoutedEventArgs e) { if (_runLog != null && File.Exists(_runLog.FullLogPath)) Process.Start(new ProcessStartInfo(_runLog.FullLogPath) { UseShellExecute = true }); }

    private void Window_Closing(object? s, CancelEventArgs e)
    {
        if (!_running) return;
        if (MessageBox.Show(this, "A copy is running. Closing now stops it (finished items are remembered, so you can resume).\n\nClose anyway?", "Copy in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        else _cts?.Cancel();
    }
}
