using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Security.Credentials;

namespace FilesClient.Service;

/// <summary>
/// Persists login credentials (token + session URL) in Windows Credential Manager
/// via the PasswordVault API.
/// </summary>
sealed class CredentialStore
{
    private const string ResourceName = "FastmailFiles";
    private const string DefaultSessionUrl = "https://api.fastmail.com/jmap/session";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public record StoredLogin(string LoginId, string Token, string SessionUrl, HashSet<string>? EnabledAccountIds);

    private record CredentialPayload(string Token, string SessionUrl, HashSet<string>? EnabledAccountIds = null);

    /// <summary>
    /// Save (or overwrite) a credential for the given loginId.
    /// </summary>
    public void Save(string loginId, string token, string sessionUrl, HashSet<string>? enabledAccountIds = null)
    {
        var vault = new PasswordVault();
        var payload = JsonSerializer.Serialize(
            new CredentialPayload(token, sessionUrl, enabledAccountIds), SerializerOptions);

        // Remove existing before adding (PasswordVault throws on duplicate)
        try
        {
            var existing = vault.Retrieve(ResourceName, loginId);
            vault.Remove(existing);
        }
        catch (Exception) { /* not found — ok */ }

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
        catch (Exception)
        {
            return result; // No credentials stored
        }

        foreach (var cred in creds)
        {
            try
            {
                cred.RetrievePassword();
                var payload = JsonSerializer.Deserialize<CredentialPayload>(cred.Password, SerializerOptions);
                if (payload != null)
                {
                    result.Add(new StoredLogin(
                        cred.UserName,
                        payload.Token,
                        payload.SessionUrl ?? DefaultSessionUrl,
                        payload.EnabledAccountIds));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load credential for {cred.UserName}: {ex.Message}");
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
        catch (Exception) { /* not found — ok */ }
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
