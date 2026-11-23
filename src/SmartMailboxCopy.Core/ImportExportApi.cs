using System.Text;
using System.Text.Json;

namespace SmartMailboxCopy;

/// <summary>An item as the import/export API lists it. Keys holds the names (never the values) of the properties the API returned, for discovery.</summary>
internal sealed record AdminItem(string Id, string Type, long Size, string Keys = "", string ChangeKey = "", string Created = "");
internal sealed record ExportedItem(string Id, string Data);

/// <summary>An import session: a pre-authenticated URL (no Authorization header) tied to one target mailbox.</summary>
internal sealed class ImportSession
{
    public string Url { get; init; } = "";
    public DateTime ExpiresUtc { get; init; }
    public bool NearlyExpired => ExpiresUtc - DateTime.UtcNow < TimeSpan.FromSeconds(60);
}

/// <summary>Thin wrappers over Microsoft Graph's mailbox import/export API (/admin/exchange/mailboxes/...).</summary>
internal static class ImportExportApi
{
    private const string G = GraphHttp.Graph;

    public static async Task<string> GetMailboxIdAsync(GraphHttp http, TenantSession s)
    {
        var (status, json, body) = await http.JsonAsync(s, HttpMethod.Get, $"{G}{s.Me}/settings/exchange");
        if (status != 200) throw new InvalidOperationException($"Could not get the {s.Role} mailbox id ({status}): {GraphHttp.Truncate(body)}");
        return json.GetProperty("primaryMailboxId").GetString()!;
    }

    public static Task<List<JsonElement>> ListFoldersAsync(GraphHttp http, TenantSession s) =>
        http.GetAllAsync(s, $"{G}/admin/exchange/mailboxes/{s.MailboxId}/folders?$top=100");

    public static async Task<List<AdminItem>> ListItemsAsync(GraphHttp http, TenantSession s, string adminFolderId)
    {
        var items = await http.GetAllAsync(s, $"{G}/admin/exchange/mailboxes/{s.MailboxId}/folders/{adminFolderId}/items?$top=100");
        return items.Select(i => new AdminItem(
            i.GetProperty("id").GetString()!,
            i.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
            i.TryGetProperty("size", out var z) ? z.GetInt64() : 0,
            string.Join(",", i.EnumerateObject().Select(p => p.Name)),
            i.TryGetProperty("changeKey", out var ck) && ck.ValueKind == JsonValueKind.String ? ck.GetString() ?? "" : "",
            i.TryGetProperty("createdDateTime", out var cd) && cd.ValueKind == JsonValueKind.String ? cd.GetString() ?? "" : "")).ToList();
    }

    /// <summary>Exports items in batches of 20 (the API maximum); a failed batch is retried one item at a time.</summary>
    public static async Task<List<ExportedItem>> ExportAsync(GraphHttp http, TenantSession s, IReadOnlyList<string> ids, RunLog log)
    {
        var result = new List<ExportedItem>();
        var url = $"{G}/admin/exchange/mailboxes/{s.MailboxId}/exportItems";
        for (var offset = 0; offset < ids.Count; offset += 20)
        {
            var batch = ids.Skip(offset).Take(20).ToList();
            var (status, json, body) = await http.JsonAsync(s, HttpMethod.Post, url, new { itemIds = batch });
            if (status == 200) Collect(json, result);
            else
            {
                log.Warn($"batch export of {batch.Count} items failed ({status}): {GraphHttp.Truncate(body, 200)} — retrying that batch one item at a time");
                foreach (var id in batch)
                {
                    var (s1, j1, b1) = await http.JsonAsync(s, HttpMethod.Post, url, new { itemIds = new[] { id } });
                    if (s1 == 200) Collect(j1, result);
                    else log.Error($"EXPORT FAILED for source item id {id} ({s1}): {GraphHttp.Truncate(b1, 200)}");
                }
            }
            log.Debug($"exported {result.Count}/{ids.Count}");
        }
        return result;

        static void Collect(JsonElement json, List<ExportedItem> into)
        {
            foreach (var v in json.GetProperty("value").EnumerateArray())
                into.Add(new ExportedItem(v.GetProperty("itemId").GetString()!, v.GetProperty("data").GetString()!));
        }
    }

    public static async Task<ImportSession> CreateSessionAsync(GraphHttp http, TenantSession target)
    {
        var (status, json, body) = await http.JsonAsync(target, HttpMethod.Post, $"{G}/admin/exchange/mailboxes/{target.MailboxId}/createImportSession");
        if (status is not (200 or 201)) throw new InvalidOperationException($"createImportSession failed ({status}): {GraphHttp.Truncate(body)}");
        var url = json.GetProperty("importUrl").GetString()!;
        // The URL is pre-authenticated, so make sure it points at the target mailbox and nowhere else.
        if (new Uri(url).Host != "outlook.office365.com" || !url.Contains(target.MailboxId))
            throw new InvalidOperationException("The import URL is not the target mailbox on outlook.office365.com. Stopping.");
        return new ImportSession { Url = url, ExpiresUtc = json.GetProperty("expirationDateTime").GetDateTime().ToUniversalTime() };
    }

    public static async Task<(bool Ok, string Detail)> ImportAsync(GraphHttp http, ImportSession session, string adminFolderId, string data)
    {
        var content = new StringContent(JsonSerializer.Serialize(new { FolderId = adminFolderId, Mode = "create", Data = data }), Encoding.UTF8, "application/json");
        var (status, body) = await http.SendAsync(null, HttpMethod.Post, session.Url, content);   // no Authorization header, by design
        return (status == 200, status == 200 ? "" : $"{status}: {GraphHttp.Truncate(body, 300)}");
    }
}
