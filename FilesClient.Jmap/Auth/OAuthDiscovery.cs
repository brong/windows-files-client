using System.Text.Json;

namespace FilesClient.Jmap.Auth;

/// <summary>
/// Implements the OAuth discovery chain:
/// 1. {domain}/.well-known/user-agent-configuration → session URL + OAuth issuer
/// 2. {issuer}/.well-known/oauth-authorization-server → full metadata
/// </summary>
public static class OAuthDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Discover OAuth metadata from Fastmail.
    /// Returns the session URL and OAuth server metadata.
    /// </summary>
    public static async Task<(string SessionUrl, OAuthServerMetadata Metadata)> DiscoverAsync(
        CancellationToken ct = default)
    {
        using var http = new HttpClient();

        // Step 1: .well-known/user-agent-configuration → session URL + OAuth issuer
        // Always discover against fastmail.com for now
        var configUrl = "https://fastmail.com/.well-known/user-agent-configuration";

        HttpResponseMessage configResponse;
        try
        {
            configResponse = await http.GetAsync(configUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Failed to reach {configUrl}: {ex.Message}", ex);
        }

        await OAuthHelpers.EnsureSuccessAsync(configResponse, "user-agent configuration");

        var configJson = await configResponse.Content.ReadAsStringAsync(ct);
        var config = JsonSerializer.Deserialize<UserAgentConfiguration>(configJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse user-agent configuration response");

        var sessionUrl = config.Protocols?.Jmap?.Url
            ?? throw new InvalidOperationException("No JMAP session URL in user-agent configuration");
        var issuer = config.Authentication?.OAuthPublic?.Issuer
            ?? throw new InvalidOperationException("No OAuth issuer in user-agent configuration");

        // Ensure issuer has a scheme — server may return bare hostname
        if (!issuer.Contains("://"))
            issuer = $"https://{issuer}";

        // Step 2: {issuer}/.well-known/oauth-authorization-server → full metadata
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
            throw new InvalidOperationException(
                $"OAuth issuer is not a valid absolute URI: \"{issuer}\"");
        var metadataUrl = issuerUri.AbsolutePath == "/"
            ? $"{issuerUri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-authorization-server"
            : $"{issuerUri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-authorization-server{issuerUri.AbsolutePath.TrimEnd('/')}";

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
