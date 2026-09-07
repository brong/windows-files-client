using System.Text.Json;
using System.Text.Json.Serialization;
using FileNodeClient.Logging;
using Windows.Security.Credentials;

namespace FileNodeClient.App;

/// <summary>
/// What we need to open a JMAP session: where, and a bearer token — plus, for
/// OAuth logins, enough to refresh that token.
/// </summary>
public sealed record LoginCredential(
    string SessionUrl, string Token,
    string? RefreshToken = null, string? TokenEndpoint = null,
    string? ClientId = null, long? ExpiresAtUnixSeconds = null)
{
    public bool IsOAuth => RefreshToken != null && TokenEndpoint != null && ClientId != null;
}

/// <summary>
/// Persists login credentials in Windows Credential Manager via the PasswordVault API.
/// </summary>
sealed class CredentialStore
{
    private const string ResourceName = "FileNodeClient";
    private const string DefaultSessionUrl = "https://api.fastmail.com/jmap/session";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public record StoredLogin(string LoginId, LoginCredential Credential, HashSet<string>? EnabledAccountIds);

    // On-disk shape (kept stable across versions).
    private record CredentialPayload(
        string Token, string? SessionUrl, HashSet<string>? EnabledAccountIds = null,
        string? RefreshToken = null, string? TokenEndpoint = null,
        string? ClientId = null, long? ExpiresAtUnixSeconds = null);

    /// <summary>
    /// Save (or overwrite) a credential for the given loginId.
    /// </summary>
    public void Save(string loginId, LoginCredential cred, HashSet<string>? enabledAccountIds)
    {
        var vault = new PasswordVault();
        var payload = JsonSerializer.Serialize(
            new CredentialPayload(cred.Token, cred.SessionUrl, enabledAccountIds,
                cred.RefreshToken, cred.TokenEndpoint, cred.ClientId, cred.ExpiresAtUnixSeconds), SerializerOptions);

        // Remove existing before adding (PasswordVault throws on duplicate)
        try
        {
            var existing = vault.Retrieve(ResourceName, loginId);
            vault.Remove(existing);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Element not found (E_ELEMENT_NOT_FOUND) — expected
        }
        catch (Exception ex)
        {
            Log.Warn($"CredentialStore: error removing existing credential for {loginId}: {ex.Message}");
        }

        vault.Add(new PasswordCredential(ResourceName, loginId, payload));
    }

    /// <summary>
    /// Load all stored logins.
    /// </summary>
    public List<StoredLogin> LoadAll()
    {
        var vault = new PasswordVault();
        var result = new List<StoredLogin>();

        IReadOnlyList<PasswordCredential> creds;
        try
        {
            creds = vault.FindAllByResource(ResourceName);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            return result; // No credentials stored
        }
        catch (Exception ex)
        {
            Log.Error($"CredentialStore: vault read failed: {ex.Message}");
            return result;
        }

        foreach (var cred in creds)
        {
            try
            {
                cred.RetrievePassword();
                var p = JsonSerializer.Deserialize<CredentialPayload>(cred.Password, SerializerOptions);
                if (p != null)
                {
                    result.Add(new StoredLogin(cred.UserName,
                        new LoginCredential(p.SessionUrl ?? DefaultSessionUrl, p.Token,
                            p.RefreshToken, p.TokenEndpoint, p.ClientId, p.ExpiresAtUnixSeconds),
                        p.EnabledAccountIds));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load credential for {cred.UserName}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Remove a stored credential by loginId.
    /// </summary>
    public void Remove(string loginId)
    {
        var vault = new PasswordVault();
        try
        {
            var cred = vault.Retrieve(ResourceName, loginId);
            vault.Remove(cred);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Element not found — already removed
        }
        catch (Exception ex)
        {
            Log.Warn($"CredentialStore: error removing credential for {loginId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Derive a stable loginId from a connected session's username and API host.
    /// </summary>
    public static string DeriveLoginId(string username, string sessionUrl)
    {
        var host = "fastmail.com";
        try { host = new Uri(sessionUrl).Host; } catch { }
        return $"{username}@{host}";
    }
}
