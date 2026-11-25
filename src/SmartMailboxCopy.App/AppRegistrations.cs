using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartMailboxCopy.App.Localization;

namespace SmartMailboxCopy.App;

/// <summary>
/// A saved Microsoft Entra app registration: a name you recognize, plus the tenant and application (client) ID.
/// The optional client secret is only for app-only runs (copying many mailboxes without anyone signing in).
/// It is encrypted for the current Windows user (DPAPI), never stored as text, and cannot be read on another PC or account.
/// </summary>
public sealed class AppRegistration : INotifyPropertyChanged
{
    private string _name = "", _tenantId = "", _clientId = "";
    public string Name { get => _name; set { _name = value; Changed(); Changed(nameof(DisplayName)); } }
    public string TenantId { get => _tenantId; set { _tenantId = value; Changed(); } }
    public string ClientId { get => _clientId; set { _clientId = value; Changed(); } }
    public string ProtectedClientSecret { get; set; } = "";

    [JsonIgnore] public bool HasClientSecret => ProtectedClientSecret.Length > 0;
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Loc.T("Reg_Unnamed") : Name;

    public void SetClientSecret(string? secret)
    {
        ProtectedClientSecret = string.IsNullOrEmpty(secret) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
        Changed(nameof(HasClientSecret));
    }

    /// <summary>Null when there is none, or it cannot be decrypted (copied from another account or PC): the user enters it again.</summary>
    public string? GetClientSecret()
    {
        if (ProtectedClientSecret.Length == 0) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedClientSecret), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>The saved registrations, kept per Windows user in %APPDATA%\SmartMailboxCopy\registrations.json (outside the project, so never committed).</summary>
public sealed class AppRegistrationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartMailboxCopy", "registrations.json");

    public List<AppRegistration> Registrations { get; set; } = new();
    /// <summary>Mailboxes the tool must never write to, even if they are mapped as a target by mistake.</summary>
    public List<string> ProtectedMailboxes { get; set; } = new();
    public string LastContainer { get; set; } = "";
    /// <summary>Additional calendars and contact folders to leave out by name (for example a subscribed holiday calendar).</summary>
    public List<string> LeaveOutCalendars { get; set; } = new();
    /// <summary>Display language as a culture code such as de-DE; empty follows the Windows display language. Applied at startup.</summary>
    public string Language { get; set; } = "";
    /// <summary>System, Light or Dark.</summary>
    public string Theme { get; set; } = "System";

    public static AppRegistrationStore Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppRegistrationStore>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch { try { File.Copy(FilePath, FilePath + ".corrupt", true); } catch { } }   // keep the evidence; the next save would overwrite it
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));   // write then move: a crash cannot leave a half-written file
        File.Move(tmp, FilePath, true);
    }

    public AppRegistration? Find(string tenantId, string clientId) =>
        Registrations.FirstOrDefault(r => string.Equals(r.TenantId.Trim(), tenantId.Trim(), StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(r.ClientId.Trim(), clientId.Trim(), StringComparison.OrdinalIgnoreCase));
}
