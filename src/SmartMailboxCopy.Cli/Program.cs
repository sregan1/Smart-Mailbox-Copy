using SmartMailboxCopy;

const string Usage = """
    Smart Mailbox Copy: copy a mail folder between Exchange Online mailboxes (same or different tenant).

    Usage:
      SmartMailboxCopy copy --source-folder <name> --target-folder <name> [options]
      SmartMailboxCopy copy-tree (--target-container <name> | --merge-wellknown) [options]
          Copies the whole mail folder tree into a new container folder (--max is the total for the tree, default 500).
      SmartMailboxCopy list-mailboxes --side source|target [--kind users|resources|guests|all] [--domain <d>] [--contains <text>]
                                      [--enabled-only] [--map-to-domain <d> --out <map.csv>] [--app-only]
          Lists the tenant's mailboxes (read only). With --map-to-domain and --out it also writes a source,target map
          (user@old.com becomes user@<d>) that you can edit and pass to copy-batch.
      SmartMailboxCopy copy-batch --map <map.csv> (--target-container <name> | --merge-wellknown) --app-only [options]
          Copies every mailbox in the map, one after another, each as a copy-tree run. Needs --app-only: a signed-in person can only reach their own mailbox.

    Options:
      --config <path>        Config file (default: smartmailboxcopy.config.json, searched upward from the current folder)
      --max <n>              Refuse a source folder with more than n items (default 100)
      --app-only             Use application permissions (client secrets) instead of interactive sign-in
      --allow-existing       Import into a destination folder that already exists
      --fresh                Ignore the resume journal and start over
      --include-rooms        (copy-tree) Include room/equipment/shared mailboxes. Off by default; not supported yet, stops with a message
      --merge-wellknown      (copy-tree) Instead of a container: Inbox, Sent Items, Drafts, Deleted Items, Junk and Archive go into the
                             target's real folders; other folders are recreated beside/under them. Adds mail only, never changes or deletes.
                             Refuses a target folder that already holds mail unless --allow-nonempty is also given.
      --allow-nonempty       (copy-tree --merge-wellknown) Add alongside mail that is already in the target's folders
      --include-pim          (copy-tree --merge-wellknown) M3 first step: also copy the default Contacts and Calendar into the target's default ones
                             (counted and reconciled; not yet compared field by field). Other contact/calendar folders are still skipped.
      --exclude-folders <list>  (copy-tree, copy-batch) Leave out top-level folders and everything under them: any of inbox, sentitems,
                             drafts, deleteditems, junkemail, archive, outbox, user (the person's own top-level folders). Comma separated.
      --since <date>         (copy-tree, copy-batch) Only mail created on or after this date (yyyy-MM-dd). Does not apply to contacts and calendar.
      --max-item-mb <n>      (copy-tree, copy-batch) Skip mail items larger than n MB. Does not apply to contacts and calendar.
      --exclude-calendars <list>  (with --include-pim) Names of additional calendars or contact folders to leave out, separated by semicolons
                             (for example a subscribed holiday calendar). A contact folder takes its subfolders with it.
      --no-extra-pim         (with --include-pim) Copy only the default Calendar and Contacts, not additional calendars and contact folders
      --pim-only             (with --merge-wellknown --include-pim) Copy only the default Contacts and Calendar; no mail at all
      --no-dedupe            (copy-tree) Do not skip source messages whose internet message id is already in the target folder
                             (by default a re-run, or a lost resume journal, cannot duplicate mail)
      --scan                 (copy-tree) Read-only look at the source folder tree and counts; the target is not touched
      --verbose              Also show the request-by-request trace on screen (it is always written to the log file)
      --no-verify            Skip the field-by-field comparison afterwards (copy and copy-tree)

    The source mailbox is only ever read. Both accounts are checked against the config before anything happens.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
if (args[0] is not ("copy" or "copy-tree" or "list-mailboxes" or "copy-batch")) { Console.WriteLine($"Unknown command '{args[0]}'.\n\n{Usage}"); return 1; }
var list = args[0] == "list-mailboxes"; var batch = args[0] == "copy-batch";
var tree = args[0] == "copy-tree" || batch;

string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);

var sourceFolder = Opt("--source-folder"); var targetFolder = Opt("--target-folder"); var container = Opt("--target-container");
var merge = Flag("--merge-wellknown");
if (batch && !Flag("--app-only")) { Console.WriteLine($"copy-batch needs --app-only. A signed-in person can only reach their own mailbox; copying other people's mailboxes uses application permissions.\n\n{Usage}"); return 1; }
if (batch && string.IsNullOrWhiteSpace(Opt("--map"))) { Console.WriteLine($"copy-batch needs --map <file.csv> (source,target per line). Make one with list-mailboxes --map-to-domain.\n\n{Usage}"); return 1; }
if (tree && merge == !string.IsNullOrWhiteSpace(container))
{ Console.WriteLine($"copy-tree needs exactly one of --target-container <name> or --merge-wellknown.\n\n{Usage}"); return 1; }
if (!tree && !list && (string.IsNullOrWhiteSpace(sourceFolder) || string.IsNullOrWhiteSpace(targetFolder)))
{ Console.WriteLine($"--source-folder and --target-folder are required.\n\n{Usage}"); return 1; }

var appOnly = Flag("--app-only");
var exTokens = (Opt("--exclude-folders") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList();
var knownEx = new[] { "inbox", "sentitems", "drafts", "deleteditems", "junkemail", "archive", "outbox", "user" };
if (exTokens.FirstOrDefault(x => !knownEx.Contains(x)) is { } badEx) { Console.WriteLine($"--exclude-folders: '{badEx}' is not one of {string.Join(", ", knownEx)}."); return 1; }
var leaveOutPim = (Opt("--exclude-calendars") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
var exUser = exTokens.Contains("user"); var exWk = exTokens.Where(x => x != "user").ToHashSet(StringComparer.OrdinalIgnoreCase);
DateTime? since = null;
if (Opt("--since") is { } sinceText)
{
    if (!DateTime.TryParse(sinceText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var sd)) { Console.WriteLine("--since needs a date such as 2025-01-01."); return 1; }
    since = sd;
}
long maxBytes = long.TryParse(Opt("--max-item-mb"), out var mm) && mm > 0 ? mm * 1048576 : 0;
var configPath = Opt("--config") ?? CopyConfig.Find(Directory.GetCurrentDirectory()) ?? CopyConfig.Find(AppContext.BaseDirectory);
if (configPath == null) { Console.WriteLine($"{CopyConfig.FileName} not found. Copy smartmailboxcopy.config.sample.json and fill it in."); return 1; }

CopyConfig config;
try { config = CopyConfig.Load(configPath, appOnly, needAccounts: !(appOnly && (list || batch))); }
catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var log = new RunLog(verbose: Flag("--verbose"));
log.Info($"Smart Mailbox Copy: {args[0]}. Full log: {log.FullLogPath}");
log.Info($"Errors and warnings only: {log.ErrorLogPath}");
AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled exception", e.ExceptionObject as Exception);
var exitCode = 0;
try
{
    if (list)
    {
        var sideName = Opt("--side") ?? "";
        if (sideName is not ("source" or "target")) { Console.WriteLine("list-mailboxes needs --side source or --side target."); return 1; }
        var kindArg = Opt("--kind") ?? "users";
        var kinds = kindArg switch
        {
            "users" => new HashSet<MailboxKind> { MailboxKind.User }, "resources" => new HashSet<MailboxKind> { MailboxKind.UnlicensedResource },
            "guests" => new HashSet<MailboxKind> { MailboxKind.Guest }, "all" => new HashSet<MailboxKind>(),
            _ => throw new InvalidOperationException("--kind must be users, resources, guests or all.")
        };
        var filter = new MailboxFilter { Kinds = kinds, Domain = Opt("--domain") ?? "", Contains = Opt("--contains") ?? "", EnabledOnly = Flag("--enabled-only") };
        var directory = new MailboxDirectory(config, sideName == "source" ? SessionRole.Source : SessionRole.Target, appOnly, log);
var found = (await directory.ListAsync()).Where(filter.Matches).ToList();
        foreach (var m in found) Console.WriteLine($"{m.Upn,-48} {(m.Enabled ? "enabled " : "disabled")} {m.KindText}");
        Console.WriteLine($"\n{found.Count} mailboxes.");
        if (appOnly) Console.WriteLine("Application permissions granted to this app: " + string.Join(", ", await directory.GetGrantedRolesAsync()));
        if (Opt("--map-to-domain") is { Length: > 0 } toDomain)
        {
            var outPath = Opt("--out");
            if (string.IsNullOrWhiteSpace(outPath)) { Console.WriteLine("--map-to-domain needs --out <file.csv>."); return 1; }
            MailboxMap.Save(outPath, MailboxMap.DomainSwap(found.Select(m => m.Upn), toDomain));
            Console.WriteLine($"Wrote a map of {found.Count} rows to {outPath}. Review it, fix any names that differ in the target tenant, then run copy-batch.");
        }
    }
    else if (batch)
    {
        var rows = MailboxMap.Load(Opt("--map")!);
        var bopts = () => new TreeOptions
        {
            TargetContainer = container ?? "", MergeWellKnown = merge, AllowNonEmpty = Flag("--allow-nonempty"), Dedupe = !Flag("--no-dedupe"), IncludePim = Flag("--include-pim"), PimOnly = Flag("--pim-only"), ExtraPim = !Flag("--no-extra-pim"), LeaveOutPimNames = leaveOutPim, AppOnly = true,
            MaxItems = int.TryParse(Opt("--max"), out var bmax) ? bmax : 100000,
            AllowExisting = Flag("--allow-existing"), Fresh = Flag("--fresh"), IncludeRooms = false, ScanOnly = Flag("--scan"), ExcludeWellKnown = exWk, ExcludeUserFolders = exUser, CreatedOnOrAfter = since, MaxItemBytes = maxBytes, Verify = !Flag("--no-verify"),
        };
        var b = await new BatchCopier(config, rows, bopts, log).RunAsync(cts.Token);
        Console.WriteLine($"results:    {BatchCopier.SaveResults(b)}");
        if (b.Problems > 0 || b.Failed > 0 || b.Results.Any(r => r.State == MailboxState.Skipped)) exitCode = 2;
    }
    else if (tree)
    {
        var topts = new TreeOptions
        {
            TargetContainer = container ?? "", MergeWellKnown = merge, AllowNonEmpty = Flag("--allow-nonempty"), Dedupe = !Flag("--no-dedupe"), IncludePim = Flag("--include-pim"), PimOnly = Flag("--pim-only"), ExtraPim = !Flag("--no-extra-pim"), LeaveOutPimNames = leaveOutPim, AppOnly = appOnly,
            MaxItems = int.TryParse(Opt("--max"), out var tmax) ? tmax : 500,
            AllowExisting = Flag("--allow-existing"), Fresh = Flag("--fresh"), IncludeRooms = Flag("--include-rooms"),
            ScanOnly = Flag("--scan"), ExcludeWellKnown = exWk, ExcludeUserFolders = exUser, CreatedOnOrAfter = since, MaxItemBytes = maxBytes, Verify = !Flag("--no-verify"),
        };
        var t = await new TreeCopier(config, topts, log).RunAsync(cts.Token);
        if (t.Failed > 0 || t.CountMismatches > 0 || (t.Verification?.Different ?? 0) > 0 || (t.Verification?.Unpaired ?? 0) > 0) exitCode = 2;
    }
    else
    {
        var options = new CopyOptions
        {
            SourceFolder = sourceFolder!, TargetFolder = targetFolder!, AppOnly = appOnly,
            MaxItems = int.TryParse(Opt("--max"), out var max) ? max : 100,
            AllowExisting = Flag("--allow-existing"), Fresh = Flag("--fresh"), Verify = !Flag("--no-verify"),
        };
        var outcome = await new FolderCopier(config, options, log).RunAsync(cts.Token);
        if (outcome.Failed > 0 || (outcome.Verification?.Different ?? 0) > 0) exitCode = 2;
    }
}
catch (Exception ex)
{
    log.Error($"STOPPED: {ex.GetType().Name}: {ex.Message}", ex);
    exitCode = 1;
}
log.Info($"\nFinished with {log.Errors} errors and {log.Warnings} warnings (exit code {exitCode}).");
log.Info($"Full log:   {log.FullLogPath}");
log.Info($"Errors:     {log.ErrorLogPath}");
if (File.Exists(log.ItemsPath)) log.Info($"Item list:  {log.ItemsPath}");
Console.WriteLine($"report:     {log.SaveReport()}");
return exitCode;
