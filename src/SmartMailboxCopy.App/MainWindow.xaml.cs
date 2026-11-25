using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SmartMailboxCopy.App.Localization;

namespace SmartMailboxCopy.App;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 5000;
    private static readonly string AppData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMailboxCopy");

    private const int StepCount = 9;
    private static string TitleOf(int step) => Loc.T($"Step{step}_Title");
    private static string DescOf(int step) => Loc.T($"Step{step}_Desc");

    // The permission names are Microsoft's and stay as they are; only the notes beside them are translated.
    private static string SourcePerms => "Mail.Read\nMailboxItem.ImportExport.All  (" + Loc.T("Conn_PermNoteExport") + ")\nMailboxItem.Read.All\nMailboxFolder.Read.All\nUser.Read.All";
    private static string TargetPerms => "Mail.ReadWrite\nMailboxItem.ImportExport.All\nMailboxItem.Read.All\nMailboxFolder.Read.All\nUser.Read.All\n"
        + "Calendars.ReadWrite  (" + Loc.T("Chk_WhyCalendars") + ")\nContacts.ReadWrite  (" + Loc.T("Chk_WhyContacts") + ")";

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
    private bool _ready, _running, _finished, _sourceOk, _targetOk, _mapStale = true;
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
        for (var i = 0; i < StepCount; i++) _nav.Add(new NavItem { Label = Loc.T($"Step{i}_Label"), Number = i + 1, IsLast = i == StepCount - 1 });
        StepBar.ItemsSource = _nav;

        _boxView = CollectionViewSource.GetDefaultView(_boxes);
        _boxView.Filter = PassesFilter;
        MailboxGrid.ItemsSource = _boxView;
        MapGrid.ItemsSource = _map;
        RunGrid.ItemsSource = _runRows;
        FolderGrid.ItemsSource = _folders;
        _logView = CollectionViewSource.GetDefaultView(_log);
        _logView.Filter = o => o is LogLine l && l.Level >= LogLevel.Info && (OnlyProblems.IsChecked != true || l.Level >= LogLevel.Warn);
        LogList.ItemsSource = _logView;

        SourcePanel.Setup(Loc.T("Conn_SourceIntro"), Loc.T("Conn_SourcePermsIntro"), SourcePerms);
        TargetPanel.Setup(Loc.T("Conn_TargetIntro"), Loc.T("Conn_TargetPermsIntro"), TargetPerms);
        SourcePanel.ManageRequested += ManageRegs; TargetPanel.ManageRequested += ManageRegs;
        SourcePanel.TestRequested += async () => await ConnectAsync(SourcePanel, SessionRole.Source);
        TargetPanel.TestRequested += async () => await ConnectAsync(TargetPanel, SessionRole.Target);
        SourcePanel.SelectionChangedByUser += () => { _sourceOk = false; _boxes.Clear(); _map.Clear(); _mapStale = true; UpdateNav(); };
        TargetPanel.SelectionChangedByUser += () => { _targetOk = false; _targetBoxes = new(); _targetAddrs.Clear(); _mapStale = true; UpdateNav(); };

        if (!_demo) ImportLegacyConfig();
        RefreshRegistrations(null, null);
        Protected.Text = string.Join(Environment.NewLine, _regs.ProtectedMailboxes);
        LeaveOutCal.Text = string.Join(Environment.NewLine, _regs.LeaveOutCalendars);
        ContainerName.Text = _regs.LastContainer != "" ? _regs.LastContainer : Loc.T("Opt_DefaultFolderName");
        _ready = true;
        GoTo(0);
        if (demo != null) ApplyDemo(demo);
    }

    // =============================== registrations ===============================

    private void ManageRegs_Click(object s, RoutedEventArgs e) => ManageRegs();

    private void ManageRegs()
    {
        var keepS = SourcePanel.Selected; var keepT = TargetPanel.Selected;
        if (new RegistrationsDialog(_regs) { Owner = this }.ShowDialog() != true) _regs = AppRegistrationStore.Load();   // cancelled: drop in-memory edits
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
        if (msg.Contains("7000215")) return Loc.T("Err_SecretInvalid");
        if (msg.Contains("7000222")) return Loc.T("Err_SecretExpired");
        if (msg.Contains("700016")) return Loc.T("Err_ClientNotFound");
        if (msg.Contains("90002") || msg.Contains("90013")) return Loc.T("Err_TenantNotFound");
        if (msg.Contains("403") || msg.Contains("Authorization_RequestDenied")) return Loc.T("Err_MissingUserRead");
        return msg.Length > 260 ? msg[..260] + "…" : msg;
    }

    /// <summary>Compares the application permissions the app was granted with what this role needs. Optional ones only matter for additional calendars and contact folders.</summary>
    internal static (string Text, bool AllRequiredOk) CheckRoles(SessionRole role, IReadOnlyList<string> granted)
    {
        bool Has(string name) => granted.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool Any(string prefix) => granted.Any(g => g.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var rows = new List<(string Label, bool Ok, bool Required, string Why)>();
        if (role == SessionRole.Source) rows.Add(("Mail.Read", Has("Mail.Read") || Has("Mail.ReadWrite"), true, Loc.T("Chk_WhyReadMail")));
        else rows.Add(("Mail.ReadWrite", Has("Mail.ReadWrite"), true, Loc.T("Chk_WhyCreateFolders")));
        rows.Add((Loc.T("Chk_LabelMailboxItem"), Any("MailboxItem."), true, Loc.T("Chk_WhyImportExport")));
        rows.Add(("MailboxFolder.Read.All", Any("MailboxFolder."), true, Loc.T("Chk_WhyFolderTree")));
        rows.Add(("User.Read.All", Has("User.Read.All"), true, Loc.T("Chk_WhyListMailboxes")));
        if (role == SessionRole.Target)
        {
            rows.Add(("Calendars.ReadWrite", Has("Calendars.ReadWrite"), false, Loc.T("Chk_WhyCalendars")));
            rows.Add(("Contacts.ReadWrite", Has("Contacts.ReadWrite"), false, Loc.T("Chk_WhyContacts")));
        }
        var sb = new StringBuilder(Loc.T("Chk_Header") + "\n");
        foreach (var r in rows) sb.AppendLine(r.Ok ? $"  ✓ {r.Label}" : r.Required ? "  ✗ " + Loc.T("Chk_Missing", r.Label, r.Why) : "  – " + Loc.T("Chk_NotGranted", r.Label, r.Why));
        if (rows.Any(r => !r.Ok && r.Required)) sb.Append(Loc.T("Chk_AddMissing"));
        return (sb.ToString().TrimEnd(), rows.All(r => r.Ok || !r.Required));
    }

    private async Task ConnectAsync(ConnectionPanel panel, SessionRole role)
    {
        var reg = panel.Selected;
        if (reg == null) { panel.SetResult(Loc.T("Conn_ChooseReg"), false); return; }
        var secret = reg.GetClientSecret();
        if (secret == null) { panel.SetResult(Loc.T("Conn_NoSecret"), false); return; }
        panel.SetBusy(true); panel.SetResult(Loc.T("Conn_Connecting"), null);
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
            panel.SetResult(Loc.T("Conn_Connected", list.Count), true);
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
        DomainFilter.ItemsSource = new[] { Loc.T("Boxes_AllDomains") }.Concat(domains).ToList();
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
        SelectionText.Text = Loc.T("Boxes_Selection", sel, shown, _boxes.Count) + (hidden > 0 ? Loc.T("Boxes_SelectionHidden", hidden) : "");
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
            var isReady = t != "" && t.Contains('@') && !dupes.Contains(t) && _targetAddrs.Contains(t);
            m.Status = t == "" ? Loc.T("Match_Enter") : !t.Contains('@') ? Loc.T("Match_NotEmail") : dupes.Contains(t) ? Loc.T("Match_Dupe")
                : isReady ? Loc.T("Match_Ready") : Loc.T("Match_NotFound");
            m.Ready = isReady;
        }
        var readyCount = _map.Count(m => m.Ready);
        MapSummary.Text = Loc.T("Match_Summary", readyCount, _map.Count) + (readyCount < _map.Count ? Loc.T("Match_SummaryLeft", _map.Count - readyCount) : "");
    }

    private void ApplyDomain_Click(object s, RoutedEventArgs e) { foreach (var m in _map) m.Target = Suggest(m.Source); RecheckAll(); UpdateNav(); }

    private void ImportMap_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Match_CsvFilter"), Title = Loc.T("Match_ImportTitle") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var rows = MailboxMap.Load(dlg.FileName); var matched = 0;
            foreach (var r in rows) if (_map.FirstOrDefault(m => string.Equals(m.Source, r.Source, StringComparison.OrdinalIgnoreCase)) is { } m) { m.Target = r.Target; matched++; }
            RecheckAll(); UpdateNav();
            MessageBox.Show(this, Loc.T("Match_Imported", matched, rows.Count), Loc.T("Match_ImportedTitle"));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.T("Match_ReadFail"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ExportMap_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = Loc.T("Match_ExportFilter"), FileName = "mailbox-map.csv", Title = Loc.T("Match_ExportTitle") };
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
        FilterExpander.Header = n == 0 ? Loc.T("Opt_LimitNone") : Loc.T("Opt_LimitSome", n);
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

    private static string FolderName(string wellKnown) => wellKnown switch
    {
        "inbox" => Loc.T("Folder_Inbox"), "sentitems" => Loc.T("Folder_Sent"), "drafts" => Loc.T("Folder_Drafts"), "deleteditems" => Loc.T("Folder_Deleted"),
        "junkemail" => Loc.T("Folder_Junk"), "archive" => Loc.T("Folder_Archive"), _ => wellKnown
    };

    private void BuildReview()
    {
        var ready = _map.Where(m => m.Ready).ToList(); var left = _map.Count - ready.Count;
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("Rev_Copy", ready.Count, SourcePanel.Selected?.DisplayName, TargetPanel.Selected?.DisplayName));
        if (PimOnlyOn) sb.AppendLine(Loc.T("Rev_PimOnly"));
        else sb.AppendLine(Merge ? Loc.T("Rev_Merge") : Loc.T("Rev_Container", ContainerName.Text.Trim()));
        if (WantsPim && LeaveOutSet().Count > 0) sb.AppendLine(Loc.T("Rev_LeftOutCal", LeaveOutSet().Count));
        if (!PimOnlyOn) sb.AppendLine(Loc.T(WantsPim ? "Rev_PimYes" : "Rev_PimNo"));
        if (!PimOnlyOn) sb.AppendLine(Loc.T(Dedupe.IsChecked == true ? "Rev_DedupeOn" : "Rev_DedupeOff"));
        if (!PimOnlyOn)
        {
            var f = ReadFilters(); var omitted = new List<string>();
            foreach (var (box, name) in new[] { (FolderInbox, "inbox"), (FolderSent, "sentitems"), (FolderDrafts, "drafts"), (FolderDeleted, "deleteditems"), (FolderJunk, "junkemail"), (FolderArchive, "archive") })
                if (box.IsChecked == true) omitted.Add(FolderName(name));
            if (FolderUser.IsChecked == true) omitted.Add(Loc.T("Folder_User"));
            if (omitted.Count > 0) sb.AppendLine(Loc.T("Rev_LeftOut", string.Join(", ", omitted)));
            if (f.Since is { } sd) sb.AppendLine(Loc.T("Rev_Since", sd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
            if (f.MaxBytes > 0) sb.AppendLine(Loc.T("Rev_MaxSize", f.MaxBytes / 1048576));
        }
        if (!PimOnlyOn) sb.AppendLine(Loc.T(Verify.IsChecked == true ? "Rev_VerifyOn" : "Rev_VerifyOff"));
        sb.Append(Loc.T("Rev_Sequential"));
        PlanText.Text = sb.ToString();

        var warn = new List<string>();
        if (left > 0) warn.Add(Loc.T("Rev_WarnLeft", left));
        var resources = ready.Count(r => _boxes.FirstOrDefault(b => string.Equals(b.Upn, r.Source, StringComparison.OrdinalIgnoreCase))?.Kind == MailboxKind.UnlicensedResource);
        if (resources > 0) warn.Add(Loc.T("Rev_WarnResource", resources));
        if (Merge) warn.Add(Loc.T(PimOnlyOn ? "Rev_WarnMergePim" : "Rev_WarnMerge"));
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
        1 => _sourceOk ? "" : Loc.T("Hint_Source"),
        2 => _targetOk ? "" : Loc.T("Hint_Target"),
        3 => _boxes.Any(b => b.Selected) ? "" : Loc.T("Hint_TickOne"),
        4 => _map.Any(m => m.Ready) ? "" : Loc.T("Hint_OneReady"),
        5 => !Merge && ContainerName.Text.Trim() == "" ? Loc.T("Hint_EnterFolder") : !FiltersValid() ? Loc.T("Hint_Filters") : "",
        6 => _scanning ? Loc.T("Hint_Estimating") : !Merge || Understand.IsChecked == true ? "" : Loc.T("Hint_Understand"),
        _ => "",
    };

    private void UpdateNav()
    {
        if (!_ready) return;
        BackButton.Visibility = _step > 0 && _step < 7 && !_running ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = Loc.T(_step switch { 0 => "Btn_GetStarted", 6 => "Btn_Start", 7 => "Btn_ViewResults", 8 => "Btn_NewMigration", _ => "Btn_Next" });
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
        StepTitle.Text = TitleOf(step); StepDesc.Text = DescOf(step);
        for (var i = 0; i < _nav.Count; i++) _nav[i].Current = i == step;
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

    /// <summary>A finished step in the step bar takes you back to it (never while a copy is running).</summary>
    private void Step_Click(object s, RoutedEventArgs e)
    {
        if (_running || s is not FrameworkElement { DataContext: NavItem n }) return;
        if (n.Number - 1 < _step) GoTo(n.Number - 1);
    }

    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow_Click(object s, RoutedEventArgs e) => Close();

    private void Settings_Click(object s, RoutedEventArgs e)
    {
        if (_demo) return;   // demo mode never writes the saved settings
        new SettingsDialog(_regs) { Owner = this }.ShowDialog();
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
        if (sSecret == null || tSecret == null) { MessageBox.Show(this, Loc.T("Run_NoSecret"), Loc.T("Run_CannotStart"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var prot = ProtectedList();
        var cfg = new CopyConfig(new SideConfig(sReg.TenantId, sReg.ClientId, "", sReg.ClientId, sSecret), new SideConfig(tReg.TenantId, tReg.ClientId, "", tReg.ClientId, tSecret), prot);
        var rows = ready.Select(m => new MapRow(m.Source.Trim(), m.Target.Trim())).ToList();
        var problems = MailboxMap.Validate(rows, string.Equals(sReg.TenantId, tReg.TenantId, StringComparison.OrdinalIgnoreCase), cfg.IsProtected);
        if (problems.Count > 0) { MessageBox.Show(this, Loc.T("Run_FixFirst") + "\n\n" + string.Join("\n", problems), Loc.T("Run_CannotStart"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var merge = Merge; var container = ContainerName.Text.Trim(); var pim = WantsPim; var pimOnly = PimOnlyOn; var nonEmpty = merge && AllowNonEmpty.IsChecked == true;
        var dedupe = Dedupe.IsChecked == true; var verify = Verify.IsChecked == true; var fresh = Fresh.IsChecked == true; var flt = ReadFilters(); var leaveOut = LeaveOutSet();

        var ok = MessageBox.Show(this, Loc.T("Run_Confirm", rows.Count, merge ? Loc.T("Run_ConfirmMerge") : Loc.T("Run_ConfirmContainer", container)),
            Loc.T("Run_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;

        SaveSettings();
        _log.Clear(); _warnings = _errors = 0; UpdateCounts(); _folders.Clear(); _byPath.Clear(); _runRows.Clear();
        for (var i = 0; i < rows.Count; i++) _runRows.Add(new RunRow { Index = i + 1, Source = rows[i].Source, Target = rows[i].Target });
        _verifyOn = verify; _cur = 0; _runStart = DateTime.Now;
        _prog = new double[rows.Count]; _weights = new double[rows.Count]; _known = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++) if (_est.TryGetValue(rows[i].Source, out var e0)) { _weights[i] = Math.Max(1, e0.Items); _known[i] = true; }
        MailboxBar.Value = 0; TotalsText.Text = ""; PhaseText.Text = ""; EtaText.Text = Loc.T("Run_Starting");
        _timer.Tick -= Timer_Tick; _timer.Tick += Timer_Tick; _timer.Start();
        RunHeadline.Text = Loc.T("Run_Headline", rows.Count);
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
            headline = Loc.T("Run_Finished", Parts(o.Done, o.Problems, o.Failed, notRun, true));
            if (o.Problems == 0 && o.Failed == 0 && notRun == 0) headline += verify ? " " + Loc.T("Run_AllCompared") : "";
            else headline += " " + Loc.T("Run_SelectRow");
        }
        catch (Exception ex) { _runLog.Error($"STOPPED: {ex.GetType().Name}: {ex.Message}", ex); headline = Loc.T("Run_StoppedByError", ex.Message); runError = ex.Message; }

        _runLog.Info($"\nFinished with {_runLog.Errors} errors and {_runLog.Warnings} warnings.");
        _runLog.SaveReport();
        _running = false; _finished = true; _timer.Stop();
        for (var i = 0; i < _prog.Length; i++) if (_runRows[i].State is MailboxState.Done or MailboxState.Problems or MailboxState.Failed) _prog[i] = 1;
        MailboxBar.Value = _runRows.All(r => r.State is MailboxState.Done or MailboxState.Problems or MailboxState.Failed) ? 1000 : MailboxBar.Value;
        EtaText.Text = Loc.T("Run_Took", Dur(DateTime.Now - _runStart)).Replace("..", ".");
        RunHeadline.Text = headline; PhaseText.Text = "";
        OpenLogButton.Visibility = Visibility.Visible; OpenResultsButton.Visibility = _resultsPath != null ? Visibility.Visible : Visibility.Collapsed;
        OpenItemsButton.Visibility = File.Exists(_runLog.ItemsPath) ? Visibility.Visible : Visibility.Collapsed;
        ShowResults(outcome, runError, merge, container, pim, verify, DateTime.Now - _runStart);
        GoTo(8);
    }

    // =============================== results screen ===============================

    private string _summaryText = "";

    /// <summary>"Copied cleanly: 3 · Need a look: 1 · Failed: 1" (the last three only when they are not zero).</summary>
    private static string Parts(int done, int prob, int fail, int notRun, bool includeClean)
    {
        var parts = new List<string>();
        if (includeClean) parts.Add(Loc.T("Part_Clean", done));
        if (prob > 0) parts.Add(Loc.T("Part_Problems", prob));
        if (fail > 0) parts.Add(Loc.T("Part_Failed", fail));
        if (notRun > 0) parts.Add(Loc.T("Part_NotRun", notRun));
        return string.Join(" · ", parts);
    }

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

        // Banner colors come from the palette (background brush, border brush), so they follow the theme.
        (string Title, string Sub, string Back, string Border) banner =
            error != null ? (Loc.T("Res_Stopped"), error, "DangerBgBrush", "DangerBrush")
            : clean ? (Loc.T("Res_Complete"), verify && vAll > 0 ? Loc.T("Res_CompleteVerified", done, vAll.ToString("N0")) : Loc.T("Res_CompleteSub", done), "SuccessBgBrush", "SuccessBrush")
            : notRun > 0 && fail == 0 && prob == 0 ? (Loc.T("Res_StoppedEarly"), Loc.T("Res_StoppedEarlySub", done, notRun), "WarningBgBrush", "WarningBrush")
            : (Loc.T("Res_NeedLook"), Loc.T("Res_NeedLookSub", done, results.Count, Parts(done, prob, fail, notRun, false)).Trim(), fail > 0 ? "DangerBgBrush" : "WarningBgBrush", fail > 0 ? "DangerBrush" : "WarningBrush");
        ResultBanner.Background = ThemeManager.Brush(banner.Back);
        ResultBanner.BorderBrush = ThemeManager.Brush(banner.Border);
        ResultTitle.Text = banner.Title; ResultSub.Text = banner.Sub;

        StatsList.ItemsSource = new[]
        {
            new StatItem(Loc.T("Stat_Mailboxes"), Loc.T("Stat_Of", done, results.Count)),
            new StatItem(Loc.T("Stat_Messages"), copied.ToString("N0")),
            new StatItem(Loc.T("Stat_Already"), already.ToString("N0")),
            new StatItem(Loc.T("Stat_Failed"), failedItems.ToString("N0")),
            new StatItem(Loc.T("Stat_Verified"), verify && vAll > 0 ? Loc.T("Stat_Of", vOk.ToString("N0"), vAll.ToString("N0")) : verify ? Loc.T("Stat_None") : Loc.T("Stat_NotChecked")),
            new StatItem(Loc.T("Stat_Time"), Dur(took)),
        };

        ResultGrid.ItemsSource = results.Select(r => new ResultRow
        {
            Source = r.Row.Source, Target = r.Row.Target,
            Result = r.State is MailboxState.Done or MailboxState.Problems or MailboxState.Failed or MailboxState.Skipped ? RunRow.StateName(r.State, running: false) : "",
            StatusLevel = r.State switch { MailboxState.Done => "Ready", MailboxState.Problems => "Look", MailboxState.Failed => "Error", _ => "" },
            Copied = r.Outcome?.Imported.ToString("N0") ?? "–", AlreadyThere = r.Outcome?.AlreadyDone.ToString("N0") ?? "–", Failed = r.Outcome?.Failed.ToString("N0") ?? "–",
            Verified = r.Outcome?.Verification is { } v ? Loc.T("Stat_Of", v.Identical.ToString("N0"), v.Paired.ToString("N0")) : verify ? "–" : Loc.T("Stat_NotChecked"),
            Notes = r.State == MailboxState.Done ? "" : r.Message,
            HasProblem = r.State is MailboxState.Problems or MailboxState.Failed,
        }).ToList();

        var steps = new List<string>();
        if (error != null || fail > 0 || prob > 0 || notRun > 0) steps.Add(Loc.T("Next_Problems"));
        steps.Add(merge ? Loc.T("Next_SpotMerge") : Loc.T("Next_SpotContainer", container));
        if (pim) steps.Add(Loc.T("Next_Pim"));
        steps.Add(Loc.T("Next_Rerun"));
        steps.Add(Loc.T("Next_Cleanup"));
        NextSteps.Text = string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}"));

        _summaryText = Loc.T("Sum_Header", banner.Title) + "\n" + banner.Sub + "\n"
            + (verify ? Loc.T("Sum_LineVerified", done, results.Count, copied.ToString("N0"), already.ToString("N0"), failedItems.ToString("N0"), vOk.ToString("N0"), vAll.ToString("N0"), Dur(took))
                      : Loc.T("Sum_Line", done, results.Count, copied.ToString("N0"), already.ToString("N0"), failedItems.ToString("N0"), Dur(took))) + "\n"
            + string.Join("\n", results.Select(r => $"  {r.Row.Source} -> {r.Row.Target}: {RunRow.StateName(r.State, running: false)}" + (r.Message != "" ? $" ({r.Message})" : "")));
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
        var m = PhaseRx.Match(phase);
        // The engine's phase text is English; the app shows the same phase in the chosen language, keyed by its number.
        PhaseText.Text = Loc.T("Run_Phase", n, total, m.Success ? $"[{m.Groups[1].Value}/6] " + Loc.T("Phase_" + m.Groups[1].Value) : phase);
        if (m.Success && n >= 1 && n <= _prog.Length)
        {
            var k = int.Parse(m.Groups[1].Value);
            _prog[n - 1] = k <= 3 ? OverheadShare * k / 3 : k == 4 ? OverheadShare : OverheadShare + CopyShare;
            RecalcProgress();
        }
    }

    private void OnTotals(int n, TreeTotals t)
    {
        TotalsText.Text = Loc.T("Run_Totals", n, (t.Imported + t.AlreadyDone + t.Failed).ToString("N0"), t.Total.ToString("N0"), t.Imported.ToString("N0"), t.AlreadyDone.ToString("N0"), t.Failed.ToString("N0"));
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
        string eta = f >= 0.03 && elapsed.TotalSeconds >= 20 ? Loc.T("Run_EtaLeft", Dur(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - f) / f)))
            : _estRange != "" ? Loc.T("Run_EtaEstimated", _estRange) : Loc.T("Run_EtaEstimating");
        EtaText.Text = Loc.T("Run_Eta", f, finished, _runRows.Count, eta, Dur(elapsed));
    }

    private static string Dur(TimeSpan t) =>
        t.TotalSeconds < 60 ? Loc.T("Dur_Under") : t.TotalMinutes < 60 ? Loc.T("Dur_Min", Math.Round(t.TotalMinutes)) : Loc.T("Dur_HourMin", (int)t.TotalHours, t.Minutes);

    // =============================== estimate (read-only scan of the selected mailboxes) ===============================

    private void SkipEstimate_Click(object s, RoutedEventArgs e) => _scanCts?.Cancel();

    private async Task EstimateAsync()
    {
        _est.Clear(); _estRange = "";
        var rows = _map.Where(m => m.Ready).Select(m => new MapRow(m.Source.Trim(), m.Target.Trim())).ToList();
        var sReg = SourcePanel.Selected; var tReg = TargetPanel.Selected;
        var sSecret = sReg?.GetClientSecret(); var tSecret = tReg?.GetClientSecret();
        if (rows.Count == 0 || sReg == null || tReg == null || sSecret == null || tSecret == null) { EstimateText.Text = Loc.T("Est_NeedsConn"); return; }
        var cfg = new CopyConfig(new SideConfig(sReg.TenantId, sReg.ClientId, "", sReg.ClientId, sSecret), new SideConfig(tReg.TenantId, tReg.ClientId, "", tReg.ClientId, tSecret), ProtectedList());
        var flt0 = ReadFilters(); var leaveOut0 = LeaveOutSet(); var pimOnlyScan = PimOnlyOn; var mergeScan = Merge; var pimScan = WantsPim; var verify = Verify.IsChecked == true && !pimOnlyScan;

        _scanning = true; EstimateBar.Visibility = SkipEstimateButton.Visibility = Visibility.Visible; EstimateBar.Value = 0;
        EstimateText.Text = Loc.T("Est_Reading", rows.Count); UpdateNav();
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

        if (token.IsCancellationRequested) { EstimateText.Text = Loc.T("Est_Skipped"); UpdateNav(); return; }
        if (outcome == null) { EstimateText.Text = Loc.T("Est_Failed", Friendly(error ?? "")); UpdateNav(); return; }

        foreach (var r in outcome.Results.Where(r => r.Outcome != null)) _est[r.Row.Source] = (r.Outcome!.Listed, r.Outcome.Bytes);
        var items = _est.Values.Sum(v => v.Items); var mb = _est.Values.Sum(v => v.Bytes) / 1048576.0;
        var secs = rows.Count * SecPerMailbox + items * (SecPerMessageCopy + (verify ? SecPerMessageVerify : 0));
        var lo = TimeSpan.FromSeconds(secs * 0.8); var hi = TimeSpan.FromSeconds(secs * 1.8);
        _estRange = Dur(lo) == Dur(hi) ? Dur(hi) : $"{(lo.TotalMinutes < 60 ? Math.Round(lo.TotalMinutes).ToString() : Dur(lo))}–{Dur(hi)}";
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("Est_Summary", items.ToString("N0"), mb.ToString("N0"), _est.Count));
        sb.AppendLine(Loc.T("Est_Time", _estRange).Replace("..", "."));
        sb.Append(Loc.T("Est_Basis"));
        var failed = outcome.Results.Where(r => r.State == MailboxState.Failed).ToList();
        if (failed.Count > 0) sb.Append("\n\n" + Loc.T("Est_Unreadable", failed.Count, Friendly(failed[0].Message)));
        EstimateText.Text = sb.ToString();
        UpdateNav();
    }


    private void OnFolders(IReadOnlyList<FolderInfo> list)
    {
        _folders.Clear(); _byPath.Clear();
        foreach (var f in list)
        {
            var row = new FolderRow { Path = f.Path, Display = new string(' ', f.Depth * 3) + f.Path, WellKnown = f.WellKnown, Items = f.ItemCount };
            _byPath[f.Path] = row; _folders.Add(row);
        }
    }

    private void OnFolderChanged(FolderProgress p)
    {
        if (!_byPath.TryGetValue(p.Path, out var r)) return;
        if (p.Status == "Mismatch") { r.Status = "Mismatch"; return; }
        r.Imported = p.Imported; r.AlreadyDone = p.AlreadyDone; r.Failed = p.Failed; r.Status = p.Status;
    }

    private void Stop_Click(object s, RoutedEventArgs e) { _cts?.Cancel(); StopButton.IsEnabled = false; PhaseText.Text = Loc.T("Run_Stopping"); }

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

    private void UpdateCounts() => CountsText.Text = Loc.T("Run_Counts", _errors, _warnings);
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
        if (MessageBox.Show(this, Loc.T("Run_Closing"), Loc.T("Run_ClosingTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) e.Cancel = true;
        else _cts?.Cancel();
    }
}
