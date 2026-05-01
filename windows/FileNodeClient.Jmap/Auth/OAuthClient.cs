using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileNodeClient.Logging;

namespace FileNodeClient.Jmap.Auth;

/// <summary>
/// Stateless helper methods for OAuth operations:
/// dynamic client registration, PKCE, authorization URL, token exchange, refresh, revocation.
/// </summary>
public static class OAuthClient
{
    public const string FilesScope = "urn:ietf:params:jmap:core urn:ietf:params:oauth:scope:files";
    public const string ClientName = "FileNodeClient";
    public const string SoftwareId = "4a1c3d2e-8f7b-4e6a-9d5c-1b0a2e3f4d5c";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Dynamic client registration (RFC 7591).
    /// Server deduplicates by software_id, so repeated calls return the same client_id.
    /// </summary>
    public static async Task<OAuthClientRegistration> RegisterAsync(
        string registrationEndpoint, string[] redirectUris, string scope,
        CancellationToken ct = default)
    {
        using var http = new HttpClient();

        var payload = new Dictionary<string, object>
        {
            ["redirect_uris"] = redirectUris,
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["scope"] = scope,
            ["client_name"] = ClientName,
            ["client_uri"] = "https://www.fastmail.com/files/",
            ["logo_uri"] = "https://www.fastmail.com/assets/images/fm-logo.svg",
            ["tos_uri"] = "https://www.fastmail.com/about/tos/",
            ["policy_uri"] = "https://www.fastmail.com/about/privacy/",
            ["software_id"] = SoftwareId,
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync(registrationEndpoint, content, ct);
        await OAuthHelpers.EnsureSuccessAsync(response, "client registration");

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<OAuthClientRegistration>(responseJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse client registration response");
    }

    /// <summary>
    /// Generate PKCE code_verifier and code_challenge (S256).
    /// </summary>
    public static (string CodeVerifier, string CodeChallenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);

        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        return (codeVerifier, codeChallenge);
    }

    /// <summary>
    /// Build the authorization URL for the browser redirect.
    /// `resource` (RFC 8707) is required by the Fastmail OAuth server for
    /// dynamically-registered clients; pass the JMAP session URL here.
    /// </summary>
    public static string BuildAuthorizationUrl(
        string authorizationEndpoint, string clientId,
        string redirectUri, string scope,
        string codeChallenge, string state,
        string resource)
    {
        var query = string.Join("&",
            $"response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            $"code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}",
            $"resource={Uri.EscapeDataString(resource)}");

        return $"{authorizationEndpoint}?{query}";
    }

    /// <summary>
    /// Exchange an authorization code for tokens.
    /// </summary>
    public static async Task<OAuthTokenResponse> ExchangeCodeAsync(
        string tokenEndpoint, string clientId,
        string code, string redirectUri,
        string codeVerifier, CancellationToken ct = default)
    {
        using var http = new HttpClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        });

        var response = await http.PostAsync(tokenEndpoint, form, ct);
        await OAuthHelpers.EnsureSuccessAsync(response, "token exchange");

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<OAuthTokenResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse token response");
    }

    /// <summary>
    /// Refresh an access token using a refresh token.
    /// </summary>
    public static async Task<OAuthTokenResponse> RefreshTokenAsync(
        string tokenEndpoint, string clientId,
        string refreshToken, CancellationToken ct = default)
    {
        using var http = new HttpClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        });

        var response = await http.PostAsync(tokenEndpoint, form, ct);
        await OAuthHelpers.EnsureSuccessAsync(response, "token refresh");

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<OAuthTokenResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse refresh token response");
    }

    /// <summary>
    /// Revoke a token (access or refresh) at the revocation endpoint.
    /// </summary>
    public static async Task RevokeTokenAsync(
        string revocationEndpoint, string clientId,
        string token, CancellationToken ct = default)
    {
        using var http = new HttpClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["token"] = token,
        });

        var response = await http.PostAsync(revocationEndpoint, form, ct);
        // Revocation is best-effort; don't throw on failure
        if (!response.IsSuccessStatusCode)
            Log.Warn($"[OAuth] Token revocation returned {(int)response.StatusCode}");
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
