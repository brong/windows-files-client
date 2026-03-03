using System.Text.Json.Serialization;

namespace FilesClient.Jmap.Auth;

/// <summary>
/// Response from {domain}/.well-known/user-agent-configuration
/// (draft-ietf-mailmaint-pacc)
/// </summary>
public record UserAgentConfiguration(
    [property: JsonPropertyName("protocols")]
    UserAgentProtocols? Protocols,

    [property: JsonPropertyName("authentication")]
    UserAgentAuthentication? Authentication);

public record UserAgentProtocols(
    [property: JsonPropertyName("jmap")]
    UserAgentJmapConfig? Jmap);

public record UserAgentJmapConfig(
    [property: JsonPropertyName("url")]
    string Url);

public record UserAgentAuthentication(
    [property: JsonPropertyName("oauth-public")]
    UserAgentOAuthPublic? OAuthPublic);

public record UserAgentOAuthPublic(
    [property: JsonPropertyName("issuer")]
    string Issuer);

/// <summary>
/// Response from {issuer}/.well-known/oauth-authorization-server
/// </summary>
public record OAuthServerMetadata(
    [property: JsonPropertyName("issuer")]
    string Issuer,

    [property: JsonPropertyName("authorization_endpoint")]
    string AuthorizationEndpoint,

    [property: JsonPropertyName("token_endpoint")]
    string TokenEndpoint,

    [property: JsonPropertyName("registration_endpoint")]
    string? RegistrationEndpoint,

    [property: JsonPropertyName("revocation_endpoint")]
    string? RevocationEndpoint,

    [property: JsonPropertyName("scopes_supported")]
    List<string>? ScopesSupported,

    [property: JsonPropertyName("response_types_supported")]
    List<string>? ResponseTypesSupported,

    [property: JsonPropertyName("grant_types_supported")]
    List<string>? GrantTypesSupported,

    [property: JsonPropertyName("code_challenge_methods_supported")]
    List<string>? CodeChallengeMethodsSupported);

/// <summary>
/// Response from dynamic client registration endpoint.
/// </summary>
public record OAuthClientRegistration(
    [property: JsonPropertyName("client_id")]
    string ClientId,

    [property: JsonPropertyName("client_id_issued_at")]
    long? ClientIdIssuedAt);

/// <summary>
/// Response from the token endpoint.
/// </summary>
public record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")]
    string AccessToken,

    [property: JsonPropertyName("token_type")]
    string TokenType,

    [property: JsonPropertyName("expires_in")]
    int ExpiresIn,

    [property: JsonPropertyName("refresh_token")]
    string? RefreshToken,

    [property: JsonPropertyName("scope")]
    string? Scope);

/// <summary>
/// Credential bundle passed around after OAuth completes.
/// </summary>
public record OAuthCredential(
    string SessionUrl,
    string AccessToken,
    string RefreshToken,
    string TokenEndpoint,
    string ClientId,
    DateTimeOffset ExpiresAt);
