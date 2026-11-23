namespace SmartMailboxCopy;

/// <summary>One source mailbox and the target mailbox it is copied into.</summary>
public sealed record MapRow(string Source, string Target);

/// <summary>Which source mailbox goes to which target mailbox. Cross-tenant addresses differ, so this is explicit and reviewable (CSV: source,target).</summary>
public static class MailboxMap
{
    /// <summary>user@old.com becomes user@new.com. Rows can then be edited by hand where the names differ between tenants.</summary>
    public static List<MapRow> DomainSwap(IEnumerable<string> sources, string targetDomain) =>
        sources.Select(s => new MapRow(s, s.Contains('@') ? s[..s.IndexOf('@')] + "@" + targetDomain.TrimStart('@') : s)).ToList();

    public static List<MapRow> Load(string path)
    {
        var rows = new List<MapRow>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line == "" || line.StartsWith('#')) continue;
            var parts = line.Split(new[] { ',', ';', '\t' }, 2).Select(p => p.Trim().Trim('"')).ToArray();
            if (parts.Length != 2) throw new InvalidOperationException($"{path}: expected \"source,target\" but found: {line}");
            if (rows.Count == 0 && string.Equals(parts[0], "source", StringComparison.OrdinalIgnoreCase)) continue;   // header row
            rows.Add(new MapRow(parts[0], parts[1]));
        }
        return rows;
    }

    public static void Save(string path, IEnumerable<MapRow> rows) =>
        File.WriteAllLines(path, new[] { "source,target" }.Concat(rows.Select(r => $"{r.Source},{r.Target}")));

    /// <summary>Problems that must be fixed before a run. Empty means the map is usable.</summary>
    public static List<string> Validate(IReadOnlyList<MapRow> rows, bool sameTenant, Func<string, bool> isProtected)
    {
        var problems = new List<string>();
        if (rows.Count == 0) problems.Add("The map has no mailboxes.");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var n = i + 1;
            if (!r.Source.Contains('@')) problems.Add($"Row {n}: the source is not an email address.");
            if (!r.Target.Contains('@')) problems.Add($"Row {n}: the target is not an email address.");
            if (sameTenant && string.Equals(r.Source, r.Target, StringComparison.OrdinalIgnoreCase)) problems.Add($"Row {n}: the source and target are the same mailbox.");
            if (isProtected(r.Target)) problems.Add($"Row {n}: the target is a protected mailbox.");
        }
        foreach (var g in rows.GroupBy(r => r.Source, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) problems.Add($"{g.Count()} rows have the same source ({g.Key}).");
        foreach (var g in rows.GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) problems.Add($"{g.Count()} rows copy into the same target ({g.Key}); two mailboxes would be mixed together.");
        return problems;
    }
}
