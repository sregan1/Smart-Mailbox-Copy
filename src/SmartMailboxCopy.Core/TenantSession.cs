using System.Text.Json;
using Microsoft.Identity.Client;

namespace SmartMailboxCopy;

public enum SessionRole { Source, Target }

/// <summary>
/// One signed-in side of the copy. Interactive (delegated) by default; with a client secret it uses application
/// (app-only) auth instead. Every request made through <see cref="GraphHttp"/> is checked against <see cref="Allowed"/>.
/// </summary>
public sealed class TenantSession
{
    // Least privilege per role: the source only ever reads and exports, the target only imports.
    private static readonly string[] SourceScopes = { "Mail.Read", "MailboxItem.Export", "MailboxItem.Read", "MailboxFolder.Read", "User.Read.All" };
    private static readonly string[] TargetScopes = { "Mail.ReadWrite", "MailboxItem.ImportExport", "MailboxItem.Read", "MailboxFolder.Read", "User.Read.All" };

    public SessionRole Role { get; }
    public string Upn { get; }
    public string Domain => Upn.Contains('@') ? Upn[(Upn.IndexOf('@') + 1)..] : Upn;
    public string MailboxId { get; set; } = "";
    public bool AppOnly => _capp != null;
    /// <summary>"/me" for a signed-in user, "/users/{address}" for app-only.</summary>
    public string Me => AppOnly ? $"/users/{Upn}" : "/me";
    public Func<HttpMethod, string, bool>? Allowed { get; set; }

    private readonly IPublicClientApplication? _app;
    private readonly IConfidentialClientApplication? _capp;
    private readonly string[] _scopes;
    /// <summary>Fixed on purpose: every sign-in gets 4 attempts of 12 seconds, each opening a fresh browser window.</summary>
    private const int SecondsPerAttempt = 12, Attempts = 4;
    private readonly RunLog _log;
    private IAccount? _account;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TenantSession(SessionRole role, SideConfig cfg, bool appOnly, RunLog log)
    {
        Role = role; Upn = cfg.Upn; _log = log;
        _scopes = (role == SessionRole.Source ? SourceScopes : TargetScopes).Select(s => "https://graph.microsoft.com/" + s).ToArray();
        var authority = $"https://login.microsoftonline.com/{cfg.Tenant}";
        if (appOnly)
            _capp = ConfidentialClientApplicationBuilder.Create(cfg.AppClientId).WithClientSecret(cfg.ClientSecret).WithAuthority(authority).Build();
        else
            _app = PublicClientApplicationBuilder.Create(cfg.ClientId).WithAuthority(authority).WithRedirectUri("http://localhost").Build();
    }

    /// <summary>
    /// The application permissions (app roles) the app has actually been granted, read from the "roles" claim of its app-only token. Names only; no secret
    /// is involved. Empty for an interactive session. A permission added moments ago can take a few minutes to appear.
    /// </summary>
    public async Task<List<string>> GetGrantedRolesAsync()
    {
        if (!AppOnly) return new();
        var parts = (await GetTokenAsync()).Split('.');
        if (parts.Length < 2) return new();
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            : new();
    }

    public async Task<string> GetTokenAsync()
    {
        if (_capp != null)   // app-only: the token comes from the client secret, no sign-in
            return (await _capp.AcquireTokenForClient(new[] { "https://graph.microsoft.com/.default" }).ExecuteAsync()).AccessToken;

        await _gate.WaitAsync();
        try
        {
            if (_account != null)
            {
                try { return (await _app!.AcquireTokenSilent(_scopes, _account).ExecuteAsync()).AccessToken; }
                catch (MsalUiRequiredException) { }
            }
            var who = Role == SessionRole.Source ? "SOURCE" : "TARGET";
            // Four attempts, each opening a fresh browser tab: the newest tab is the one that counts.
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                _log.Info($">>> SIGN IN NOW — {who}: use the account @{Domain} — attempt {attempt}/{Attempts}, waiting {SecondsPerAttempt}s (use the NEWEST window).");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(SecondsPerAttempt));
                try
                {
                    var r = await _app!.AcquireTokenInteractive(_scopes).WithLoginHint(Upn).ExecuteAsync(cts.Token);
                    if (!string.Equals(r.Account.Username, Upn, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"The {who} sign-in used a different account than the one configured. Stopping so nothing is read or written with the wrong account.");
                    _account = r.Account;
                    _log.Info($">>> signed in: {who} (@{Domain}).");
                    return r.AccessToken;
                }
                catch (OperationCanceledException) { _log.Warn($"no sign-in within {SecondsPerAttempt}s."); }
            }
            throw new TimeoutException($"{who} sign-in was not completed after {Attempts} attempts.");
        }
        finally { _gate.Release(); }
    }
}
