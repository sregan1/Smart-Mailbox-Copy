using System.Windows;
using System.Windows.Threading;

namespace SmartMailboxCopy.App;

public partial class MainWindow
{
    private bool _demo;

    /// <summary>Fills the wizard with fictional data and shows one screen (see <see cref="DemoOptions"/>). Makes no network calls and saves nothing.</summary>
    private void ApplyDemo(DemoOptions d)
    {
        Width = 1200; Height = 880;
        var src = DemoData.Source(); var tgt = DemoData.Target();
        SourcePanel.Select(_regs.Registrations[0]); TargetPanel.Select(_regs.Registrations[1]);
        LoadSourceBoxes(src); _sourceOk = true; SourcePanel.SetResult($"Connected. {src.Count} accounts with a mailbox address found.", true);
        _targetBoxes = tgt; _targetAddrs = tgt.SelectMany(m => new[] { m.Upn, m.Mail }).ToHashSet(StringComparer.OrdinalIgnoreCase); _targetOk = true;
        TargetPanel.SetResult($"Connected. {tgt.Count} accounts with a mailbox address found.", true);
        SourcePanel.SetChecks(CheckRoles(SessionRole.Source, new[] { "Mail.Read", "MailboxItem.ImportExport.All", "MailboxItem.Read.All", "MailboxFolder.Read.All", "User.Read.All" }).Text, true);
        TargetPanel.SetChecks(CheckRoles(SessionRole.Target, new[] { "Mail.ReadWrite", "MailboxItem.ImportExport.All", "MailboxItem.Read.All", "MailboxFolder.Read.All", "User.Read.All", "Calendars.ReadWrite", "Contacts.ReadWrite" }).Text, true);
        foreach (var m in _boxes) m.Selected = DemoData.Picked.Contains(m.Upn, StringComparer.OrdinalIgnoreCase);
        UpdateSelectionText();

        var step = int.Parse(d.Step);
        if (step >= 4) BuildMap();

        if (step == 5 && d.Variant == "filters") SetDemoMergeWithLimits();
        if (step == 5 && d.Variant == "calendar") { PimOnly.IsChecked = true; Option_Changed(this, (RoutedEventArgs)null!); }
        if (step is 6 or 8) SetDemoMergeWithLimits();

        GoTo(step);

        if (step == 6)
        {
            EstimateText.Text = "About 41,380 messages (12,640 MB) across 5 mailboxes.\nEstimated time: 3 h 51 min–8 h 40 min.\nBased on speeds measured in our tests with ordinary messages; very large attachments take longer. Mailboxes are copied one after another. If you stop, the next run resumes where it left off.";
            EstimateBar.Visibility = SkipEstimateButton.Visibility = Visibility.Collapsed; _scanning = false; UpdateNav();
        }
        if (step == 7) ShowDemoCopy();
        if (step == 8) ShowDemoResults(d.Variant == "clean");

        if (d.ScreenshotPath != null)
            ContentRendered += async (_, _) =>
            {
                await Task.Delay(1500);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                ScreenshotWriter.Save(this, d.ScreenshotPath);
                _running = false; Close();
            };
    }

    private void SetDemoMergeWithLimits()
    {
        ModeMerge.IsChecked = true;
        FilterExpander.IsExpanded = true;
        FolderDeleted.IsChecked = true; FolderJunk.IsChecked = true;
        DateFilter.IsChecked = true; SinceDate.SelectedDate = new DateTime(2024, 1, 1);
        SizeFilter.IsChecked = true;
        Option_Changed(this, (RoutedEventArgs)null!);
    }

    private void ShowDemoCopy()
    {
        _running = true; _runStart = DateTime.Now;
        var rows = new (string From, string To, MailboxState State, string Msg)[]
        {
            ("alex.rivera@contoso.com", "alex.rivera@fabrikam.com", MailboxState.Done, "8,420 imported, 0 already done, 8,420 of 8,420 identical"),
            ("priya.shah@contoso.com", "priya.shah@fabrikam.com", MailboxState.Done, "12,115 imported, 0 already done, 12,115 of 12,115 identical"),
            ("morgan.chen@contoso.com", "morgan.chen@fabrikam.com", MailboxState.Running, "Copying…"),
            ("sam.patel@contoso.com", "sam.patel@fabrikam.com", MailboxState.Waiting, ""),
            ("taylor.brooks@contoso.com", "taylor.brooks@fabrikam.com", MailboxState.Waiting, ""),
        };
        _runRows.Clear();
        for (var i = 0; i < rows.Length; i++) _runRows.Add(new RunRow { Index = i + 1, Source = rows[i].From, Target = rows[i].To, State = rows[i].State, Message = rows[i].Msg });

        _folders.Clear(); _byPath.Clear();
        void F(string path, string kind, int items, int copied, int already, int failed, string status)
        {
            var r = new FolderRow { Path = path, Display = new string(' ', path.Count(c => c == '.') * 3) + path, Kind = kind, Items = items, Imported = copied, AlreadyDone = already, Failed = failed, Status = status };
            _byPath[path] = r; _folders.Add(r);
        }
        F("1", "inbox", 3120, 3120, 0, 0, "Done"); F("1.1", "user folder", 640, 640, 0, 0, "Done"); F("2", "sentitems", 2275, 2275, 0, 0, "Done");
        F("3", "drafts", 18, 18, 0, 0, "Done"); F("4", "deleteditems", 905, 905, 0, 0, "Done"); F("5", "user folder", 1480, 1042, 0, 0, "Copying");
        F("5.1", "user folder", 760, 0, 0, 0, "Waiting"); F("6", "archive", 3900, 0, 0, 0, "Waiting");

        FolderExpander.IsExpanded = true;
        MailboxBar.Value = 420;
        RunHeadline.Text = "Copying 5 mailboxes…";
        EtaText.Text = "42% · 2 of 5 mailboxes finished · about 1 h 12 min left · 52 min so far";
        PhaseText.Text = "Mailbox 3 of 5: [4/6] Copy: import the items";
        TotalsText.Text = "Mailbox 3: 8,442 of 13,098 items processed — 8,442 copied, 0 already there, 0 failed";
        UpdateNav();
    }

    private void ShowDemoResults(bool clean)
    {
        _finished = true;
        BatchOutcome Make()
        {
            MailboxProgress P(int i, string from, string to, int n, int failed, string note) =>
                new(i, new MapRow(from, to), failed > 0 ? MailboxState.Problems : MailboxState.Done,
                    new TreeOutcome(41, 5, n + failed, 0, n, failed, 0, new VerifyOutcome(n, n, 0, failed), (long)n * 310_000), note);
            return new BatchOutcome(new[]
            {
                P(1, "alex.rivera@contoso.com", "alex.rivera@fabrikam.com", 8420, 0, ""),
                P(2, "priya.shah@contoso.com", "priya.shah@fabrikam.com", 12115, clean ? 0 : 3,
                    clean ? "" : "12,115 imported, 3 failed, 0 folders with different counts, 0 messages differ, 3 not compared"),
                P(3, "morgan.chen@contoso.com", "morgan.chen@fabrikam.com", 9873, 0, ""),
                P(4, "sam.patel@contoso.com", "sam.patel@fabrikam.com", 6210, 0, ""),
                P(5, "taylor.brooks@contoso.com", "taylor.brooks@fabrikam.com", 4762, 0, ""),
            });
        }
        ShowResults(Make(), null, true, "Migrated mail", true, true, new TimeSpan(4, 12, 0));
        OpenResultsButton.Visibility = OpenLogButton.Visibility = OpenItemsButton.Visibility = Visibility.Visible;
        GoTo(8);
    }
}
