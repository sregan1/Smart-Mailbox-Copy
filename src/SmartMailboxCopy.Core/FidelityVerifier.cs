using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartMailboxCopy;

public sealed record VerifyOutcome(int Paired, int Identical, int Different, int Unpaired);

/// <summary>
/// Compares every copied message with its source (paired by internet message id) field by field.
/// Reports never contain mail content: content fields are named, never shown; only flags, dates and importance show values.
/// </summary>
internal sealed class FidelityVerifier
{
    private const string G = GraphHttp.Graph;
    private const string PrTime = "SystemTime 0x0E06", PrSubmit = "SystemTime 0x0039", PrFlags = "Integer 0x0E07";
    private const string Select = "id,subject,body,bodyPreview,isDraft,isRead,receivedDateTime,sentDateTime,importance,categories,flag,from,toRecipients,ccRecipients,bccRecipients,internetMessageId,internetMessageHeaders";
    private static readonly string Expand =
        $"attachments($select=name,size,isInline),singleValueExtendedProperties($filter=id eq '{PrTime}' or id eq '{PrFlags}' or id eq '{PrSubmit}')";
    private static readonly HashSet<string> ShownValues = new() { "isDraft", "isRead", "received", "sent", "importance", "flag", "body.type" };

    private readonly GraphHttp _http; private readonly TenantSession _src, _tgt; private readonly RunLog _log;
    public FidelityVerifier(GraphHttp http, TenantSession src, TenantSession tgt, RunLog log) { _http = http; _src = src; _tgt = tgt; _log = log; }

    public async Task<VerifyOutcome> VerifyAsync(IReadOnlyDictionary<string, string> sourceByMessageId, IReadOnlyList<(string MessageId, string TargetId)> copies)
    {
        var pairs = new List<(string Key, string SrcId, string DstId)>(); var unpaired = 0; var n = 0;
        foreach (var (imid, tid) in copies)
        {
            if (imid != "" && sourceByMessageId.TryGetValue(imid, out var sid)) pairs.Add(($"item {++n}", sid, tid)); else unpaired++;
        }
        _log.Info($"{copies.Count} messages in the target folder; {pairs.Count} paired with a source message by internet message id; {unpaired} could not be paired.");
        return await CompareAsync(pairs, unpaired);
    }

    /// <summary>Compares already-paired messages (key is a label for the log, never content). Used directly by tree mode, which pairs per folder.</summary>
    public async Task<VerifyOutcome> CompareAsync(IReadOnlyList<(string Key, string SrcId, string DstId)> pairs, int unpaired)
    {
        int identical = 0; var perField = new SortedDictionary<string, int>(); var info = new SortedDictionary<string, int>(); var i = 0;
        foreach (var (key, sid, did) in pairs)
        {
            if (++i % 25 == 0) _log.Info($"  …compared {i}/{pairs.Count}");
            var a = await SnapshotAsync(_src, sid); var b = await SnapshotAsync(_tgt, did);
            var keys = a.Keys.Union(b.Keys).ToList();
            var diffs = keys.Where(k => !k.StartsWith("info.") && a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).ToList();
            foreach (var k in keys.Where(k => k.StartsWith("info.") && a.GetValueOrDefault(k) != b.GetValueOrDefault(k))) info[k] = info.GetValueOrDefault(k) + 1;
            foreach (var k in diffs) perField[k] = perField.GetValueOrDefault(k) + 1;
            if (diffs.Count == 0) { identical++; continue; }
            _log.Info($"- {key}: differs in " + string.Join("; ", diffs.Select(k => ShownValues.Contains(k) ? $"{k} `{a.GetValueOrDefault(k)}` → `{b.GetValueOrDefault(k)}`" : k)));
            if (diffs.Contains("attachments"))   // sizes and inline flags only, never names
                _log.Info($"    attachments (size, inline) source: [{AttachmentShape(a.GetValueOrDefault("attachments"))}]  target: [{AttachmentShape(b.GetValueOrDefault("attachments"))}]");
        }
        _log.Info($"{identical} of {pairs.Count} items identical on every compared field; {unpaired} could not be paired.");
        _log.Info($"Fields that differed, by number of items: {(perField.Count == 0 ? "none" : string.Join(", ", perField.Select(p => $"{p.Key}={p.Value}")))}");
        _log.Info($"Informational differences (not counted as loss): {(info.Count == 0 ? "none" : string.Join(", ", info.Select(p => $"{p.Key}={p.Value}")))}");
        return new VerifyOutcome(pairs.Count, identical, pairs.Count - identical, unpaired);
    }

    /// <summary>"name:12KB:inline | other:3KB" becomes "12KB inline, 3KB": the shape of the attachments without their names.</summary>
    private static string AttachmentShape(string? s) =>
        string.IsNullOrEmpty(s) ? "none" : string.Join(", ", s.Split(" | ").Select(p => { var m = Regex.Match(p, @":(\d+KB|item)(:inline)?$"); return m.Success ? m.Groups[1].Value + (m.Groups[2].Success ? " inline" : "") : "?"; }));

    private async Task<SortedDictionary<string, string>> SnapshotAsync(TenantSession s, string id)
    {
        var (status, j, _) = await _http.JsonAsync(s, HttpMethod.Get, $"{G}{s.Me}/messages/{id}?$select={Select}&$expand={Uri.EscapeDataString(Expand).Replace("%24", "$")}");
        var d = new SortedDictionary<string, string>();
        if (status != 200) { d["_error"] = status.ToString(); return d; }
        string S(string n) => j.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        string Addrs(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? string.Join(";", v.EnumerateArray().Select(x => x.GetProperty("emailAddress").GetProperty("address").GetString()?.ToLowerInvariant()).OrderBy(x => x)) : "";
        var preview = Regex.Replace(j.GetProperty("bodyPreview").GetString() ?? "", @"\s+", " ").Trim();
        d["subject"] = S("subject");
        d["body.type"] = j.GetProperty("body").GetProperty("contentType").GetString() ?? "";
        d["body.text"] = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(preview)))[..10];
        d["isDraft"] = S("isDraft"); d["isRead"] = S("isRead");
        d["received"] = S("receivedDateTime"); d["sent"] = S("sentDateTime"); d["importance"] = S("importance");
        d["categories"] = j.TryGetProperty("categories", out var c) ? string.Join(",", c.EnumerateArray().Select(x => x.GetString())) : "";
        d["flag"] = j.TryGetProperty("flag", out var f) && f.TryGetProperty("flagStatus", out var fs) ? fs.GetString() ?? "" : "";
        d["from"] = j.TryGetProperty("from", out var fr) && fr.ValueKind == JsonValueKind.Object ? fr.GetProperty("emailAddress").GetProperty("address").GetString()?.ToLowerInvariant() ?? "" : "";
        d["to"] = Addrs("toRecipients"); d["cc"] = Addrs("ccRecipients"); d["bcc"] = Addrs("bccRecipients");
        // File attachments are compared by name, exact size and inline flag. An item attachment (an embedded message) is compared by name
        // and inline flag only: Exchange re-serializes the embedded message on import, so its reported size can shift by a few KB.
        // That size is kept as an informational difference (info.itemAttachmentSize), never silently dropped.
        string AttType(JsonElement x) => x.TryGetProperty("@odata.type", out var t) ? t.GetString() ?? "" : "";
        bool IsItem(JsonElement x) => AttType(x).EndsWith("itemAttachment", StringComparison.OrdinalIgnoreCase);
        if (j.TryGetProperty("attachments", out var at))
        {
            var list = at.EnumerateArray().ToList();
            d["attachments"] = string.Join(" | ", list.Select(x => $"{x.GetProperty("name").GetString()}:{(IsItem(x) ? "item" : x.GetProperty("size").GetInt64() / 1024 + "KB")}{(x.GetProperty("isInline").GetBoolean() ? ":inline" : "")}").OrderBy(x => x));
            d["info.itemAttachmentSize"] = string.Join(",", list.Where(IsItem).Select(x => (x.GetProperty("size").GetInt64() / 1024) + "KB").OrderBy(x => x));
        }
        else d["attachments"] = "";
        d["info.internetMessageId"] = S("internetMessageId");
        d["info.headers.count"] = j.TryGetProperty("internetMessageHeaders", out var hh) && hh.ValueKind == JsonValueKind.Array ? hh.GetArrayLength().ToString() : "";
        // PidTagMessageFlags differs by the UNMODIFIED bit after an import (see docs/SPIKE-FINDINGS.md); informational only.
        if (j.TryGetProperty("singleValueExtendedProperties", out var sv) && sv.ValueKind == JsonValueKind.Array)
            foreach (var p in sv.EnumerateArray()) d["info.prop " + p.GetProperty("id").GetString()] = p.GetProperty("value").GetString() ?? "";
        return d;
    }
}
