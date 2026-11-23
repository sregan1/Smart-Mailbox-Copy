namespace SmartMailboxCopy;

public sealed class CopyOptions
{
    public required string SourceFolder { get; init; }
    public required string TargetFolder { get; init; }
    /// <summary>Safety limit: refuse a source folder with more items than this.</summary>
    public int MaxItems { get; init; } = 100;
    public bool Verify { get; init; } = true;
    /// <summary>Ignore any earlier journal and start over.</summary>
    public bool Fresh { get; init; }
    /// <summary>Allow importing into a target folder that already exists and was not created by an earlier run of this copy.</summary>
    public bool AllowExisting { get; init; }
    public bool AppOnly { get; init; }
}

public sealed record CopyOutcome(int Listed, int AlreadyDone, int Imported, int Failed, VerifyOutcome? Verification);

/// <summary>
/// Copies one top-level mail folder from a source mailbox to a new folder in a target mailbox (same or different tenant)
/// using Microsoft Graph's mailbox import/export API. The source is only ever read.
/// </summary>
public sealed class FolderCopier
{
    private const string G = GraphHttp.Graph;
    private readonly CopyConfig _cfg;
    private readonly CopyOptions _opt;
    private readonly RunLog _log;

    public FolderCopier(CopyConfig cfg, CopyOptions opt, RunLog log) { _cfg = cfg; _opt = opt; _log = log; }

    public async Task<CopyOutcome> RunAsync(CancellationToken ct = default)
    {
        _log.Hide(_cfg.Source.Upn, _cfg.Target.Upn, _cfg.Source.ClientSecret, _cfg.Target.ClientSecret);
        var started = DateTime.Now;
        var http = new GraphHttp(_log);
        var src = new TenantSession(SessionRole.Source, _cfg.Source, _opt.AppOnly, _log);
        var tgt = new TenantSession(SessionRole.Target, _cfg.Target, _opt.AppOnly, _log);
        var ss = new SourceScope(); var ts = new TargetScope();
        Guards.Apply(src, ss, tgt, ts);

        Phase(1, "Sign in and verify the accounts", started);
        await VerifyIdentityAsync(http, src);
        await VerifyIdentityAsync(http, tgt);

        // ---- source: read-only discovery ----
        Phase(2, "Source (read-only): find the folder and list its items", started);
        src.MailboxId = await ImportExportApi.GetMailboxIdAsync(http, src);
        var lookup = await http.GetAllAsync(src, $"{G}{src.Me}/mailFolders?$filter=displayName eq '{Uri.EscapeDataString(_opt.SourceFolder.Replace("'", "''"))}'&$select=id,displayName,totalItemCount");
        if (lookup.Count != 1) throw new InvalidOperationException($"Expected exactly one top-level mail folder named '{_opt.SourceFolder}' in the source mailbox, found {lookup.Count}.");
        ss.MailFolderId = lookup[0].GetProperty("id").GetString()!;
        var total = lookup[0].GetProperty("totalItemCount").GetInt32();
        _log.Info($"Source folder found with {total} items (limit {_opt.MaxItems}).");
        if (total == 0) { _log.Info("The source folder is empty. Nothing to do."); return new CopyOutcome(0, 0, 0, 0, null); }
        if (total > _opt.MaxItems) throw new InvalidOperationException($"The source folder has {total} items, more than the limit of {_opt.MaxItems}. Pass a larger --max if that is intended.");

        var adminFolders = (await ImportExportApi.ListFoldersAsync(http, src)).Where(f => f.GetProperty("displayName").GetString() == _opt.SourceFolder).ToList();
        if (adminFolders.Count != 1) throw new InvalidOperationException($"The import/export API shows {adminFolders.Count} folders named '{_opt.SourceFolder}' (need exactly 1).");
        ss.AdminFolderId = adminFolders[0].GetProperty("id").GetString()!;
        var items = await ImportExportApi.ListItemsAsync(http, src, ss.AdminFolderId);
        foreach (var i in items) ss.AdminItemIds.Add(i.Id);
        _log.Info($"{items.Count} items listed ({items.Sum(i => i.Size) / 1024} KB); types: {string.Join(", ", items.GroupBy(i => i.Type).Select(g => $"{g.Key} x{g.Count()}"))}.");

        // messages as seen by the mail API, only used to pair copies with sources when verifying
        var sourceByMessageId = new Dictionary<string, string>();
        foreach (var m in await http.GetAllAsync(src, $"{G}{src.Me}/mailFolders/{ss.MailFolderId}/messages?$select=id,internetMessageId&$top=100"))
        {
            var id = m.GetProperty("id").GetString()!; ss.MessageIds.Add(id);
            var imid = m.TryGetProperty("internetMessageId", out var x) ? x.GetString() ?? "" : "";
            if (imid != "") sourceByMessageId.TryAdd(imid, id);
        }

        // ---- resume bookkeeping ----
        var journal = new Journal(_cfg.Source.Upn, _opt.SourceFolder, _cfg.Target.Upn, _opt.TargetFolder, _opt.Fresh);
        var pending = items.Where(i => !journal.IsDone(i.Id)).ToList();
        if (journal.Count > 0) _log.Info($"Resuming: {journal.Count} items were already imported by an earlier run, {pending.Count} remain.");

        // ---- target folder ----
        Phase(3, "Target: find or create the destination folder", started);
        tgt.MailboxId = await ImportExportApi.GetMailboxIdAsync(http, tgt);
        var existing = await http.GetAllAsync(tgt, $"{G}{tgt.Me}/mailFolders?$filter=displayName eq '{Uri.EscapeDataString(_opt.TargetFolder.Replace("'", "''"))}'&$select=id,displayName");
        string targetFolderId;
        if (existing.Count > 1) throw new InvalidOperationException($"The target mailbox has {existing.Count} top-level folders named '{_opt.TargetFolder}'. Pick a different name.");
        if (existing.Count == 1)
        {
            if (journal.Count == 0 && !_opt.AllowExisting)
                throw new InvalidOperationException($"The target folder '{_opt.TargetFolder}' already exists and no earlier run of this copy is recorded. Pass --allow-existing to import into it, or choose another name.");
            targetFolderId = existing[0].GetProperty("id").GetString()!;
            _log.Info("Using the existing destination folder.");
        }
        else
        {
            if (journal.Count > 0) throw new InvalidOperationException("An earlier run is recorded but its destination folder is gone. Run again with --fresh.");
            var (cs, cj, cb) = await http.JsonAsync(tgt, HttpMethod.Post, $"{G}{tgt.Me}/mailFolders", new { displayName = _opt.TargetFolder });
            if (cs != 201) throw new InvalidOperationException($"Could not create the destination folder ({cs}): {GraphHttp.Truncate(cb)}");
            targetFolderId = cj.GetProperty("id").GetString()!;
            _log.Info("Created the destination folder.");
        }
        ts.Folders.Add(targetFolderId);
        var tgtAdmin = (await ImportExportApi.ListFoldersAsync(http, tgt)).Where(f => f.GetProperty("displayName").GetString() == _opt.TargetFolder).ToList();
        if (tgtAdmin.Count != 1) throw new InvalidOperationException($"The destination folder is not visible through the import/export API yet (found {tgtAdmin.Count}).");
        var targetAdminFolderId = tgtAdmin[0].GetProperty("id").GetString()!;

        // ---- export (read-only) and import ----
        int imported = 0, failed = 0;
        if (pending.Count > 0)
        {
            Phase(4, "Export from the source (read-only) and import into the target", started);
            var ids = pending.Select(i => i.Id).ToList();
            if (ids.Any(id => !ss.AdminItemIds.Has(id))) throw new InvalidOperationException("Refusing to export an item that was not listed from the designated folder.");
            var exported = await ImportExportApi.ExportAsync(http, src, ids, _log);

            var session = await ImportExportApi.CreateSessionAsync(http, tgt);
            _log.Info($"Import session created (valid about {(session.ExpiresUtc - DateTime.UtcNow).TotalMinutes:0} min).");
            foreach (var item in exported)
            {
                ct.ThrowIfCancellationRequested();
                if (session.NearlyExpired) session = await ImportExportApi.CreateSessionAsync(http, tgt);
                var (ok, detail) = await ImportExportApi.ImportAsync(http, session, targetAdminFolderId, item.Data);
                if (ok) { imported++; journal.MarkDone(item.Id); }
                else { failed++; _log.Error($"IMPORT FAILED for source item id {item.Id}: {detail}"); }
                if ((imported + failed) % 10 == 0) _log.Info($"  imported {imported}/{exported.Count} (failed {failed})");
            }
            failed += pending.Count - exported.Count;   // items that could not even be exported
            _log.Info($"Import done: {imported} ok, {failed} failed.");
        }

        // ---- verify ----
        VerifyOutcome? verification = null;
        if (_opt.Verify)
        {
            Phase(5, "Verify: compare every copy with its source", started);
            var copies = new List<(string MessageId, string TargetId)>();
            foreach (var m in await http.GetAllAsync(tgt, $"{G}{tgt.Me}/mailFolders/{targetFolderId}/messages?$select=id,internetMessageId&$top=100"))
            {
                var id = m.GetProperty("id").GetString()!; ts.Messages.Add(id);
                copies.Add((m.TryGetProperty("internetMessageId", out var x) ? x.GetString() ?? "" : "", id));
            }
            verification = await new FidelityVerifier(http, src, tgt, _log).VerifyAsync(sourceByMessageId, copies);
        }

        _log.Info($"\nDone in {(DateTime.Now - started).TotalMinutes:0.0} min. Throttle responses: {http.Throttles}. The source mailbox was only read.");
        return new CopyOutcome(items.Count, items.Count - pending.Count, imported, failed, verification);
    }

    private void Phase(int n, string name, DateTime started) =>
        _log.Info($"\n=== [{n}/5] {name}   (elapsed {(DateTime.Now - started).TotalMinutes:0.0} min)");

    private static async Task VerifyIdentityAsync(GraphHttp http, TenantSession s)
    {
        var (status, json, _) = await http.JsonAsync(s, HttpMethod.Get, $"{G}{s.Me}?$select=userPrincipalName");
        if (status != 200) throw new InvalidOperationException($"The {s.Role} sign-in check failed ({status}).");
        if (!string.Equals(json.GetProperty("userPrincipalName").GetString(), s.Upn, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The {s.Role} is signed in as a different account than configured. Stopping.");
    }
}
