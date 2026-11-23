namespace SmartMailboxCopy;

public enum MailboxState { Waiting, Running, Done, Problems, Failed, Skipped }

/// <summary>Where one mailbox in a batch stands.</summary>
public sealed record MailboxProgress(int Index, MapRow Row, MailboxState State, TreeOutcome? Outcome, string Message);

public sealed record BatchOutcome(IReadOnlyList<MailboxProgress> Results)
{
    public int Done => Results.Count(r => r.State == MailboxState.Done);
    public int Problems => Results.Count(r => r.State is MailboxState.Problems);
    public int Failed => Results.Count(r => r.State == MailboxState.Failed);
}

/// <summary>
/// Copies many mailboxes, one after another, each exactly as a single copy-tree run (same guards, journal and verification).
/// A problem in one mailbox is recorded and the batch carries on with the next. Logs name mailboxes by number, never by address;
/// the addresses appear only in the results the caller receives.
/// Meant for application (app-only) access: one app can reach each mailbox without anyone signing in as that person.
/// </summary>
public sealed class BatchCopier
{
    private readonly CopyConfig _cfg; private readonly IReadOnlyList<MapRow> _rows; private readonly Func<TreeOptions> _options; private readonly RunLog _log;
    public BatchCopier(CopyConfig cfg, IReadOnlyList<MapRow> rows, Func<TreeOptions> options, RunLog log) { _cfg = cfg; _rows = rows; _options = options; _log = log; }

    public event Action<MailboxProgress>? MailboxChanged;
    // Per-mailbox detail, forwarded from the TreeCopier currently running (the index says which mailbox).
    public event Action<int, string>? PhaseChanged;
    public event Action<int, IReadOnlyList<FolderInfo>>? FoldersDiscovered;
    public event Action<int, FolderProgress>? FolderChanged;
    public event Action<int, TreeTotals>? TotalsChanged;
    private void Raise(Action a) { try { a(); } catch { } }

    public async Task<BatchOutcome> RunAsync(CancellationToken ct = default)
    {
        var problems = MailboxMap.Validate(_rows, _cfg.Source.Tenant.Equals(_cfg.Target.Tenant, StringComparison.OrdinalIgnoreCase), _cfg.IsProtected);
        if (problems.Count > 0) throw new InvalidOperationException("The mailbox map has problems:\n  " + string.Join("\n  ", problems));

        var results = new List<MailboxProgress>();
        _log.Hide(_rows.SelectMany(r => new[] { r.Source, r.Target }).ToArray());
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i]; var n = i + 1;
            if (ct.IsCancellationRequested)
            {
                foreach (var rest in _rows.Skip(i).Select((r, k) => new MailboxProgress(i + k + 1, r, MailboxState.Skipped, null, "Stopped before this mailbox started.")))
                { results.Add(rest); Raise(() => MailboxChanged?.Invoke(rest)); }
                break;
            }
            _log.Context = n.ToString();
            _log.Info($"\n##### Mailbox {n} of {_rows.Count}");
            Report(results, new MailboxProgress(n, row, MailboxState.Running, null, "Copying…"), add: false);
            var cfg = new CopyConfig(_cfg.Source with { Upn = row.Source }, _cfg.Target with { Upn = row.Target }, _cfg.Protected);
            var copier = new TreeCopier(cfg, _options(), _log);
            copier.PhaseChanged += p => Raise(() => PhaseChanged?.Invoke(n, p));
            copier.FoldersDiscovered += f => Raise(() => FoldersDiscovered?.Invoke(n, f));
            copier.FolderChanged += f => Raise(() => FolderChanged?.Invoke(n, f));
            copier.TotalsChanged += t => Raise(() => TotalsChanged?.Invoke(n, t));
            MailboxProgress done;
            try
            {
                var o = await copier.RunAsync(ct);
                var bad = o.Failed > 0 || o.CountMismatches > 0 || (o.Verification?.Different ?? 0) > 0 || (o.Verification?.Unpaired ?? 0) > 0;
                done = new MailboxProgress(n, row, bad ? MailboxState.Problems : MailboxState.Done, o,
                    bad ? $"{o.Imported} imported, {o.Failed} failed, {o.CountMismatches} folders with different counts" + (o.Verification is { } v ? $", {v.Different} messages differ, {v.Unpaired} not compared" : "")
                        : $"{o.Imported} imported, {o.AlreadyDone} already done" + (o.Verification is { } vv ? $", {vv.Identical} of {vv.Paired} identical" : ""));
            }
            catch (OperationCanceledException) { done = new MailboxProgress(n, row, MailboxState.Skipped, null, "Stopped. Run again to resume; finished items are remembered."); }
            catch (Exception ex)
            {
                _log.Error($"Mailbox {n} stopped: {ex.GetType().Name}: {ex.Message}", ex);
                done = new MailboxProgress(n, row, MailboxState.Failed, null, ex.Message);
            }
            Report(results, done, add: true);
        }
        _log.Info($"\nBatch finished: {results.Count(r => r.State == MailboxState.Done)} done, {results.Count(r => r.State == MailboxState.Problems)} with problems, {results.Count(r => r.State == MailboxState.Failed)} failed, {results.Count(r => r.State == MailboxState.Skipped)} not run, of {_rows.Count}.");
        return new BatchOutcome(results);
    }

    private void Report(List<MailboxProgress> results, MailboxProgress p, bool add)
    {
        if (add) results.Add(p);
        Raise(() => MailboxChanged?.Invoke(p));
    }

    /// <summary>Writes the per-mailbox results (with addresses; this is the admin's own report) next to the other reports.</summary>
    public static string SaveResults(BatchOutcome outcome)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMailboxCopy", "reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"batch-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        File.WriteAllLines(path, new[] { "n,source,target,state,message" }.Concat(
            outcome.Results.Select(r => $"{r.Index},{Q(r.Row.Source)},{Q(r.Row.Target)},{r.State},{Q(r.Message)}")));
        return path;
    }
}
