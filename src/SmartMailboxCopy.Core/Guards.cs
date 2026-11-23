namespace SmartMailboxCopy;

/// <summary>What the source side is allowed to touch: only the one designated folder and the items listed from it.</summary>
internal sealed class SourceScope
{
    public string MailFolderId = "";
    public string AdminFolderId = "";
    public readonly IdSet MessageIds = new();     // mail-API message ids listed from the designated folder
    public readonly IdSet AdminItemIds = new();   // import/export-API item ids listed from the designated folder
    public readonly IdSet TreeFolders = new();    // tree mode: import/export-API folder ids discovered by walking the source tree
    public readonly IdSet MailFolders = new();    // tree mode: mail-API folder ids resolved for the verification pass (read-only)
}

/// <summary>What the target side is allowed to touch: only the destination folder this run created or resumed.</summary>
internal sealed class TargetScope
{
    public readonly IdSet Folders = new();
    public readonly IdSet Messages = new();
    public readonly IdSet AdminFolders = new();   // tree mode: import/export-API ids of folders this run created or resumed
}

/// <summary>
/// The safety rules, enforced on every request:
///  - the SOURCE is read-only (GETs inside the designated folder, plus exportItems, which only reads);
///  - the TARGET may only create the destination folder, create an import session, and read what it created.
/// </summary>
internal static class Guards
{
    private const string G = GraphHttp.Graph;

    /// <summary>Well-known folders that merge mode maps onto the target's real folders (read to find their ids; items are only ever added).</summary>
    public static readonly string[] MergeableWellKnown = { "inbox", "sentitems", "drafts", "deleteditems", "junkemail", "outbox", "archive" };

    /// <summary>Tree mode: the source may read the folder tree and items of folders discovered from it; the target may only create folders under the container this run owns.</summary>
    public static void ApplyTree(TenantSession src, SourceScope ss, TenantSession tgt, TargetScope ts)
    {
        src.Allowed = (method, url) =>
        {
            if (method == HttpMethod.Get)
            {
                if (url.StartsWith($"{G}{src.Me}?$select=")) return true;
                if (url.Contains(src.Me + "/settings/exchange")) return true;
                if (src.MailboxId != "" && url.Contains("/admin/exchange/mailboxes/" + src.MailboxId + "/folders", StringComparison.OrdinalIgnoreCase))
                    return !url.Contains("/items", StringComparison.OrdinalIgnoreCase) || ss.TreeFolders.InUrl(url);
                // verification pass: read-only mail-API reads of folders resolved from the tree, and of messages listed from them
                if (url.StartsWith($"{G}{src.Me}/mailFolders?")) return true;
                return ss.MailFolders.InUrl(url) || ss.MessageIds.InUrl(url);
            }
            return method == HttpMethod.Post && src.MailboxId != "" && url == $"{G}/admin/exchange/mailboxes/{src.MailboxId}/exportItems";
        };

        tgt.Allowed = (method, url) =>
        {
            if (method == HttpMethod.Get && (url.StartsWith($"{G}{tgt.Me}?$select=") || url.Contains(tgt.Me + "/settings/exchange"))) return true;
            if (method == HttpMethod.Get && url.StartsWith($"{G}{tgt.Me}/mailFolders?")) return true;              // find the container by name
            if (method == HttpMethod.Get && (url.StartsWith($"{G}{tgt.Me}/mailFolders/msgfolderroot?") || MergeableWellKnown.Any(n => url.StartsWith($"{G}{tgt.Me}/mailFolders/{n}?")))) return true;   // merge mode: locate real folders
            if (method == HttpMethod.Post && url == $"{G}{tgt.Me}/mailFolders") return true;                       // create the container
            // additional calendars and contact folders (copied with --include-pim): look up by name, create under the default ones, or under our own contact folders
            if (method == HttpMethod.Get && (url.StartsWith($"{G}{tgt.Me}/calendars?") || url.StartsWith($"{G}{tgt.Me}/contactFolders?"))) return true;
            if (method == HttpMethod.Post && (url == $"{G}{tgt.Me}/calendars" || url == $"{G}{tgt.Me}/contactFolders")) return true;
            if (method == HttpMethod.Post && url.EndsWith("/childFolders") && ts.Folders.InUrl(url)) return true;  // create folders only under our own
            if (tgt.MailboxId != "" && url.Contains("/admin/exchange/mailboxes/" + tgt.MailboxId, StringComparison.OrdinalIgnoreCase))
            {
                if (method == HttpMethod.Get && url.Contains("/folders", StringComparison.OrdinalIgnoreCase) && (!url.Contains("/items", StringComparison.OrdinalIgnoreCase) || ts.AdminFolders.InUrl(url))) return true;
                if (method == HttpMethod.Post && url.EndsWith("/createImportSession", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return method == HttpMethod.Get && (ts.Folders.InUrl(url) || ts.Messages.InUrl(url));   // read-back for verification
        };
    }

    public static void Apply(TenantSession src, SourceScope ss, TenantSession tgt, TargetScope ts)
    {
        src.Allowed = (method, url) =>
        {
            if (method == HttpMethod.Get)
            {
                if (url.StartsWith($"{G}{src.Me}?$select=")) return true;                                        // identity check
                if (url.Contains(src.Me + "/settings/exchange")) return true;                                    // mailbox id
                if (url.StartsWith($"{G}{src.Me}/mailFolders?")) return true;                                    // top-level folder lookup by name
                if (ss.MailFolderId != "" && url.Contains(src.Me + "/mailFolders/" + ss.MailFolderId)) return true;
                if (ss.MessageIds.InUrl(url)) return true;
                if (src.MailboxId != "" && url.Contains("/admin/exchange/mailboxes/" + src.MailboxId + "/folders", StringComparison.OrdinalIgnoreCase))
                    return !url.Contains("/items") || (ss.AdminFolderId != "" && url.Contains(ss.AdminFolderId));
                return false;
            }
            // exportItems reads only; the caller checks that every id came from the designated folder
            return method == HttpMethod.Post && src.MailboxId != "" && url == $"{G}/admin/exchange/mailboxes/{src.MailboxId}/exportItems";
        };

        tgt.Allowed = (method, url) =>
        {
            if (method == HttpMethod.Get && (url.StartsWith($"{G}{tgt.Me}?$select=") || url.Contains(tgt.Me + "/settings/exchange"))) return true;
            if (method == HttpMethod.Get && url.StartsWith($"{G}{tgt.Me}/mailFolders?")) return true;              // find the destination folder by name
            if (method == HttpMethod.Post && url == $"{G}{tgt.Me}/mailFolders") return true;                       // create the destination folder
            if (tgt.MailboxId != "" && url.Contains("/admin/exchange/mailboxes/" + tgt.MailboxId, StringComparison.OrdinalIgnoreCase))
            {
                if (method == HttpMethod.Get && url.Contains("/folders", StringComparison.OrdinalIgnoreCase) && !url.Contains("/items", StringComparison.OrdinalIgnoreCase)) return true;
                if (method == HttpMethod.Post && url.EndsWith("/createImportSession", StringComparison.OrdinalIgnoreCase)) return true;
            }
            // read-back of what this run created, for verification; never a write or a delete
            return method == HttpMethod.Get && (ts.Folders.InUrl(url) || ts.Messages.InUrl(url));
        };
    }
}
