using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartMailboxCopy;

/// <summary>Guarded Microsoft Graph HTTP: every authenticated request must pass the session's guard; throttling is retried.</summary>
public sealed class GraphHttp
{
    public const string Graph = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly RunLog _log;
    private int _throttles;

    public GraphHttp(RunLog log) => _log = log;

    public int Throttles => _throttles;

    /// <param name="session">The session whose token and guard apply; null for the pre-authenticated import URL.</param>
    public async Task<(int Status, string Body)> SendAsync(TenantSession? session, HttpMethod method, string url, HttpContent? content = null)
    {
        if (session != null && !(session.Allowed?.Invoke(method, url) ?? false))
            throw new InvalidOperationException($"Refusing [{session.Role}] {method} {url}");

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url) { Content = content == null ? null : await CloneAsync(content) };
            if (session != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.GetTokenAsync());
            var side = session?.Role.ToString() ?? "Import";
            var path = RunLog.RedactUrl(session == null ? new Uri(url).GetLeftPart(UriPartial.Path) : url.Replace(Graph, ""));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req); }
            catch (Exception ex) { _log.Error($"[{side}] {method} {path} failed before any response (attempt {attempt + 1}): {ex.GetType().Name}: {ex.Message}", ex); throw; }
            using var _ = resp;
            var text = await resp.Content.ReadAsStringAsync();
            var code = (int)resp.StatusCode;
            _log.Debug($"[{side}] {method} {path} -> {code} in {clock.ElapsedMilliseconds} ms, {text.Length} bytes{(attempt > 0 ? $" (attempt {attempt + 1})" : "")}");
            if (code is 429 or 503 or 504 && attempt < 6)
            {
                Interlocked.Increment(ref _throttles);
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, 5 * (attempt + 1)));
                _log.Warn($"[{side}] {code} on {method} {path} — waiting {wait.TotalSeconds:0}s, then retrying (attempt {attempt + 1} of 7)");
                await Task.Delay(wait);
                continue;
            }
            if (code >= 400) _log.Warn($"[{side}] {method} {path} -> HTTP {code}: {Truncate(text, 300)}");
            return (code, text);
        }
    }

    public async Task<(int Status, JsonElement Json, string Body)> JsonAsync(TenantSession session, HttpMethod method, string url, object? body = null)
    {
        var content = body == null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var (status, text) = await SendAsync(session, method, url, content);
        JsonElement el = default;
        try { if (text.Length > 0) el = JsonDocument.Parse(text).RootElement.Clone(); } catch { }
        return (status, el, text);
    }

    /// <summary>GET with paging: follows @odata.nextLink and returns every element of "value".</summary>
    public async Task<List<JsonElement>> GetAllAsync(TenantSession session, string url)
    {
        var all = new List<JsonElement>();
        string? next = url;
        while (next != null)
        {
            var (status, json, body) = await JsonAsync(session, HttpMethod.Get, next);
            if (status != 200) throw new InvalidOperationException($"GET failed ({status}): {Truncate(body)}");
            foreach (var v in json.GetProperty("value").EnumerateArray()) all.Add(v);
            next = json.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
        return all;
    }

    public static string Truncate(string s, int n = 400) => s.Length <= n ? s.Replace("\n", " ") : s[..n].Replace("\n", " ") + "…";

    private static async Task<HttpContent> CloneAsync(HttpContent c)
    {
        var copy = new ByteArrayContent(await c.ReadAsByteArrayAsync());
        foreach (var h in c.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return copy;
    }
}
