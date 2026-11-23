using System.Text.Json;

namespace SmartMailboxCopy;

/// <summary>One side (source or target) of a copy: which tenant, which app registration, which account.</summary>
public sealed record SideConfig(string Tenant, string ClientId, string Upn, string AppClientId, string ClientSecret);

public sealed record CopyConfig(SideConfig Source, SideConfig Target, IReadOnlyList<string>? Protected = null)
{
    /// <summary>True when the address is listed in the config's protectedMailboxes: a real mailbox that must never be written to.</summary>
    public bool IsProtected(string upn) => Protected?.Any(p => string.Equals(p, upn, StringComparison.OrdinalIgnoreCase)) == true;

    public const string FileName = "smartmailboxcopy.config.json";

    /// <summary>Looks for the config file in the given folder and each parent folder.</summary>
    public static string? Find(string startDir)
    {
        var d = new DirectoryInfo(startDir);
        while (d != null)
        {
            var p = Path.Combine(d.FullName, FileName);
            if (File.Exists(p)) return p;
            d = d.Parent;
        }
        return null;
    }

    /// <param name="appOnly">Application permissions with a client secret. The app's client ID is sourceAppClientId / targetAppClientId when given, otherwise the same sourceClientId / targetClientId.</param>
    /// <param name="needAccounts">False for batch runs, where the mailbox addresses come from the mapping file instead of the config.</param>
    public static CopyConfig Load(string path, bool appOnly, bool needAccounts = true)
    {
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        string C(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var required = new List<string> { "sourceTenant", "sourceClientId", "targetTenant", "targetClientId" };
        if (needAccounts) required.AddRange(new[] { "sourceUpn", "targetUpn" });
        if (appOnly) required.AddRange(new[] { "sourceClientSecret", "targetClientSecret" });
        foreach (var name in required)
            if (C(name) == "" || C(name).StartsWith('<'))
                throw new InvalidOperationException($"{FileName}: '{name}' is not filled in.");

        string App(string side) => C(side + "AppClientId") is { Length: > 0 } a && !a.StartsWith('<') ? a : C(side + "ClientId");
        var prot = root.TryGetProperty("protectedMailboxes", out var pm) && pm.ValueKind == JsonValueKind.Array
            ? pm.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "" && !x.StartsWith('<')).ToList() : new List<string>();
        return new CopyConfig(
            new SideConfig(C("sourceTenant"), C("sourceClientId"), C("sourceUpn"), App("source"), C("sourceClientSecret")),
            new SideConfig(C("targetTenant"), C("targetClientId"), C("targetUpn"), App("target"), C("targetClientSecret")),
            prot);
    }
}
