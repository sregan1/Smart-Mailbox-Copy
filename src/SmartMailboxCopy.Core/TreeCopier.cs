using System.Text.Json;

namespace SmartMailboxCopy;

public sealed class TreeOptions
{
    /// <summary>Container mode: the new top-level target folder everything is copied into. Leave empty when MergeWellKnown is set.</summary>
    public string TargetContainer { get; init; } = "";
    /// <summary>
    /// Merge mode (M2): Inbox, Sent Items, Drafts, Deleted Items, Junk and Archive go into the target's real folders, and the rest of the tree
    /// is recreated under the matching parent (top-level user folders at the target's root). Items are only ever added; nothing is deleted.
    /// </summary>
    public bool MergeWellKnown { get; init; }
    /// <summary>Merge mode refuses to import into a target folder that already holds mail on a fresh run, unless this is set.</summary>
    public bool AllowNonEmpty { get; init; }
    /// <summary>Skip source items whose internet message id is already in the target folder (so a re-run, or a lost journal, cannot duplicate mail). On by default.</summary>
    public bool Dedupe { get; init; } = true;
    /// <summary>
    /// M3, first step (merge mode only): also copy the default Contacts and Calendar folders into the target's default ones, through the same
    /// import/export path. Counted and reconciled, not yet compared field by field. Other contact/calendar folders are still skipped.
    /// </summary>
    public bool IncludePim { get; init; }
    /// <summary>With IncludePim: copy only the default Contacts and Calendar and skip every mail folder (a safe way to test calendar copying on its own).</summary>
    public bool PimOnly { get; init; }
    /// <summary>With IncludePim: also copy the person's additional calendars and contact folders (recreated in the target by name; an existing one of the same name is reused). Tasks, notes and journal cannot be created through Graph and are never copied.</summary>
    public bool ExtraPim { get; init; } = true;
    /// <summary>Names of additional calendars or contact folders to leave out (for example a subscribed holiday calendar). Matched without regard to case; a contact folder takes its subfolders with it.</summary>
    public HashSet<string> LeaveOutPimNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Top-level well-known folders (inbox, sentitems, drafts, deleteditems, junkemail, archive, outbox) left out together with everything under them.</summary>
    public HashSet<string> ExcludeWellKnown { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Leave out the top-level folders the person created themselves (and everything under them).</summary>
    public bool ExcludeUserFolders { get; init; }
    /// <summary>Only mail created in the source mailbox on or after this moment (UTC). Normally that is when it arrived. Does not apply to contacts and calendar.</summary>
    public DateTime? CreatedOnOrAfter { get; init; }
    /// <summary>Skip mail items larger than this many bytes; 0 means no limit. Does not apply to contacts and calendar.</summary>
    public long MaxItemBytes { get; init; }
    /// <summary>Safety limit on the total number of items in the whole tree.</summary>
    public int MaxItems { get; init; } = 500;
    public bool Fresh { get; init; }
    public bool AllowExisting { get; init; }
    public bool AppOnly { get; init; }
    /// <summary>Selectable, off by default. Room and equipment mailboxes are not supported yet; setting this stops the run.</summary>
    public bool IncludeRooms { get; init; }
    /// <summary>Read-only survey: sign in to the SOURCE only, list the folder tree and item counts, change nothing, never touch the target.</summary>
    public bool ScanOnly { get; init; }
    /// <summary>Compare every copied message with its source, field by field, after the counts are reconciled.</summary>
    public bool Verify { get; init; } = true;
}

public sealed record TreeOutcome(int Folders, int SkippedFolders, int Listed, int AlreadyDone, int Imported, int Failed, int CountMismatches, VerifyOutcome? Verification = null, long Bytes = 0);

/// <summary>A folder found in the source tree. Path is its position (for example 3.2), not its name.</summary>
public sealed record FolderInfo(string Path, string WellKnown, int Depth, int ItemCount);
/// <summary>Where one folder stands. Status: Waiting, Copying, Done, Failed items, Mismatch.</summary>
public sealed record FolderProgress(string Path, int Imported, int AlreadyDone, int Failed, string Status);
public sealed record TreeTotals(int Total, int Imported, int AlreadyDone, int Failed, int Mismatches);

/// <summary>
/// Copies a source mailbox's whole mail folder tree into a new container folder in the target mailbox (same or different tenant),
/// using the import/export API. The source is only read. Nothing is merged into existing target folders.
/// Folder names are never written to logs: folders are identified by their position in the tree (for example 3.2) and, for system folders, their well-known name.
/// </summary>
public sealed class TreeCopier
{
    private const string G = GraphHttp.Graph;
    private static readonly HashSet<string> SkipWellKnown = new(StringComparer.OrdinalIgnoreCase)
    { "conflicts", "syncissues", "localfailures", "serverfailures", "searchfolders", "recoverableitemsroot", "recoverableitemsdeletions", "recoverableitemsversions", "recoverableitemspurges", "scheduled" };
    private static readonly string[] NonMailTypeHints = { "contact", "appointment", "calendar", "task", "stickynote", "journal" };

    private readonly CopyConfig _cfg; private readonly TreeOptions _opt; private readonly RunLog _log;
    public TreeCopier(CopyConfig cfg, TreeOptions opt, RunLog log) { _cfg = cfg; _opt = opt; _log = log; }

    // Progress for a UI. Raised from the copy's thread; a subscriber must not throw or block.
    public event Action<string>? PhaseChanged;
    public event Action<IReadOnlyList<FolderInfo>>? FoldersDiscovered;
    public event Action<FolderProgress>? FolderChanged;
    public event Action<TreeTotals>? TotalsChanged;
    private void Raise(Action a) { try { a(); } catch { /* a UI problem must never stop a copy */ } }

    private sealed record SrcFolder(string Id, string ParentId, string Name, string WellKnown, string Path, int ItemCount, List<AdminItem> Items, string Type = "");

    public async Task<TreeOutcome> RunAsync(CancellationToken ct = default)
    {
        if (_opt.IncludeRooms)
            throw new NotSupportedException("--include-rooms: room, equipment and shared mailboxes are not supported yet (needs app-only mode and calendar support). See the user guide, section 12.");
        if (_opt.MergeWellKnown == (_opt.TargetContainer != ""))
            throw new InvalidOperationException("Choose exactly one: a target container folder, or merge mode into the target's real folders.");
        if (_opt.PimOnly && !(_opt.IncludePim && _opt.MergeWellKnown))
            throw new InvalidOperationException("--pim-only needs --merge-wellknown and --include-pim.");
        if (_opt.IncludePim && !_opt.MergeWellKnown)
            throw new InvalidOperationException("--include-pim needs --merge-wellknown: contacts and calendar cannot be copied into a container folder; they go into the target's default Contacts and Calendar.");
        if (!_opt.ScanOnly && _cfg.IsProtected(_cfg.Target.Upn))
            throw new InvalidOperationException("The target account is listed in protectedMailboxes. It is a real mailbox and is refused as a copy target.");
        _log.Hide(_cfg.Source.Upn, _cfg.Target.Upn, _cfg.Source.ClientSecret, _cfg.Target.ClientSecret);
        var started = DateTime.Now;
        var http = new GraphHttp(_log);
        var src = new TenantSession(SessionRole.Source, _cfg.Source, _opt.AppOnly, _log);
        var tgt = new TenantSession(SessionRole.Target, _cfg.Target, _opt.AppOnly, _log);
        var ss = new SourceScope(); var ts = new TargetScope();
        Guards.ApplyTree(src, ss, tgt, ts);
        var resolver = new MailResolver(http, src, ss);

        Phase(1, "Sign in and verify the accounts", started);
        await VerifyIdentityAsync(http, src);
        if (!_opt.ScanOnly) await VerifyIdentityAsync(http, tgt);

        // ---- source: read-only discovery of the tree and its items ----
        Phase(2, "Source (read-only): walk the folder tree and list items", started);
        src.MailboxId = await ImportExportApi.GetMailboxIdAsync(http, src);
        var skipped = new List<string>();
        var folders = await EnumerateAsync(http, src, ss, skipped);
        if (_opt.PimOnly)
        {
            var mailFolders = folders.Count;
            var pimRoots = folders.Where(f => f.ParentId == "" && (string.Equals(f.WellKnown, "contacts", StringComparison.OrdinalIgnoreCase) || string.Equals(f.WellKnown, "calendar", StringComparison.OrdinalIgnoreCase))).Select(f => f.Path).ToList();
            folders = folders.Where(f => pimRoots.Any(r => f.Path == r || f.Path.StartsWith(r + "."))).ToList();
            _log.Info($"Calendar and contacts only: {folders.Count} of {mailFolders} folders kept; every mail folder is left out.");
        }
        var total = folders.Sum(f => f.ItemCount);
        _log.Info($"{folders.Count} folders to copy, {skipped.Count} skipped, {total} items in total (limit {_opt.MaxItems}).");
        foreach (var s in skipped) _log.Warn($"skipped folder {s}");
        _log.Info("Item types: " + string.Join(", ", folders.SelectMany(f => f.Items).GroupBy(i => i.Type).Select(g => $"{g.Key} x{g.Count()}")));
        if (!_opt.ScanOnly && total > _opt.MaxItems) throw new InvalidOperationException($"The tree has {total} items, more than the limit of {_opt.MaxItems}. Pass a larger --max if that is intended.");
        foreach (var i in folders.SelectMany(f => f.Items)) ss.AdminItemIds.Add(i.Id);
        Raise(() => FoldersDiscovered?.Invoke(folders.Select(f => new FolderInfo(f.Path, f.WellKnown, f.Path.Count(ch => ch == '.'), f.ItemCount)).ToList()));
        Raise(() => TotalsChanged?.Invoke(new TreeTotals(total, 0, 0, 0, 0)));
        if (_opt.ScanOnly)
        {
            var mb = folders.Sum(f => f.Items.Sum(i => i.Size)) / 1048576.0;
            _log.Info($"\nScan complete in {(DateTime.Now - started).TotalMinutes:0.0} min: {folders.Count} folders, {total} items, about {mb:0.0} MB. Nothing was changed and the target was not contacted.");
            return new TreeOutcome(folders.Count, skipped.Count, total, 0, 0, 0, 0, null, folders.Sum(f => f.Items.Sum(i => i.Size)));
        }

        var journal = new Journal(_cfg.Source.Upn, "*tree*", _cfg.Target.Upn, _opt.MergeWellKnown ? "*merge*" : _opt.TargetContainer, _opt.Fresh);
        if (journal.Count > 0) _log.Info($"Resuming: {journal.Count} items were already imported by an earlier run.");
        var freshRun = journal.Count == 0;     // the journal fills up during the run, so remember how it started

        // ---- target: the container folder (container mode) or the real well-known folders (merge mode) ----
        string rootMailId; string? rootAdminId;                                    // where top-level source folders go
        var wkTargets = new Dictionary<string, (string MailId, string AdminId)>(StringComparer.OrdinalIgnoreCase);   // well-known name -> real target folder
        var skippedIds = new HashSet<string>();                                    // merge mode: source folders left out (empty system folders)
        if (_opt.MergeWellKnown)
        {
            Phase(3, "Target: locate the real well-known folders (merge mode)", started);
            tgt.MailboxId = await ImportExportApi.GetMailboxIdAsync(http, tgt);
            rootMailId = (await GetWellKnownAsync(http, tgt, "msgfolderroot")).Id; rootAdminId = null;
            ts.Folders.Add(rootMailId);
            var tooFull = new List<string>();
            foreach (var f in folders.Where(x => x.ParentId == "" && x.WellKnown != ""))
            {
                var hasDescendants = folders.Any(x => x.Path.StartsWith(f.Path + "."));
                if (_opt.IncludePim && (string.Equals(f.WellKnown, "contacts", StringComparison.OrdinalIgnoreCase) || string.Equals(f.WellKnown, "calendar", StringComparison.OrdinalIgnoreCase)))
                {
                    // contacts and calendar are not mail folders: only the import/export-API id exists (no mail-API id, no dedupe by message id)
                    var pimAdmin = await FindAdminWellKnownAsync(http, tgt, f.WellKnown);
                    ts.AdminFolders.Add(pimAdmin);
                    wkTargets[f.WellKnown] = ("", pimAdmin);
                    var pimExisting = (await ImportExportApi.ListItemsAsync(http, tgt, pimAdmin)).Count;
                    _log.Info($"folder {f.Path} [{f.WellKnown}] merges into the target's default {f.WellKnown} folder, which holds {pimExisting} items now.");
                    if (freshRun && f.ItemCount > 0 && pimExisting > 0) tooFull.Add($"{f.WellKnown} ({pimExisting} items)");
                    continue;
                }
                if (!Guards.MergeableWellKnown.Contains(f.WellKnown, StringComparer.OrdinalIgnoreCase))
                {
                    // not mapped (conversation history, yammer...): skipped when nothing in its subtree has items, otherwise copied as an ordinary folder
                    var subtree = folders.Where(x => x.Path == f.Path || x.Path.StartsWith(f.Path + ".")).Sum(x => x.ItemCount);
                    if (subtree == 0) { skippedIds.Add(f.Id); _log.Info($"folder {f.Path} [{f.WellKnown}]: system folder with no items, left out."); }
                    else _log.Warn($"folder {f.Path} [{f.WellKnown}]: no matching real folder in the target; it is copied as an ordinary folder.");
                    continue;
                }
                if (f.ItemCount == 0 && !hasDescendants) { skippedIds.Add(f.Id); _log.Info($"folder {f.Path} [{f.WellKnown}]: empty, left out."); continue; }
                var (mid, existing) = await GetWellKnownAsync(http, tgt, f.WellKnown.ToLowerInvariant());
                ts.Folders.Add(mid);
                var aid = await FindAdminWellKnownAsync(http, tgt, f.WellKnown);
                ts.AdminFolders.Add(aid);
                wkTargets[f.WellKnown] = (mid, aid);
                _log.Info($"folder {f.Path} [{f.WellKnown}] merges into the target's real {f.WellKnown} folder, which holds {existing} items now.");
                if (freshRun && f.ItemCount > 0 && existing > 0) tooFull.Add($"{f.WellKnown} ({existing} items)");
            }
            if (tooFull.Count > 0 && !_opt.AllowNonEmpty)
                throw new InvalidOperationException($"These target folders already hold mail: {string.Join(", ", tooFull)}. Nothing was written. Pass --allow-nonempty to add the source's mail alongside it (existing mail is never changed or deleted).");
        }
        else
        {
            Phase(3, "Target: find or create the container folder", started);
            tgt.MailboxId = await ImportExportApi.GetMailboxIdAsync(http, tgt);
            var top = await http.GetAllAsync(tgt, $"{G}{tgt.Me}/mailFolders?$filter=displayName eq '{Uri.EscapeDataString(_opt.TargetContainer.Replace("'", "''"))}'&$select=id,displayName");
            if (top.Count > 1) throw new InvalidOperationException($"The target mailbox has {top.Count} top-level folders named '{_opt.TargetContainer}'. Pick a different name.");
            if (top.Count == 1)
            {
                if (journal.Count == 0 && !_opt.AllowExisting)
                    throw new InvalidOperationException($"The container folder '{_opt.TargetContainer}' already exists and no earlier run is recorded. Pass --allow-existing or choose another name.");
                rootMailId = top[0].GetProperty("id").GetString()!;
                _log.Info("Using the existing container folder.");
            }
            else
            {
                if (journal.Count > 0) throw new InvalidOperationException("An earlier run is recorded but its container folder is gone. Run again with --fresh.");
                var (cs, cj, cb) = await http.JsonAsync(tgt, HttpMethod.Post, $"{G}{tgt.Me}/mailFolders", new { displayName = _opt.TargetContainer });
                if (cs != 201) throw new InvalidOperationException($"Could not create the container folder ({cs}): {GraphHttp.Truncate(cb)}");
                rootMailId = cj.GetProperty("id").GetString()!;
                _log.Info("Created the container folder.");
            }
            ts.Folders.Add(rootMailId);
            rootAdminId = await FindAdminFolderAsync(http, tgt, null, _opt.TargetContainer);
            ts.AdminFolders.Add(rootAdminId);
        }

        // ---- recreate the tree (parents come before children) and copy ----
        Phase(4, "Recreate the tree and copy items (export from source, import into target)", started);
        var map = new Dictionary<string, (string MailId, string AdminId)>();      // source folder id -> target ids
        var mailKids = new Dictionary<string, Dictionary<string, string>>();       // target parent mail id -> name -> mail id
        var pimCreated = new Dictionary<string, string>();                         // source id of an additional calendar / contact folder -> the id Graph gave the target one
        ImportSession? session = null;
        int imported = 0, failed = 0, already = 0, mismatches = 0;
        foreach (var f in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (skippedIds.Contains(f.Id) || (f.ParentId != "" && skippedIds.Contains(f.ParentId))) { skippedIds.Add(f.Id); continue; }
            string mailId, adminId;
            if (_opt.MergeWellKnown && f.ParentId == "" && wkTargets.TryGetValue(f.WellKnown, out var real)) (mailId, adminId) = real;
            else if (_opt.IncludePim && PimKind(f.Type) != "")
            {
                // an additional calendar or contact folder: created as a real calendar / contact folder (not a mail folder) and found again by name through the import/export API
                try
                {
                    var kind = PimKind(f.Type);
                    string? parentAdmin = null, parentCreated = null;
                    if (f.ParentId != "" && map.TryGetValue(f.ParentId, out var pp)) { parentAdmin = pp.AdminId; pimCreated.TryGetValue(f.ParentId, out parentCreated); }
                    else if (wkTargets.TryGetValue(kind == "calendar" ? "calendar" : "contacts", out var dflt)) parentAdmin = dflt.AdminId;
                    else throw new InvalidOperationException($"The target's default {kind} folder was not located.");
                    var made = await EnsurePimFolderAsync(http, tgt, ts, kind, f.Name, parentCreated, parentAdmin);
                    adminId = made.AdminId; mailId = "";
                    pimCreated[f.Id] = made.CreatedId;
                    _log.Info($"folder {f.Path} [{kind}]: " + (made.Existed ? "an existing target folder of the same name is used." : "created in the target."));
                }
                catch (InvalidOperationException ex)
                {
                    _log.Error($"folder {f.Path}: could not create the matching target {PimKind(f.Type)} folder, so its {f.ItemCount} items are not copied. {ex.Message}");
                    failed += f.ItemCount; skippedIds.Add(f.Id);
                    Raise(() => FolderChanged?.Invoke(new FolderProgress(f.Path, 0, 0, f.ItemCount, "Not created")));
                    continue;
                }
            }
            else
            {
                string pMail; string? pAdmin;
                if (f.ParentId != "" && map.TryGetValue(f.ParentId, out var p)) (pMail, pAdmin) = (p.MailId, p.AdminId); else (pMail, pAdmin) = (rootMailId, rootAdminId);
                mailId = await EnsureMailChildAsync(http, tgt, ts, mailKids, pMail, f.Name);
                ts.Folders.Add(mailId);
                adminId = await FindAdminFolderAsync(http, tgt, pAdmin, f.Name);
                ts.AdminFolders.Add(adminId);
            }
            map[f.Id] = (mailId, adminId);
            if (_opt.MergeWellKnown && freshRun && !_opt.AllowNonEmpty && f.ItemCount > 0 && !wkTargets.ContainsValue(map[f.Id]))
            {
                var existingCount = (await ImportExportApi.ListItemsAsync(http, tgt, adminId)).Count;
                if (existingCount > 0) throw new InvalidOperationException($"Target folder for source folder {f.Path} already holds {existingCount} items. Stopped before importing into it. Pass --allow-nonempty to add alongside existing mail.");
            }

            var pending = f.Items.Where(i => !journal.IsDone(i.Id)).ToList();
            foreach (var i in f.Items) if (journal.IsDone(i.Id)) _log.Item(f.Path, i.Id, "skipped", "copied by an earlier run");
            // Dedupe: when the target folder already holds mail, skip source items whose internet message id is already there.
            // (An import/export-API item id equals its mail-API message id, which is how the source item's message id is found.)
            if (_opt.Dedupe && pending.Count > 0 && mailId != "")
            {
                var (tst, tj, _) = await http.JsonAsync(tgt, HttpMethod.Get, $"{G}{tgt.Me}/mailFolders/{mailId}?$select=id,totalItemCount");
                var targetHas = tst == 200 && tj.TryGetProperty("totalItemCount", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : -1;
                if (targetHas != 0)   // -1 means unknown: check to be safe
                {
                    var srcMailId = await resolver.ResolveAsync(f);
                    if (srcMailId == null) _log.Warn($"folder {f.Path}: the target folder holds mail but the source folder could not be located through the mail API, so duplicates cannot be checked.");
                    else
                    {
                        var have = new HashSet<string>();
                        foreach (var m in await http.GetAllAsync(tgt, $"{G}{tgt.Me}/mailFolders/{mailId}/messages?$select=id,internetMessageId&$top=100"))
                        { var x = Str(m, "internetMessageId"); if (x != "") have.Add(x); }
                        var dup = new HashSet<string>();
                        foreach (var m in await http.GetAllAsync(src, $"{G}{src.Me}/mailFolders/{srcMailId}/messages?$select=id,internetMessageId&$top=100"))
                        { var id = Str(m, "id"); ss.MessageIds.Add(id); var x = Str(m, "internetMessageId"); if (x != "" && have.Contains(x)) dup.Add(id); }
                        var already1 = pending.Where(i => dup.Contains(i.Id)).ToList();
                        foreach (var d in already1) { journal.MarkDone(d.Id); _log.Item(f.Path, d.Id, "skipped", "already in the target (same internet message id)"); }
                        pending = pending.Where(i => !dup.Contains(i.Id)).ToList();
                        if (already1.Count > 0) _log.Info($"folder {f.Path}: {already1.Count} items already exist in the target (same internet message id) and are skipped.");
                    }
                }
            }
            already += f.Items.Count - pending.Count;
            int fImported = 0, fFailed = 0, fAlready = f.Items.Count - pending.Count;
            void Report(string status)
            {
                Raise(() => FolderChanged?.Invoke(new FolderProgress(f.Path, fImported, fAlready, fFailed, status)));
                Raise(() => TotalsChanged?.Invoke(new TreeTotals(total, imported + fImported, already, failed + fFailed, mismatches)));
            }
            Report("Copying");
            for (var off = 0; off < pending.Count; off += 20)
            {
                var batch = pending.Skip(off).Take(20).Select(i => i.Id).ToList();
                if (batch.Any(id => !ss.AdminItemIds.Has(id))) throw new InvalidOperationException("Refusing to export an item that was not listed from the source tree.");
                var exported = await ImportExportApi.ExportAsync(http, src, batch, _log);
                if (exported.Count < batch.Count) _log.Error($"folder {f.Path}: {batch.Count - exported.Count} of {batch.Count} items in this batch could not be exported (ids are in the lines above)");
                fFailed += batch.Count - exported.Count;
                var exportedIds = exported.Select(x => x.Id).ToHashSet();
                foreach (var lost in batch.Where(id => !exportedIds.Contains(id))) _log.Item(f.Path, lost, "failed", "could not be exported from the source");
                session ??= await ImportExportApi.CreateSessionAsync(http, tgt);
                foreach (var item in exported)
                {
                    ct.ThrowIfCancellationRequested();
                    if (session.NearlyExpired) session = await ImportExportApi.CreateSessionAsync(http, tgt);
                    var (ok, detail) = await ImportExportApi.ImportAsync(http, session, adminId, item.Data);
                    if (ok)
                    {
                        fImported++; journal.MarkDone(item.Id);
                        _log.Debug($"imported source item {item.Id} into folder {f.Path}");
                        _log.Item(f.Path, item.Id, "copied");
                        if (fImported % 5 == 0) Report("Copying");
                        if ((imported + fImported) % 25 == 0) _log.Info($"  progress: {imported + fImported} imported, {failed + fFailed} failed, in folder {f.Path} ({(DateTime.Now - started).TotalMinutes:0.0} min elapsed)");
                    }
                    else { fFailed++; _log.Error($"IMPORT FAILED in folder {f.Path}, source item id {item.Id}: {detail}"); _log.Item(f.Path, item.Id, "failed", $"import: {detail}"); }
                }
            }
            Report(fFailed > 0 ? $"{fFailed} failed" : "Done");
            imported += fImported; failed += fFailed;
            var line = $"  folder {f.Path} [{(f.WellKnown == "" ? "user" : f.WellKnown)}]: {f.ItemCount} items, {fImported} imported, {f.Items.Count - pending.Count} already done, {fFailed} failed   (running total {imported + failed}/{total - already})";
            if (fFailed > 0) _log.Warn(line); else _log.Info(line);
        }

        // ---- reconcile counts ----
        Phase(5, "Reconcile: compare item counts per folder", started);
        foreach (var f in folders.Where(x => map.ContainsKey(x.Id)))
        {
            var targetCount = (await ImportExportApi.ListItemsAsync(http, tgt, map[f.Id].AdminId)).Count;
            // merge mode can land in folders that already held mail, so the target may hold more than the source, never fewer
            if (_opt.MergeWellKnown && targetCount > f.ItemCount) _log.Info($"folder {f.Path}: the target holds {targetCount - f.ItemCount} more items than the source (mail that was already there).");
            if (_opt.MergeWellKnown ? targetCount < f.ItemCount : targetCount != f.ItemCount) { mismatches++; _log.Error($"MISMATCH folder {f.Path}: source {f.ItemCount}, target {targetCount}"); Raise(() => FolderChanged?.Invoke(new FolderProgress(f.Path, targetCount, 0, 0, "Mismatch"))); }
        }
        _log.Info(mismatches == 0 ? (_opt.MergeWellKnown ? "Every folder holds at least its source's item count." : "Every folder's item count matches.") : $"{mismatches} folders do not match.");

        // ---- field-by-field verification ----
        VerifyOutcome? verification = null;
        if (_opt.Verify)
        {
            Phase(6, "Verify: compare every copied message with its source, field by field", started);
            verification = await VerifyTreeAsync(http, src, tgt, ss, ts, folders, map, resolver);
        }
        if (resolver.ById + resolver.ByName > 0) _log.Info($"Source mail-API folders located: {resolver.ById} by the same id as the import/export API, {resolver.ByName} by name.");
        _log.Info($"\nDone in {(DateTime.Now - started).TotalMinutes:0.0} min. Imported {imported}, failed {failed}. Throttle responses: {http.Throttles}. The source mailbox was only read.");
        return new TreeOutcome(folders.Count, skipped.Count, total, already, imported, failed, mismatches, verification, folders.Sum(f => f.Items.Sum(i => i.Size)));
    }

    // ---- source mail-API folders (verification and dedupe) ----

    /// <summary>
    /// Finds the mail-API folder for a source tree folder. Import/export-API item ids turned out to equal mail-API message ids, so the folder id is tried
    /// first; if the mail API does not know it, the folder is found by its name chain. Read-only.
    /// </summary>
    private sealed class MailResolver
    {
        private readonly GraphHttp _http; private readonly TenantSession _src; private readonly SourceScope _ss;
        private readonly Dictionary<string, string?> _byAdminId = new();
        private readonly Dictionary<string, List<JsonElement>> _children = new();
        private List<JsonElement>? _top;
        public int ById { get; private set; }
        public int ByName { get; private set; }
        public MailResolver(GraphHttp http, TenantSession src, SourceScope ss) { _http = http; _src = src; _ss = ss; }

        public async Task<string?> ResolveAsync(SrcFolder f)
        {
            if (_byAdminId.TryGetValue(f.Id, out var cached)) return cached;
            string? result = null;
            _ss.MailFolders.Add(f.Id);   // the id came from the source tree walk
            var (st, j, _) = await _http.JsonAsync(_src, HttpMethod.Get, $"{G}{_src.Me}/mailFolders/{f.Id}?$select=id,displayName");
            if (st == 200 && Str(j, "id") == f.Id) { result = f.Id; ById++; }
            else
            {
                List<JsonElement>? candidates = null;
                if (f.ParentId == "") candidates = _top ??= await _http.GetAllAsync(_src, $"{G}{_src.Me}/mailFolders?$top=100&$select=id,displayName");
                else if (_byAdminId.TryGetValue(f.ParentId, out var parentMail) && parentMail != null)
                {
                    if (!_children.TryGetValue(parentMail, out candidates))
                    {
                        candidates = await _http.GetAllAsync(_src, $"{G}{_src.Me}/mailFolders/{parentMail}/childFolders?$top=100&$select=id,displayName");
                        _children[parentMail] = candidates;
                    }
                }
                var match = candidates?.Where(c => string.Equals(Str(c, "displayName"), f.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<JsonElement>();
                if (match.Count == 1) { result = Str(match[0], "id"); ByName++; }
            }
            if (result != null) _ss.MailFolders.Add(result);
            _byAdminId[f.Id] = result;
            return result;
        }
    }

    // ---- verification ----

    /// <summary>
    /// Pairs each source message with its copy (same folder position, same internet message id) and compares them field by field.
    /// Source folders are located through the mail API by their name chain; an empty folder that cannot be located is not an error.
    /// </summary>
    private async Task<VerifyOutcome> VerifyTreeAsync(GraphHttp http, TenantSession src, TenantSession tgt, SourceScope ss, TargetScope ts,
        List<SrcFolder> folders, Dictionary<string, (string MailId, string AdminId)> map, MailResolver resolver)
    {
        var pairs = new List<(string Key, string SrcId, string DstId)>(); var unpaired = 0;

        var pimFolders = folders.Where(x => map.TryGetValue(x.Id, out var m) && m.MailId == "").ToList();
        if (pimFolders.Count > 0) _log.Info($"{pimFolders.Count} contacts/calendar folders ({pimFolders.Sum(x => x.ItemCount)} items) are counted and reconciled but not yet compared field by field.");
        foreach (var f in folders.Where(x => map.TryGetValue(x.Id, out var m) && m.MailId != ""))
        {
            var srcMailId = await resolver.ResolveAsync(f);
            if (srcMailId == null)
            {
                if (f.ItemCount > 0) { unpaired += f.ItemCount; _log.Error($"VERIFY: could not locate source folder {f.Path} through the mail API; its {f.ItemCount} items were not compared."); }
                else _log.Debug($"verify: empty folder {f.Path} not located; nothing to compare");
                continue;
            }
            if (f.ItemCount == 0) continue;

            var srcByMsgId = new Dictionary<string, string>();
            foreach (var m in await http.GetAllAsync(src, $"{G}{src.Me}/mailFolders/{srcMailId}/messages?$select=id,internetMessageId&$top=100"))
            {
                var id = Str(m, "id"); ss.MessageIds.Add(id);
                var imid = Str(m, "internetMessageId"); if (imid != "") srcByMsgId.TryAdd(imid, id);
            }
            var n = 0; var extras = 0;
            foreach (var m in await http.GetAllAsync(tgt, $"{G}{tgt.Me}/mailFolders/{map[f.Id].MailId}/messages?$select=id,internetMessageId&$top=100"))
            {
                var id = Str(m, "id"); ts.Messages.Add(id);
                var imid = Str(m, "internetMessageId");
                if (imid != "" && srcByMsgId.TryGetValue(imid, out var sid)) pairs.Add(($"folder {f.Path} item {++n}", sid, id));
                else extras++;
            }
            // Every source item must have a copy. Extra target messages are an error in container mode (the folder is ours alone)
            // and expected in merge mode (mail that was already in the real folder).
            var missing = Math.Max(0, f.ItemCount - n);
            if (missing > 0) { unpaired += missing; _log.Error($"VERIFY: folder {f.Path}: {missing} source messages have no matching copy in the target."); }
            if (extras > 0)
            {
                if (_opt.MergeWellKnown) _log.Info($"verify: folder {f.Path} also holds {extras} target messages that are not from the source (already there); not compared.");
                else { unpaired += extras; _log.Warn($"VERIFY: folder {f.Path}: {extras} target messages could not be paired with a source message."); }
            }
        }
        _log.Info($"{pairs.Count} messages paired across {folders.Count(f => f.ItemCount > 0)} folders; {unpaired} could not be paired.");
        return await new FidelityVerifier(http, src, tgt, _log).CompareAsync(pairs, unpaired);
    }

    // ---- source tree ----

    private async Task<List<SrcFolder>> EnumerateAsync(GraphHttp http, TenantSession s, SourceScope ss, List<string> skipped)
    {
        int byDate = 0, bySize = 0;
        var result = new List<SrcFolder>(); var seen = new HashSet<string>(); var types = new SortedSet<string>(); var pimKeys = new SortedSet<string>();
        var rootUrl = $"{G}/admin/exchange/mailboxes/{s.MailboxId}/folders?$top=100";
        var topLevel = await http.GetAllAsync(s, rootUrl);
        // If the top-level listing also returns nested folders, leave those to the child walk so parents come first.
        var topIds = topLevel.Select(f => Str(f, "id")).ToHashSet();
        var roots = topLevel.Where(f => !topIds.Contains(Str(f, "parentFolderId"))).ToList();
        _log.Info($"Top-level listing returned {topLevel.Count} folders ({roots.Count} at the top).");

        async Task WalkAsync(List<JsonElement> level, string parentId, string parentPath, string label)
        {
            var n = 0;
            foreach (var f in level)
            {
                var id = Str(f, "id"); if (id == "" || !seen.Add(id)) continue;
                var path = parentPath == "" ? $"{++n}" : $"{parentPath}.{++n}";
                var wk = Str(f, "wellKnownName"); var type = Str(f, "type");
                if (type != "") types.Add(type);
                if (wk != "" && SkipWellKnown.Contains(wk)) { skipped.Add($"{path} ({wk}): system folder"); continue; }
                var defaultPim = _opt.IncludePim && parentPath == "" && (string.Equals(wk, "contacts", StringComparison.OrdinalIgnoreCase) || string.Equals(wk, "calendar", StringComparison.OrdinalIgnoreCase));
                if (parentPath == "" && !defaultPim && ((wk != "" && _opt.ExcludeWellKnown.Contains(wk)) || (wk == "" && _opt.ExcludeUserFolders)))
                { skipped.Add($"{path} ({(wk == "" ? "user" : wk)}): left out by your folder choice"); continue; }
                var extraPim = _opt.IncludePim && _opt.ExtraPim && !defaultPim && PimKind(type) != "";   // an additional calendar or contact folder
                if (extraPim && _opt.LeaveOutPimNames.Contains(Str(f, "displayName")))
                { skipped.Add($"{path} ({PimKind(type)}): left out by your list of calendars and contact folders to leave out"); continue; }
                if (!defaultPim && !extraPim && type != "" && NonMailTypeHints.Any(h => type.Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    // Not copied yet (M3), but listed read-only so a scan shows what is there: item counts and types, never content.
                    ss.TreeFolders.Add(id);
                    string what;
                    try
                    {
                        var other = await ImportExportApi.ListItemsAsync(http, s, id);
                        what = $"{other.Count} items" + (other.Count == 0 ? "" : " (" + string.Join(", ", other.GroupBy(i => i.Type).Select(g => $"{g.Key} x{g.Count()}")) + ")");
                        foreach (var k in other.Take(1)) pimKeys.Add(k.Keys);
                    }
                    catch (Exception ex) { what = $"could not be listed ({ex.GetType().Name})"; }
                    skipped.Add($"{path} ({(wk == "" ? "user" : wk)}): type '{type}' is not copied ({(type.Contains(".Birthday", StringComparison.OrdinalIgnoreCase) ? "Outlook generates it from contact birthdays" : "tasks, notes and journal cannot be created in the target"+(_opt.IncludePim ? "" : "; calendars and contacts need --include-pim")) }); {what}");
                    continue;
                }

                ss.TreeFolders.Add(id);
                var items = await ImportExportApi.ListItemsAsync(http, s, id);
                if (defaultPim || extraPim) _log.Info($"folder {path} [{type}{(defaultPim ? ", default" : ", additional")}]: {items.Count} items");   // position, type and count only; never the name
                if (!defaultPim && !extraPim) items = ApplyItemFilters(items, ref byDate, ref bySize);
                result.Add(new SrcFolder(id, parentId, Str(f, "displayName"), wk, path, items.Count, items, type));

                var hasCount = f.TryGetProperty("childFolderCount", out var cc) && cc.ValueKind == JsonValueKind.Number;
                if (hasCount && cc.GetInt32() == 0) continue;
                var kids = await http.GetAllAsync(s, $"{G}/admin/exchange/mailboxes/{s.MailboxId}/folders/{id}/childFolders?$top=100");
                await WalkAsync(kids, id, path, label);
            }
        }
        await WalkAsync(roots, "", "", "");
        if (byDate > 0 || bySize > 0) _log.Info($"Your filters left out {byDate} items by date and {bySize} by size.");
        _log.Info("Folder type values seen: " + (types.Count == 0 ? "(none reported)" : string.Join(", ", types)));
        // discovery: what the import/export API reports about an item (property names only), so features can rely on facts
        var mailKeys = result.SelectMany(f => f.Items).Select(i => i.Keys).Distinct().ToList();
        _log.Info("Item properties the API reports (mail): " + (mailKeys.Count == 0 ? "(no items)" : string.Join(" | ", mailKeys)));
        if (pimKeys.Count > 0) _log.Info("Item properties the API reports (contacts/calendar/other): " + string.Join(" | ", pimKeys));
        return result;
    }

    /// <summary>Applies the date and size filters. An item whose date cannot be read is kept, never dropped silently.</summary>
    private List<AdminItem> ApplyItemFilters(List<AdminItem> items, ref int byDate, ref int bySize)
    {
        if (_opt.CreatedOnOrAfter == null && _opt.MaxItemBytes <= 0) return items;
        var kept = new List<AdminItem>(items.Count);
        foreach (var i in items)
        {
            if (_opt.CreatedOnOrAfter is { } since && System.DateTimeOffset.TryParse(i.Created, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var made) && made.UtcDateTime < since)
            { byDate++; continue; }
            if (_opt.MaxItemBytes > 0 && i.Size > _opt.MaxItemBytes) { bySize++; continue; }
            kept.Add(i);
        }
        return kept;
    }

    // ---- target folders ----

    /// <summary>A real, well-known target folder by its Graph name (inbox, sentitems, msgfolderroot...): its mail-API id and current item count.</summary>
    private static async Task<(string Id, int Count)> GetWellKnownAsync(GraphHttp http, TenantSession t, string name)
    {
        var (status, json, body) = await http.JsonAsync(t, HttpMethod.Get, $"{G}{t.Me}/mailFolders/{name}?$select=id,totalItemCount");
        if (status != 200) throw new InvalidOperationException($"Could not locate the target's {name} folder ({status}): {GraphHttp.Truncate(body, 200)}");
        return (Str(json, "id"), json.TryGetProperty("totalItemCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0);
    }

    /// <summary>The import/export-API id of a top-level well-known folder, found by its wellKnownName.</summary>
    /// <summary>"calendar" or "contact" for an import/export folder type such as IPF.Appointment or IPF.Contact; empty for anything else (mail, tasks, notes, journal).</summary>
    private static string PimKind(string type) =>
        type.Contains(".Birthday", StringComparison.OrdinalIgnoreCase) ? ""   // Outlook builds the Birthdays calendar from contact birthdays; copying it would create a duplicate
        : type.Contains("Appointment", StringComparison.OrdinalIgnoreCase) ? "calendar" : type.Contains("Contact", StringComparison.OrdinalIgnoreCase) ? "contact" : "";

    /// <summary>
    /// Finds or creates an additional calendar (a Graph calendar) or contact folder in the target, then finds it through the import/export API.
    /// A same-named one that already exists is reused, so a re-run never creates a duplicate. Needs the Calendars.ReadWrite / Contacts.ReadWrite permission.
    /// </summary>
    private static async Task<(string AdminId, string CreatedId, bool Existed)> EnsurePimFolderAsync(GraphHttp http, TenantSession t, TargetScope ts, string kind, string name, string? parentCreatedId, string? parentAdminId)
    {
        string listUrl, createUrl, nameProp;
        if (kind == "calendar") { listUrl = $"{G}{t.Me}/calendars?$top=100&$select=id,name"; createUrl = $"{G}{t.Me}/calendars"; nameProp = "name"; }
        else if (parentCreatedId == null) { listUrl = $"{G}{t.Me}/contactFolders?$top=100&$select=id,displayName"; createUrl = $"{G}{t.Me}/contactFolders"; nameProp = "displayName"; }
        else { listUrl = $"{G}{t.Me}/contactFolders/{parentCreatedId}/childFolders?$top=100&$select=id,displayName"; createUrl = $"{G}{t.Me}/contactFolders/{parentCreatedId}/childFolders"; nameProp = "displayName"; }

        var found = (await http.GetAllAsync(t, listUrl)).Where(x => string.Equals(Str(x, nameProp), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count > 1) throw new InvalidOperationException("More than one target folder has this name; cannot tell which to use.");
        string id; var existed = found.Count == 1;
        if (existed) id = Str(found[0], "id");
        else
        {
            var (st, json, body) = await http.JsonAsync(t, HttpMethod.Post, createUrl, kind == "calendar" ? new { name } : new { displayName = name });
            if (st is not (200 or 201))
                throw new InvalidOperationException($"HTTP {st}" + (st == 403 ? $": the target app needs the {(kind == "calendar" ? "Calendars.ReadWrite" : "Contacts.ReadWrite")} application permission, with admin consent." : $": {GraphHttp.Truncate(body, 200)}"));
            id = Str(json, "id");
        }
        ts.Folders.Add(id);
        var adminId = await FindAdminFolderAsync(http, t, parentAdminId, name);
        ts.AdminFolders.Add(adminId);
        return (adminId, id, existed);
    }

    private static async Task<string> FindAdminWellKnownAsync(GraphHttp http, TenantSession t, string wellKnown)
    {
        var all = await http.GetAllAsync(t, $"{G}/admin/exchange/mailboxes/{t.MailboxId}/folders?$top=100");
        var match = all.Where(f => string.Equals(Str(f, "wellKnownName"), wellKnown, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count != 1) throw new InvalidOperationException($"The import/export API shows {match.Count} target folders with the well-known name '{wellKnown}' (need exactly 1).");
        return Str(match[0], "id");
    }

    private static async Task<string> EnsureMailChildAsync(GraphHttp http, TenantSession t, TargetScope ts, Dictionary<string, Dictionary<string, string>> cache, string parentMailId, string name)
    {
        if (!cache.TryGetValue(parentMailId, out var kids))
        {
            kids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in await http.GetAllAsync(t, $"{G}{t.Me}/mailFolders/{parentMailId}/childFolders?$top=100&$select=id,displayName"))
                kids[Str(k, "displayName")] = Str(k, "id");
            cache[parentMailId] = kids;
        }
        if (kids.TryGetValue(name, out var existing)) return existing;
        var (status, json, body) = await http.JsonAsync(t, HttpMethod.Post, $"{G}{t.Me}/mailFolders/{parentMailId}/childFolders", new { displayName = name });
        if (status != 201) throw new InvalidOperationException($"Could not create a target folder ({status}): {GraphHttp.Truncate(body, 200)}");
        var id = Str(json, "id"); kids[name] = id; return id;
    }

    /// <summary>Finds a folder's import/export-API id by name under a parent (null = top level). Retries, because new folders can take a moment to appear.</summary>
    private static async Task<string> FindAdminFolderAsync(GraphHttp http, TenantSession t, string? parentAdminId, string name)
    {
        var url = parentAdminId == null
            ? $"{G}/admin/exchange/mailboxes/{t.MailboxId}/folders?$top=100"
            : $"{G}/admin/exchange/mailboxes/{t.MailboxId}/folders/{parentAdminId}/childFolders?$top=100";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var match = (await http.GetAllAsync(t, url)).Where(f => string.Equals(Str(f, "displayName"), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 1) return Str(match[0], "id");
            if (match.Count > 1) throw new InvalidOperationException("More than one target folder has the same name under one parent. Stopping.");
            await Task.Delay(2000);
        }
        throw new InvalidOperationException("A target folder is not visible through the import/export API yet. Try resuming in a minute.");
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void Phase(int n, string name, DateTime started)
    {
        _log.Info($"\n=== [{n}/6] {name}   (elapsed {(DateTime.Now - started).TotalMinutes:0.0} min)");
        Raise(() => PhaseChanged?.Invoke($"[{n}/6] {name}"));
    }

    private static async Task VerifyIdentityAsync(GraphHttp http, TenantSession s)
    {
        var (status, json, _) = await http.JsonAsync(s, HttpMethod.Get, $"{G}{s.Me}?$select=userPrincipalName");
        if (status != 200) throw new InvalidOperationException($"The {s.Role} sign-in check failed ({status}).");
        if (!string.Equals(json.GetProperty("userPrincipalName").GetString(), s.Upn, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The {s.Role} is signed in as a different account than configured. Stopping.");
    }
}
