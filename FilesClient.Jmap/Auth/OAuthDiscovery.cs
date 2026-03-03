using System.Text.Json;

namespace FilesClient.Jmap.Auth;

/// <summary>
/// Implements the OAuth discovery chain:
/// 1. .well-known/jmap → session URL + origin
/// 2. {origin}/.well-known/oauth-protected-resource → authorization server
/// 3. {issuer}/.well-known/oauth-authorization-server → full metadata
/// </summary>
public static class OAuthDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Override host for .well-known/jmap lookup during testing.
    /// Set to null to use the email domain directly (production behavior).
    /// </summary>
    public static string? DiscoveryHostOverride = "dogfoodapi.fastmail.com";

    /// <summary>
    /// Discover OAuth metadata from an email domain.
    /// Returns the session URL (from .well-known/jmap redirect target) and OAuth server metadata.
    /// </summary>
    public static async Task<(string SessionUrl, OAuthServerMetadata Metadata)> DiscoverAsync(
        string emailDomain, CancellationToken ct = default)
    {
        // Step 1: .well-known/jmap — follow redirects to get session URL
        // fastmaildev.com users go through their own API; everyone else uses the override if set
        var discoveryHost = emailDomain == "fastmaildev.com"
            ? "api.fastmaildev.com"
            : DiscoveryHostOverride ?? emailDomain;
        var jmapUrl = $"https://{discoveryHost}/.well-known/jmap";

        // Use a handler that follows redirects so we get the final URL
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var http = new HttpClient(handler);

        HttpResponseMessage jmapResponse;
        try
        {
            jmapResponse = await http.GetAsync(jmapUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Failed to reach {jmapUrl}: {ex.Message}", ex);
        }

        // We need the final URL (after redirects), not the response body.
        // A 401 is fine — it means we found the server but need auth.
        var finalUrl = jmapResponse.RequestMessage?.RequestUri?.ToString()
            ?? throw new InvalidOperationException("Could not determine JMAP session URL from redirect");

        var sessionUrl = finalUrl;
        var origin = new Uri(finalUrl).GetLeftPart(UriPartial.Authority);

        // Step 2: .well-known/oauth-protected-resource
        var resourceUrl = $"{origin}/.well-known/oauth-protected-resource";
        var resourceResponse = await http.GetAsync(resourceUrl, ct);
        await OAuthHelpers.EnsureSuccessAsync(resourceResponse, "OAuth protected resource");

        var resourceJson = await resourceResponse.Content.ReadAsStringAsync(ct);
        var resource = JsonSerializer.Deserialize<OAuthProtectedResource>(resourceJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse OAuth protected resource response");

        if (resource.AuthorizationServers == null || resource.AuthorizationServers.Count == 0)
            throw new InvalidOperationException("No authorization servers found in protected resource response");

        var issuer = resource.AuthorizationServers[0];

        // Step 3: .well-known/oauth-authorization-server
        var issuerUri = new Uri(issuer);
        var metadataUrl = $"{issuerUri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-authorization-server{issuerUri.AbsolutePath.TrimEnd('/')}";
        // If the issuer path is just "/", the URL is simply {origin}/.well-known/oauth-authorization-server
        if (issuerUri.AbsolutePath == "/")
            metadataUrl = $"{issuerUri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-authorization-server";

        var metadataResponse = await http.GetAsync(metadataUrl, ct);
        await OAuthHelpers.EnsureSuccessAsync(metadataResponse, "OAuth authorization server metadata");

        var metadataJson = await metadataResponse.Content.ReadAsStringAsync(ct);
        var metadata = JsonSerializer.Deserialize<OAuthServerMetadata>(metadataJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse OAuth authorization server metadata");

        // Validate issuer matches
        if (metadata.Issuer != issuer)
            throw new InvalidOperationException(
                $"Issuer mismatch: expected {issuer}, got {metadata.Issuer}");

        return (sessionUrl, metadata);
    }
}
