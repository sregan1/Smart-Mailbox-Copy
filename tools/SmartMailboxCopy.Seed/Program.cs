// Seeding harness for the migration tests. NOT part of the product: it WRITES to a mailbox, so it lives outside src/.
// It fills a throwaway TEST mailbox (the config's "source" account) with a synthetic tree of messages.
// Safety: you must pass --confirm-test-mailbox <upn> and it must equal the config's sourceUpn; every write is checked
// against a guard (new folders only under the ZZ-Seed- container this run created; messages only in folders it owns).
// Everything it creates is synthetic. It never reads existing mail.
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;

const string Prefix = "ZZ-Seed-";
const string Graph = "https://graph.microsoft.com/v1.0";
const string PrTime = "SystemTime 0x0E06", PrSubmit = "SystemTime 0x0039", PrFlags = "Integer 0x0E07";
var WellKnown = new[] { "inbox", "sentitems", "drafts", "deleteditems", "junkemail" };
const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

const string Usage = """
    Smart Mailbox Copy seeding harness (writes synthetic mail into a TEST mailbox).

      SmartMailboxCopySeed seed    --confirm-test-mailbox <upn> [--size small|volume] [--count <bulk messages>] [--wellknown] [--pim] [--pim-extra] [--mailbox <shared upn>] [--config <path>]
      SmartMailboxCopySeed cleanup --confirm-test-mailbox <upn> [--mailbox <shared upn>] [--config <path>]

    seed     creates a ZZ-Seed-<timestamp> folder holding a nested tree (4 levels, special characters, a long name, the same
             name under two parents, an empty folder), 9 designed messages (attachments incl. 8 MB, inline image, attached
             message, genuine draft, cc/bcc, flags, categories, a 5-year-old date) and bulk messages spread over the tree.
             --size volume: 10,000 bulk messages and three 25 MB attachments (override with --count).
             --wellknown: also puts messages in Inbox, Sent Items, Drafts, Deleted Items and Junk, and a ZZ-Seed- subfolder under
                          Inbox (for the well-known folder merge test).
             --pim: also creates 5 contacts in the default Contacts folder and 5 events in the default Calendar (M3 tests). Needs
                    the Contacts.ReadWrite and Calendars.ReadWrite permissions on the seed app. Events have no outside attendees.
             --pim-extra: also creates an additional calendar (3 events, one a weekly series) and an additional contact folder (3 contacts)
                          with a nested contact subfolder (2 contacts), named ZZ-Seed-... (tests copying additional calendars and contact folders).
             --mailbox <upn>: seed a SHARED test mailbox instead of the seed account's own. The seed account signs in and must have Full Access to
                          it; the seed app needs the delegated Mail.ReadWrite.Shared permission. Pass the shared address to --confirm-test-mailbox.
    cleanup  deletes every ZZ-Seed- top-level folder (and ZZ-Seed- folder under Inbox), and the well-known messages, contacts and
             events recorded by earlier seed runs.

    The account is the config's seedUpn (seedTenant, seedClientId): a throwaway test account, never the copy's source or target and never
    one listed in protectedMailboxes. You must confirm it by address.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
if (args[0] is not ("seed" or "cleanup")) { Console.WriteLine(Usage); return 1; }
string? Opt(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string n) => args.Contains(n);

// ---- config (only the source side is used) ----
var cfgPath = Opt("--config") ?? Find(Directory.GetCurrentDirectory()) ?? Find(AppContext.BaseDirectory);
if (cfgPath == null) { Console.WriteLine("smartmailboxcopy.config.json not found."); return 1; }
var root = JsonDocument.Parse(File.ReadAllText(cfgPath)).RootElement;
string C(string n) => root.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
// The seeder has its OWN account settings (seedTenant, seedClientId, seedUpn). It never uses sourceUpn or targetUpn,
// and it refuses any account listed in protectedMailboxes or used as the copy's source or target.
var tenant = C("seedTenant"); var clientId = C("seedClientId"); var upn = C("seedUpn");
if (tenant == "" || clientId == "" || upn == "" || tenant.StartsWith('<') || clientId.StartsWith('<') || upn.StartsWith('<'))
{ Console.WriteLine("seedTenant, seedClientId and seedUpn must be filled in (a throwaway TEST account; use the Setup tab of the app)."); return 1; }
// The seed account may equal sourceUpn (the test source mailbox is seeded, then copied from). It may never be the
// target or a protected mailbox, so a real mailbox listed as sourceUpn must also be in protectedMailboxes.
var blocked = new List<string> { C("targetUpn") };
if (root.TryGetProperty("protectedMailboxes", out var pm) && pm.ValueKind == JsonValueKind.Array) blocked.AddRange(pm.EnumerateArray().Select(x => x.GetString() ?? ""));
if (blocked.Any(b => b != "" && string.Equals(b, upn, StringComparison.OrdinalIgnoreCase)))
{ Console.WriteLine("Refusing: the seed account is a protected mailbox or the copy's source/target account. Seeding writes mail, so it needs its own throwaway test account."); return 1; }
// Optionally seed a SHARED test mailbox the seed account can reach (Full Access) instead of its own. It gets the same protections.
var mailbox = Opt("--mailbox");
if (mailbox != null && blocked.Any(b => b != "" && string.Equals(b, mailbox, StringComparison.OrdinalIgnoreCase)))
{ Console.WriteLine("Refusing: that shared mailbox is a protected mailbox or the copy's target account."); return 1; }
var writeTarget = mailbox ?? upn;
if (!string.Equals(Opt("--confirm-test-mailbox"), writeTarget, StringComparison.OrdinalIgnoreCase))
{ Console.WriteLine(mailbox == null ? "Refusing: pass --confirm-test-mailbox with the exact seedUpn from the config, to confirm that account is a throwaway test mailbox."
                                    : "Refusing: pass --confirm-test-mailbox with the exact shared mailbox address given to --mailbox, to confirm it is a throwaway test mailbox."); return 1; }
var Me = mailbox == null ? "/me" : $"/users/{mailbox}";   // every mailbox URL below is built on this

// ---- auth + guarded HTTP ----
var app = PublicClientApplicationBuilder.Create(clientId).WithAuthority($"https://login.microsoftonline.com/{tenant}").WithRedirectUri("http://localhost").Build();
var seedsDirEarly = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMailboxCopy", "seeds");
// Contacts and calendar permissions are requested only when seeding them (--pim) or when an earlier --pim seed left items to clean up.
var pimExtra = Flag("--pim-extra");
var pimMode = Flag("--pim") || pimExtra || (args[0] == "cleanup" && Directory.Exists(seedsDirEarly) && Directory.GetFiles(seedsDirEarly, "pim-*.ids").Length > 0);
var scopes = pimMode
    ? new[] { "https://graph.microsoft.com/Mail.ReadWrite", "https://graph.microsoft.com/Contacts.ReadWrite", "https://graph.microsoft.com/Calendars.ReadWrite" }
    : new[] { "https://graph.microsoft.com/Mail.ReadWrite" };
if (mailbox != null) scopes = scopes.Append("https://graph.microsoft.com/Mail.ReadWrite.Shared").ToArray();
IAccount? account = null;
async Task<string> Token()
{
    if (account != null) { try { return (await app.AcquireTokenSilent(scopes, account).ExecuteAsync()).AccessToken; } catch (MsalUiRequiredException) { } }
    // Fixed on purpose: 4 attempts of 12 seconds, each opening a fresh browser window (use the newest).
    for (var attempt = 1; attempt <= 4; attempt++)
    {
        Console.WriteLine($">>> SIGN IN NOW with the test account @{upn[(upn.IndexOf('@') + 1)..]} - attempt {attempt}/4, waiting 12s (use the NEWEST window).");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var r = await app.AcquireTokenInteractive(scopes).WithLoginHint(upn).ExecuteAsync(cts.Token);
            if (!string.Equals(r.Account.Username, upn, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Signed in as a different account than the configured seed account. Stopping.");
            account = r.Account; return r.AccessToken;
        }
        catch (OperationCanceledException) { Console.WriteLine("no sign-in within 12s."); }
    }
    throw new TimeoutException("Sign-in was not completed after 4 attempts.");
}

var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var ownedFolders = new ConcurrentDictionary<string, byte>(); var ownedMessages = new ConcurrentDictionary<string, byte>();
var uploadUrls = new ConcurrentDictionary<string, byte>();
var cleanupMode = args[0] == "cleanup"; var wellknownMode = Flag("--wellknown");
var throttles = 0;
bool Owned(string url) => ownedFolders.Keys.Any(url.Contains) || ownedMessages.Keys.Any(url.Contains);
bool Allowed(HttpMethod m, string url, bool auth)
{
    if (!auth) return m == HttpMethod.Put && uploadUrls.ContainsKey(url);           // pre-authenticated upload URLs handed back by Graph
    if (m == HttpMethod.Get)
        return url.StartsWith($"{Graph}/me?$select=") || (cleanupMode && (url.StartsWith($"{Graph}{Me}/mailFolders?") || url.StartsWith($"{Graph}{Me}/mailFolders/inbox/childFolders?"))) || Owned(url);
    if (m == HttpMethod.Post)
    {
        if (url == $"{Graph}{Me}/mailFolders") return true;                           // the container (name checked at the call site)
        if (wellknownMode && WellKnown.Any(w => url == $"{Graph}{Me}/mailFolders/{w}/messages")) return true;
        if (wellknownMode && url == $"{Graph}{Me}/mailFolders/inbox/childFolders") return true;   // one ZZ-Seed- subfolder under the real Inbox
        if (pimMode && !cleanupMode && (url == $"{Graph}{Me}/contacts" || url == $"{Graph}{Me}/events" || url == $"{Graph}{Me}/calendars" || url == $"{Graph}{Me}/contactFolders")) return true;   // synthetic contacts and events only
        return Owned(url);                                                           // childFolders / messages / attachments in folders or messages we own
    }
    return m == HttpMethod.Delete && cleanupMode && Owned(url);
}
async Task<(int Status, JsonElement Json, string Body)> G(HttpMethod m, string url, object? body = null)
{
    if (!Allowed(m, url, true)) throw new InvalidOperationException($"Refusing {m} {url}");
    for (var attempt = 0; ; attempt++)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync(); var code = (int)resp.StatusCode;
        if (code is 429 or 503 or 504 && attempt < 8)
        {
            Interlocked.Increment(ref throttles);
            await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, 5 * (attempt + 1)))); continue;
        }
        JsonElement el = default; try { if (text.Length > 0) el = JsonDocument.Parse(text).RootElement.Clone(); } catch { }
        return (code, el, text);
    }
}
string Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
void Say(string s) => Console.WriteLine(s);

var seedsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMailboxCopy", "seeds");
Directory.CreateDirectory(seedsDir);

// identity check
{
    var (st, j, _) = await G(HttpMethod.Get, $"{Graph}/me?$select=userPrincipalName");
    if (st != 200 || !string.Equals(Str(j, "userPrincipalName"), upn, StringComparison.OrdinalIgnoreCase)) { Say("Sign-in check failed or wrong account. Stopping."); return 1; }
}

if (cleanupMode) return await Cleanup();
return await Seed();

// =====================================================================================
async Task<int> Cleanup()
{
    var deleted = 0;
    foreach (var f in Directory.GetFiles(seedsDir, "wellknown-*.ids"))
    {
        foreach (var id in File.ReadAllLines(f).Where(l => l.Length > 0))
        {
            ownedMessages[id] = 1;
            var (st, _, _) = await G(HttpMethod.Delete, $"{Graph}{Me}/messages/{id}");
            if (st is 204 or 404) deleted++;
        }
        File.Delete(f);
    }
    Say($"Deleted {deleted} well-known-folder seed messages.");
    var pimDeleted = 0;
    foreach (var f in Directory.GetFiles(seedsDir, "pim-*.ids"))
    {
        foreach (var line in File.ReadAllLines(f).Where(l => l.Contains('|')))
        {
            var kind = line[..line.IndexOf('|')]; var id = line[(line.IndexOf('|') + 1)..];
            ownedMessages[id] = 1;
            var (st, _, _) = await G(HttpMethod.Delete, $"{Graph}{Me}/{(kind switch { "contact" => "contacts", "calendar" => "calendars", "contactfolder" => "contactFolders", _ => "events" })}/{id}");
            if (st is 204 or 404) pimDeleted++;
        }
        File.Delete(f);
    }
    if (pimDeleted > 0) Say($"Deleted {pimDeleted} seeded contacts and events.");
    var tops = new List<(string Id, string Name)>();
    foreach (var listUrl in new[] { $"{Graph}{Me}/mailFolders?$top=100&$select=id,displayName", $"{Graph}{Me}/mailFolders/inbox/childFolders?$top=100&$select=id,displayName" })
    {
        string? next = listUrl;
        while (next != null)
        {
            var (st, j, b) = await G(HttpMethod.Get, next);
            if (st != 200) { Say($"Could not list folders ({st})."); return 1; }
            foreach (var f in j.GetProperty("value").EnumerateArray()) if (Str(f, "displayName").StartsWith(Prefix, StringComparison.Ordinal)) tops.Add((Str(f, "id"), Str(f, "displayName")));
            next = j.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
    }
    foreach (var (id, name) in tops)
    {
        ownedFolders[id] = 1;
        var (st, _, b) = await G(HttpMethod.Delete, $"{Graph}{Me}/mailFolders/{id}");
        Say($"  {name}: {(st == 204 ? "deleted" : $"failed ({st})")}");
    }
    Say($"Cleanup done ({tops.Count} seed folders).");
    return 0;
}

async Task<int> Seed()
{
    var volume = string.Equals(Opt("--size"), "volume", StringComparison.OrdinalIgnoreCase);
    var bulk = int.TryParse(Opt("--count"), out var c) ? c : volume ? 10_000 : 100;
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var container = Prefix + stamp;
    Say($"Seeding {container}: {(volume ? "volume" : "small")} profile, {bulk} bulk messages{(wellknownMode ? ", plus well-known folders" : "")}.");

    var (cs, cj, cb) = await G(HttpMethod.Post, $"{Graph}{Me}/mailFolders", new { displayName = container });
    if (cs != 201) { Say($"Could not create the container ({cs}): {Trunc(cb)}"); return 1; }
    var containerId = Str(cj, "id"); ownedFolders[containerId] = 1;

    async Task<string> Folder(string parentId, string name)
    {
        var (st, j, b) = await G(HttpMethod.Post, $"{Graph}{Me}/mailFolders/{parentId}/childFolders", new { displayName = name });
        if (st != 201) throw new InvalidOperationException($"Folder '{name}': {st} {Trunc(b)}");
        var id = Str(j, "id"); ownedFolders[id] = 1; return id;
    }

    // ---- tree: 4 levels, special characters, a long name, a repeated name, an empty folder ----
    var alpha = await Folder(containerId, "Alpha");
    var alpha1 = await Folder(alpha, "Alpha Sub 1");
    var deep = await Folder(alpha1, "Deep 1");
    var deeper = await Folder(deep, "Deeper 1");
    var beta = await Folder(containerId, "R&D, Q1 'draft' – café #2");
    var sharedA = await Folder(alpha, "Shared Name");
    var sharedB = await Folder(beta, "Shared Name");
    var longName = await Folder(containerId, "Long " + new string('x', 110));
    await Folder(containerId, "Empty Folder");
    var designed = await Folder(containerId, "Designed");
    var bulkTargets = new[] { alpha, alpha1, deep, deeper, beta, sharedA, sharedB, longName };
    Say("Folder tree created (13 folders incl. the container, one of them empty).");

    // ---- designed messages (as in the spike) ----
    var me = upn; var created = 0;
    async Task<string> Msg(string folderUrl, string subject, string body, bool html, DateTime? received, bool read, bool draft, string? importance = null, string[]? cats = null, bool flagged = false, bool bcc = false, bool wk = false)
    {
        var recip = new[] { new { emailAddress = new { address = me, name = "Seed Self" } } };
        var props = new List<object>();
        if (!draft) props.Add(new { id = PrFlags, value = read ? "1" : "0" });
        if (received.HasValue) { props.Add(new { id = PrTime, value = received.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") }); props.Add(new { id = PrSubmit, value = received.Value.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ssZ") }); }
        var m = new Dictionary<string, object?>
        {
            ["subject"] = subject, ["body"] = new { contentType = html ? "html" : "text", content = body }, ["toRecipients"] = recip,
            ["ccRecipients"] = bcc ? recip : Array.Empty<object>(), ["bccRecipients"] = bcc ? recip : Array.Empty<object>(),
            ["isRead"] = read, ["importance"] = importance ?? "normal", ["categories"] = cats ?? Array.Empty<string>()
        };
        if (props.Count > 0) m["singleValueExtendedProperties"] = props;
        if (flagged) m["flag"] = new { flagStatus = "flagged" };
        var (st, j, b) = await G(HttpMethod.Post, folderUrl, m);
        if (st != 201) throw new InvalidOperationException($"Message create failed: {st} {Trunc(b)}");
        var id = Str(j, "id"); ownedMessages[id] = 1; Interlocked.Increment(ref created);
        if (wk) lock (seedsDir) File.AppendAllText(Path.Combine(seedsDir, $"wellknown-{stamp}.ids"), id + Environment.NewLine);
        return id;
    }
    async Task Attach(string msgId, string name, byte[] data, bool inline = false, string? cid = null)
    {
        if (data.Length < 3_000_000)
        {
            var (st, _, b) = await G(HttpMethod.Post, $"{Graph}{Me}/messages/{msgId}/attachments", new Dictionary<string, object?>
            { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = name, ["contentType"] = name.EndsWith(".png") ? "image/png" : "application/octet-stream", ["contentBytes"] = Convert.ToBase64String(data), ["isInline"] = inline, ["contentId"] = cid });
            if (st != 201) throw new InvalidOperationException($"Attachment failed: {st} {Trunc(b)}");
            return;
        }
        var (s1, j1, b1) = await G(HttpMethod.Post, $"{Graph}{Me}/messages/{msgId}/attachments/createUploadSession", new { AttachmentItem = new { attachmentType = "file", name, size = data.Length } });
        if (s1 is not (200 or 201)) throw new InvalidOperationException($"Upload session failed: {s1} {Trunc(b1)}");
        var url = Str(j1, "uploadUrl"); uploadUrls[url] = 1;
        const int chunk = 327_680 * 10;
        for (var off = 0; off < data.Length; off += chunk)
        {
            var len = Math.Min(chunk, data.Length - off);
            if (!Allowed(HttpMethod.Put, url, false)) throw new InvalidOperationException("Refusing upload.");
            using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(data, off, len) };
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(off, off + len - 1, data.Length);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var resp = await http.SendAsync(req);
            if ((int)resp.StatusCode >= 300) throw new InvalidOperationException($"Upload chunk failed: {(int)resp.StatusCode}");
        }
    }
    byte[] Rand(int n) { var d = new byte[n]; Random.Shared.NextBytes(d); return d; }
    string D = $"{Graph}{Me}/mailFolders/{designed}/messages";

    await Msg(D, "Seed 01 plain text", "Hello plain text", false, DateTime.UtcNow.AddDays(-10), true, false);
    var htmlId = await Msg(D, "Seed 02 html + inline image", "<html><body><p>Hi <b>there</b></p><img src=\"cid:pic1\"></body></html>", true, DateTime.UtcNow.AddDays(-9), false, false);
    await Attach(htmlId, "pic.png", Convert.FromBase64String(Png), true, "pic1");
    await Attach(await Msg(D, "Seed 03 small attachment", "see attached", false, DateTime.UtcNow.AddDays(-8), true, false), "small.bin", Rand(200_000));
    await Attach(await Msg(D, "Seed 04 large attachment (8 MB)", "big file attached", false, DateTime.UtcNow.AddDays(-7), true, false), "big8mb.bin", Rand(8 * 1024 * 1024));
    await Msg(D, "Seed 05 five years old", "old message", false, DateTime.UtcNow.AddYears(-5), true, false);
    await Msg(D, "Seed 06 flagged, categorized, high importance", "metadata test", false, DateTime.UtcNow.AddDays(-6), false, false, "high", new[] { "Blue category", "Red category" }, true);
    await Msg(D, "Seed 07 genuine draft", "still a draft", false, null, true, true);
    await Msg(D, "Seed 08 cc and bcc", "bcc test", false, DateTime.UtcNow.AddDays(-5), true, false, bcc: true);
    var itemAtt = await Msg(D, "Seed 09 attached message", "carries an attached message", false, DateTime.UtcNow.AddDays(-4), true, false);
    {
        var (st, _, b) = await G(HttpMethod.Post, $"{Graph}{Me}/messages/{itemAtt}/attachments", new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.itemAttachment", ["name"] = "attached message",
            ["item"] = new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.message", ["subject"] = "Inner message", ["body"] = new { contentType = "text", content = "inner" }, ["toRecipients"] = new[] { new { emailAddress = new { address = me } } } }
        });
        if (st != 201) throw new InvalidOperationException($"Item attachment failed: {st} {Trunc(b)}");
    }
    if (volume)
        for (var i = 1; i <= 3; i++)
            await Attach(await Msg(D, $"Seed 1{i} very large attachment (25 MB)", "very large", false, DateTime.UtcNow.AddDays(-3), true, false), $"big25mb-{i}.bin", Rand(25 * 1024 * 1024));
    Say($"Designed messages created ({created}).");

    // ---- bulk, 4 at a time (the measured sweet spot), spread over the tree ----
    var gate = new SemaphoreSlim(4); var tasks = new List<Task>(); var done = 0; var failures = 0;
    for (var i = 0; i < bulk; i++)
    {
        var n = i; var target = bulkTargets[n % bulkTargets.Length];
        await gate.WaitAsync();
        tasks.Add(Task.Run(async () =>
        {
            try { await Msg($"{Graph}{Me}/mailFolders/{target}/messages", $"Seed bulk {n:00000}", $"bulk body {n}", false, DateTime.UtcNow.AddDays(-n % 1500 - 20), n % 2 == 0, false); }
            catch (Exception ex) { if (Interlocked.Increment(ref failures) <= 3) Say($"  bulk failure: {Trunc(ex.Message)}"); }
            finally { gate.Release(); if (Interlocked.Increment(ref done) % 500 == 0) Say($"  bulk {done}/{bulk}"); }
        }));
    }
    await Task.WhenAll(tasks);

    // ---- well-known folders ----
    if (wellknownMode)
    {
        for (var i = 0; i < 5; i++) await Msg($"{Graph}{Me}/mailFolders/inbox/messages", $"Seed inbox {i}", "inbox body", false, DateTime.UtcNow.AddDays(-i - 1), i % 2 == 0, false, wk: true);
        for (var i = 0; i < 3; i++) await Msg($"{Graph}{Me}/mailFolders/sentitems/messages", $"Seed sent {i}", "sent body", false, DateTime.UtcNow.AddDays(-i - 1), true, false, wk: true);
        await Msg($"{Graph}{Me}/mailFolders/drafts/messages", "Seed drafts 0", "a draft", false, null, true, true, wk: true);
        for (var i = 0; i < 2; i++) await Msg($"{Graph}{Me}/mailFolders/deleteditems/messages", $"Seed deleted {i}", "deleted body", false, DateTime.UtcNow.AddDays(-i - 2), true, false, wk: true);
        for (var i = 0; i < 2; i++) await Msg($"{Graph}{Me}/mailFolders/junkemail/messages", $"Seed junk {i}", "junk body", false, DateTime.UtcNow.AddDays(-i - 2), false, false, wk: true);
        // a subfolder under the real Inbox (the merge must recreate it under the target's real Inbox)
        var (ics, icj, icb) = await G(HttpMethod.Post, $"{Graph}{Me}/mailFolders/inbox/childFolders", new { displayName = Prefix + "InboxChild-" + stamp });
        if (ics != 201) throw new InvalidOperationException($"Inbox subfolder failed: {ics} {Trunc(icb)}");
        var inboxChild = Str(icj, "id"); ownedFolders[inboxChild] = 1;
        for (var i = 0; i < 3; i++) await Msg($"{Graph}{Me}/mailFolders/{inboxChild}/messages", $"Seed inbox child {i}", "child body", false, DateTime.UtcNow.AddDays(-i - 3), i % 2 == 0, false);
        Say("Well-known folders seeded: Inbox 5 (+ a subfolder with 3), Sent Items 3, Drafts 1, Deleted Items 2, Junk 2 (recorded for cleanup).");
    }

    // ---- contacts and calendar (M3) ----
    if (pimMode)
    {
        void RecordPim(string kind, string id) { lock (seedsDir) File.AppendAllText(Path.Combine(seedsDir, $"pim-{stamp}.ids"), $"{kind}|{id}{Environment.NewLine}"); }
        var contacts = new object[]
        {
            new { givenName = "Seed", surname = "Contact One", displayName = $"Seed Contact One {stamp}", emailAddresses = new[] { new { address = "seed.one@example.invalid", name = "Seed One" } }, businessPhones = new[] { "+1 555 0101" }, mobilePhone = "+1 555 0102", companyName = "Seed Co", jobTitle = "Tester", categories = new[] { "Blue category" } },
            new { givenName = "Seed", surname = "Contact Two", displayName = $"Seed Contact Two {stamp}", emailAddresses = new[] { new { address = "seed.two@example.invalid", name = "Two A" }, new { address = "seed.two.alt@example.invalid", name = "Two B" } }, homePhones = new[] { "+1 555 0201" }, birthday = "1985-06-15T00:00:00Z", personalNotes = "seed note two", businessAddress = new { street = "1 Seed Way", city = "Testville", state = "TS", postalCode = "00001", countryOrRegion = "Nowhere" } },
            new { givenName = "Ünïcödé", surname = "Seed 'Three' & Co.", displayName = $"Ünïcödé Seed 'Three' & Co. {stamp}", emailAddresses = new[] { new { address = "seed.three@example.invalid", name = "Three" } }, imAddresses = new[] { "seed3@example.invalid" }, department = "QA", officeLocation = "Room 3" },
            new { givenName = "Seed", surname = "Contact Four", displayName = $"Seed Contact Four {stamp}", emailAddresses = Array.Empty<object>(), nickName = "Four", spouseName = "Seed Spouse" },
            new { givenName = "Seed", surname = "Contact Five", displayName = $"Seed Contact Five {stamp}", emailAddresses = new[] { new { address = "seed.five@example.invalid", name = "Five" } }, businessHomePage = "https://example.invalid/five", profession = "Seeder" },
        };
        foreach (var ct in contacts)
        {
            var (st, j, b) = await G(HttpMethod.Post, $"{Graph}{Me}/contacts", ct);
            if (st != 201) throw new InvalidOperationException($"Contact create failed: {st} {Trunc(b)}");
            var id = Str(j, "id"); ownedMessages[id] = 1; RecordPim("contact", id);
        }
        var day = DateTime.UtcNow.Date.AddDays(7);
        string T(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss");
        object Tm(DateTime d) => new { dateTime = T(d), timeZone = "UTC" };
        var events = new object[]
        {
            new { subject = $"Seed event single {stamp}", body = new { contentType = "text", content = "single event body" }, start = Tm(day.AddHours(14)), end = Tm(day.AddHours(15)), location = new { displayName = "Seed Room A" }, reminderMinutesBeforeStart = 30, categories = new[] { "Green category" } },
            new { subject = $"Seed event all day {stamp}", body = new { contentType = "text", content = "all day body" }, start = Tm(day.AddDays(1)), end = Tm(day.AddDays(2)), isAllDay = true, showAs = "free" },
            new { subject = $"Seed event weekly series {stamp}", body = new { contentType = "text", content = "recurring body" }, start = Tm(day.AddDays(2).AddHours(9)), end = Tm(day.AddDays(2).AddHours(10)),
                  recurrence = new { pattern = new { type = "weekly", interval = 1, daysOfWeek = new[] { day.AddDays(2).DayOfWeek.ToString().ToLowerInvariant() } }, range = new { type = "numbered", startDate = day.AddDays(2).ToString("yyyy-MM-dd"), numberOfOccurrences = 4 } } },
            new { subject = $"Seed event private high importance {stamp}", body = new { contentType = "html", content = "<p>html <b>body</b></p>" }, start = Tm(day.AddDays(3).AddHours(11)), end = Tm(day.AddDays(3).AddHours(12)), sensitivity = "private", importance = "high", showAs = "tentative" },
            new { subject = $"Seed event with self as attendee {stamp}", body = new { contentType = "text", content = "attendee body" }, start = Tm(day.AddDays(4).AddHours(13)), end = Tm(day.AddDays(4).AddHours(14)), attendees = new[] { new { emailAddress = new { address = upn, name = "Seed Self" }, type = "required" } } },
        };
        foreach (var ev in events)
        {
            var (st, j, b) = await G(HttpMethod.Post, $"{Graph}{Me}/events", ev);
            if (st != 201) throw new InvalidOperationException($"Event create failed: {st} {Trunc(b)}");
            var id = Str(j, "id"); ownedMessages[id] = 1; RecordPim("event", id);
        }
        if (pimExtra)
        {
            var cal = await G(HttpMethod.Post, $"{Graph}{Me}/calendars", new { name = $"{Prefix}Calendar-{stamp}" });
            if (cal.Status != 201) throw new InvalidOperationException($"Calendar create failed: {cal.Status} {Trunc(cal.Body)}");
            var calId = Str(cal.Json, "id"); ownedFolders[calId] = 1; RecordPim("calendar", calId);
            var extraEvents = new object[]
            {
                new { subject = $"Seed extra single {stamp}", body = new { contentType = "text", content = "extra calendar single" }, start = Tm(day.AddHours(10)), end = Tm(day.AddHours(11)), location = new { displayName = "Seed Room B" } },
                new { subject = $"Seed extra all day {stamp}", body = new { contentType = "text", content = "extra all day" }, start = Tm(day.AddDays(1)), end = Tm(day.AddDays(2)), isAllDay = true },
                new { subject = $"Seed extra weekly series {stamp}", body = new { contentType = "text", content = "extra recurring" }, start = Tm(day.AddDays(3).AddHours(15)), end = Tm(day.AddDays(3).AddHours(16)),
                      recurrence = new { pattern = new { type = "weekly", interval = 1, daysOfWeek = new[] { day.AddDays(3).DayOfWeek.ToString().ToLowerInvariant() } }, range = new { type = "numbered", startDate = day.AddDays(3).ToString("yyyy-MM-dd"), numberOfOccurrences = 3 } } },
            };
            foreach (var ev in extraEvents)
            {
                var (es, _, eb) = await G(HttpMethod.Post, $"{Graph}{Me}/calendars/{calId}/events", ev);
                if (es != 201) throw new InvalidOperationException($"Event create in the extra calendar failed: {es} {Trunc(eb)}");
            }
            var cf = await G(HttpMethod.Post, $"{Graph}{Me}/contactFolders", new { displayName = $"{Prefix}Contacts-{stamp}" });
            if (cf.Status != 201) throw new InvalidOperationException($"Contact folder create failed: {cf.Status} {Trunc(cf.Body)}");
            var cfId = Str(cf.Json, "id"); ownedFolders[cfId] = 1; RecordPim("contactfolder", cfId);
            for (var i = 1; i <= 3; i++)
            {
                var (s1, _, b1) = await G(HttpMethod.Post, $"{Graph}{Me}/contactFolders/{cfId}/contacts", new { givenName = "Seed", surname = $"Extra Contact {i}", displayName = $"Seed Extra Contact {i} {stamp}", emailAddresses = new[] { new { address = $"seed.extra{i}@example.invalid", name = $"Extra {i}" } }, companyName = "Seed Co" });
                if (s1 != 201) throw new InvalidOperationException($"Contact create in the extra folder failed: {s1} {Trunc(b1)}");
            }
            var kid = await G(HttpMethod.Post, $"{Graph}{Me}/contactFolders/{cfId}/childFolders", new { displayName = $"{Prefix}ContactsChild-{stamp}" });
            if (kid.Status != 201) throw new InvalidOperationException($"Nested contact folder create failed: {kid.Status} {Trunc(kid.Body)}");
            var kidId = Str(kid.Json, "id"); ownedFolders[kidId] = 1;
            for (var i = 1; i <= 2; i++)
            {
                var (s2, _, b2) = await G(HttpMethod.Post, $"{Graph}{Me}/contactFolders/{kidId}/contacts", new { givenName = "Seed", surname = $"Nested Contact {i}", displayName = $"Seed Nested Contact {i} {stamp}", emailAddresses = new[] { new { address = $"seed.nested{i}@example.invalid", name = $"Nested {i}" } } });
                if (s2 != 201) throw new InvalidOperationException($"Contact create in the nested folder failed: {s2} {Trunc(b2)}");
            }
            Say("Additional calendar (3 events, one a weekly series) and contact folder (3 contacts, plus a nested folder with 2) seeded.");
        }
        Say("Contacts and calendar seeded: 5 contacts in the default Contacts folder, 5 events (one a 4-occurrence weekly series) in the default Calendar (recorded for cleanup).");
    }

    Say($"\nDone. {created} messages created, {failures} bulk failures, {throttles} throttle responses.");
    Say($"Expected for the copy tests: container '{container}' with 13 folders; Designed holds {9 + (volume ? 3 : 0)} messages; the {bulk - failures} bulk messages are spread over 8 folders; 1 folder is empty.");
    Say("Run `cleanup` when finished.");
    return failures > 0 ? 2 : 0;
}

static string Trunc(string s, int n = 300) => s.Length <= n ? s.Replace("\n", " ") : s[..n].Replace("\n", " ") + "…";
static string? Find(string start)
{
    for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
    { var p = Path.Combine(d.FullName, "smartmailboxcopy.config.json"); if (File.Exists(p)) return p; }
    return null;
}
