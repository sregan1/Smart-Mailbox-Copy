using System.Text.Json;

namespace SmartMailboxCopy;

/// <summary>
/// What kind of mailbox an account most likely has. Graph does not say outright: shared, room and equipment mailboxes are
/// ordinary directory users without a license, so they can only be told apart from a licensed user, not from each other.
/// </summary>
public enum MailboxKind { User, UnlicensedResource, Guest }

public sealed record DirectoryMailbox(string Id, string DisplayName, string Upn, string Mail, MailboxKind Kind, bool Enabled)
{
    public string Domain => Upn.Contains('@') ? Upn[(Upn.IndexOf('@') + 1)..] : "";
    public string KindText => Kind switch { MailboxKind.User => "User", MailboxKind.UnlicensedResource => "Shared, room or equipment (no license)", _ => "Guest" };
}

/// <summary>Which mailboxes to include. Empty sets and empty text mean "no restriction".</summary>
public sealed class MailboxFilter
{
    public HashSet<MailboxKind> Kinds { get; init; } = new() { MailboxKind.User };
    public string Domain { get; init; } = "";
    public string Contains { get; init; } = "";
    public bool EnabledOnly { get; init; }
    /// <summary>Addresses to leave out (for example accounts already migrated).</summary>
    public HashSet<string> Exclude { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Matches(DirectoryMailbox m) =>
        (Kinds.Count == 0 || Kinds.Contains(m.Kind))
        && (Domain == "" || string.Equals(m.Domain, Domain.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
        && (Contains == "" || m.Upn.Contains(Contains, StringComparison.OrdinalIgnoreCase) || m.DisplayName.Contains(Contains, StringComparison.OrdinalIgnoreCase))
        && (!EnabledOnly || m.Enabled)
        && !Exclude.Contains(m.Upn);
}

/// <summary>Lists the mailboxes of a tenant (read only) so a migration can be scoped to some or all of them.</summary>
public sealed class MailboxDirectory
{
    private const string G = GraphHttp.Graph;
    private readonly GraphHttp _http; private readonly TenantSession _session;

    public MailboxDirectory(CopyConfig cfg, SessionRole role, bool appOnly, RunLog log)
    {
        _http = new GraphHttp(log);
        _session = new TenantSession(role, role == SessionRole.Source ? cfg.Source : cfg.Target, appOnly, log);
        _session.Allowed = (method, url) => method == HttpMethod.Get && url.StartsWith($"{G}/users?");   // the only thing this class may do
    }

    /// <summary>The application permissions this app registration has been granted (names only).</summary>
    public Task<List<string>> GetGrantedRolesAsync() => _session.GetGrantedRolesAsync();

    /// <summary>Every account that has a mail address (so a mailbox is plausible). Accounts without one are never returned.</summary>
    public async Task<List<DirectoryMailbox>> ListAsync()
    {
        var rows = await _http.GetAllAsync(_session, $"{G}/users?$select=id,displayName,userPrincipalName,mail,userType,accountEnabled,assignedLicenses&$top=999");
        var result = new List<DirectoryMailbox>();
        foreach (var u in rows)
        {
            string S(string n) => u.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var mail = S("mail");
            if (mail == "") continue;
            var guest = string.Equals(S("userType"), "Guest", StringComparison.OrdinalIgnoreCase);
            var licensed = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array && al.GetArrayLength() > 0;
            var enabled = !u.TryGetProperty("accountEnabled", out var en) || en.ValueKind != JsonValueKind.False;
            result.Add(new DirectoryMailbox(S("id"), S("displayName"), S("userPrincipalName"), mail,
                guest ? MailboxKind.Guest : licensed ? MailboxKind.User : MailboxKind.UnlicensedResource, enabled));
        }
        return result.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
