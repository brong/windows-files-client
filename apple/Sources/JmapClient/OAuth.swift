import Foundation
#if canImport(CryptoKit)
import CryptoKit
#endif

// MARK: - Discovery Models

/// Response from `{domain}/.well-known/user-agent-configuration`
struct UserAgentConfiguration: Codable {
    let protocols: UAProtocols?
    let authentication: UAAuthentication?

    struct UAProtocols: Codable {
        let jmap: UAJmap?
    }
    struct UAJmap: Codable {
        let url: String
    }
    struct UAAuthentication: Codable {
        let oauthPublic: UAOAuthPublic?

        enum CodingKeys: String, CodingKey {
            case oauthPublic = "oauth-public"
        }
    }
    struct UAOAuthPublic: Codable {
        let issuer: String
    }
}

/// Response from `{issuer}/.well-known/oauth-authorization-server`
public struct OAuthServerMetadata: Codable, Sendable {
    public let issuer: String
    public let authorizationEndpoint: String
    public let tokenEndpoint: String
    public let registrationEndpoint: String?
    public let revocationEndpoint: String?
    public let scopesSupported: [String]?
    public let grantTypesSupported: [String]?
    public let codeChallengeMethodsSupported: [String]?

    enum CodingKeys: String, CodingKey {
        case issuer
        case authorizationEndpoint = "authorization_endpoint"
        case tokenEndpoint = "token_endpoint"
        case registrationEndpoint = "registration_endpoint"
        case revocationEndpoint = "revocation_endpoint"
        case scopesSupported = "scopes_supported"
        case grantTypesSupported = "grant_types_supported"
        case codeChallengeMethodsSupported = "code_challenge_methods_supported"
    }
}

/// Response from dynamic client registration
public struct OAuthClientRegistration: Codable, Sendable {
    public let clientId: String

    enum CodingKeys: String, CodingKey {
        case clientId = "client_id"
    }
}

/// Response from the token endpoint
public struct OAuthTokenResponse: Codable, Sendable {
    public let accessToken: String
    public let tokenType: String
    public let expiresIn: Int
    public let refreshToken: String?
    public let scope: String?

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case tokenType = "token_type"
        case expiresIn = "expires_in"
        case refreshToken = "refresh_token"
        case scope
    }
}

/// Full credential bundle after OAuth completes
public struct OAuthCredential: Codable, Sendable {
    public let sessionUrl: String
    public let accessToken: String
    public let refreshToken: String
    public let tokenEndpoint: String
    public let clientId: String
    public let expiresAt: Date

    public init(sessionUrl: String, accessToken: String, refreshToken: String,
                tokenEndpoint: String, clientId: String, expiresAt: Date) {
        self.sessionUrl = sessionUrl
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.tokenEndpoint = tokenEndpoint
        self.clientId = clientId
        self.expiresAt = expiresAt
    }
}

// MARK: - OAuth Constants

public enum OAuthConstants {
    public static let filesScope = "urn:ietf:params:jmap:core urn:ietf:params:oauth:scope:files"
    public static let clientName = "FastmailFiles"
    public static let softwareId = "4a1c3d2e-8f7b-4e6a-9d5c-1b0a2e3f4d5c"
    public static let discoveryURL = "https://fastmail.com/.well-known/user-agent-configuration"
}

// MARK: - OAuth Discovery

/// Discover JMAP session URL and OAuth metadata from Fastmail.
public func oauthDiscover() async throws -> (sessionUrl: String, metadata: OAuthServerMetadata) {
    guard let configURL = URL(string: OAuthConstants.discoveryURL) else {
        throw JmapError.invalidResponse
    }

    let (configData, _) = try await URLSession.shared.data(from: configURL)
    let config = try JSONDecoder().decode(UserAgentConfiguration.self, from: configData)

    guard let sessionUrl = config.protocols?.jmap?.url else {
        throw JmapError.serverError("discovery", "No JMAP session URL in user-agent configuration")
    }
    guard var issuer = config.authentication?.oauthPublic?.issuer else {
        throw JmapError.serverError("discovery", "No OAuth issuer in user-agent configuration")
    }

    // Ensure issuer has a scheme
    if !issuer.contains("://") {
        issuer = "https://\(issuer)"
    }

    // Fetch OAuth authorization server metadata
    guard let issuerURL = URL(string: issuer) else {
        throw JmapError.invalidResponse
    }
    let metadataPath = issuerURL.path == "/" || issuerURL.path.isEmpty
        ? "/.well-known/oauth-authorization-server"
        : "/.well-known/oauth-authorization-server\(issuerURL.path)"

    guard var metadataComponents = URLComponents(url: issuerURL, resolvingAgainstBaseURL: false) else {
        throw JmapError.invalidResponse
    }
    metadataComponents.path = metadataPath
    guard let metadataURL = metadataComponents.url else {
        throw JmapError.invalidResponse
    }

    let (metaData, _) = try await URLSession.shared.data(from: metadataURL)
    let metadata = try JSONDecoder().decode(OAuthServerMetadata.self, from: metaData)

    // Validate issuer matches
    guard metadata.issuer == issuer else {
        throw JmapError.serverError("discovery",
            "Issuer mismatch: expected \(issuer), got \(metadata.issuer)")
    }

    return (sessionUrl, metadata)
}

// MARK: - PKCE

public struct PKCEChallenge: Sendable {
    public let codeVerifier: String
    public let codeChallenge: String

    public init() {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        let verifier = Self.base64URLEncode(bytes)
        self.codeVerifier = verifier

        let verifierData = Data(verifier.utf8)
        let hash = SHA256.hash(data: verifierData)
        self.codeChallenge = Self.base64URLEncode(Array(hash))
    }

    private static func base64URLEncode(_ data: [UInt8]) -> String {
        Data(data).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

// MARK: - Dynamic Client Registration

/// Register a client dynamically (RFC 7591). Server deduplicates by software_id.
public func oauthRegisterClient(
    registrationEndpoint: String,
    redirectURI: String
) async throws -> OAuthClientRegistration {
    guard let url = URL(string: registrationEndpoint) else {
        throw JmapError.invalidResponse
    }

    let payload: [String: Any] = [
        "redirect_uris": [redirectURI],
        "token_endpoint_auth_method": "none",
        "grant_types": ["authorization_code", "refresh_token"],
        "response_types": ["code"],
        "scope": OAuthConstants.filesScope,
        "client_name": OAuthConstants.clientName,
        "client_uri": "https://www.fastmail.com/files/",
        "logo_uri": "https://www.fastmail.com/assets/images/fm-logo.svg",
        "tos_uri": "https://www.fastmail.com/about/tos/",
        "policy_uri": "https://www.fastmail.com/about/privacy/",
        "software_id": OAuthConstants.softwareId,
    ]

    var request = URLRequest(url: url)
    request.httpMethod = "POST"
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    request.httpBody = try JSONSerialization.data(withJSONObject: payload)

    let (data, response) = try await URLSession.shared.data(for: request)
    guard let httpResponse = response as? HTTPURLResponse,
          (200...299).contains(httpResponse.statusCode) else {
        throw JmapError.serverError("registration", "Client registration failed")
    }

    return try JSONDecoder().decode(OAuthClientRegistration.self, from: data)
}

// MARK: - Authorization URL

/// Build the authorization URL to open in the browser.
public func oauthAuthorizationURL(
    authorizationEndpoint: String,
    clientId: String,
    redirectURI: String,
    codeChallenge: String,
    state: String
) -> URL? {
    var components = URLComponents(string: authorizationEndpoint)
    components?.queryItems = [
        URLQueryItem(name: "response_type", value: "code"),
        URLQueryItem(name: "client_id", value: clientId),
        URLQueryItem(name: "redirect_uri", value: redirectURI),
        URLQueryItem(name: "scope", value: OAuthConstants.filesScope),
        URLQueryItem(name: "code_challenge", value: codeChallenge),
        URLQueryItem(name: "code_challenge_method", value: "S256"),
        URLQueryItem(name: "state", value: state),
    ]
    return components?.url
}

// MARK: - Token Exchange

/// Exchange an authorization code for tokens.
public func oauthExchangeCode(
    tokenEndpoint: String,
    clientId: String,
    code: String,
    redirectURI: String,
    codeVerifier: String
) async throws -> OAuthTokenResponse {
    guard let url = URL(string: tokenEndpoint) else {
        throw JmapError.invalidResponse
    }

    let body = [
        "grant_type=authorization_code",
        "client_id=\(urlEncode(clientId))",
        "code=\(urlEncode(code))",
        "redirect_uri=\(urlEncode(redirectURI))",
        "code_verifier=\(urlEncode(codeVerifier))",
    ].joined(separator: "&")

    var request = URLRequest(url: url)
    request.httpMethod = "POST"
    request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
    request.httpBody = Data(body.utf8)

    let (data, response) = try await URLSession.shared.data(for: request)
    guard let httpResponse = response as? HTTPURLResponse,
          (200...299).contains(httpResponse.statusCode) else {
        let errorBody = String(data: data, encoding: .utf8) ?? ""
        throw JmapError.serverError("token_exchange", "Token exchange failed: \(errorBody)")
    }

    return try JSONDecoder().decode(OAuthTokenResponse.self, from: data)
}

// MARK: - Token Refresh

/// Refresh an access token using a refresh token.
public func oauthRefreshToken(
    tokenEndpoint: String,
    clientId: String,
    refreshToken: String
) async throws -> OAuthTokenResponse {
    guard let url = URL(string: tokenEndpoint) else {
        throw JmapError.invalidResponse
    }

    let body = [
        "grant_type=refresh_token",
        "client_id=\(urlEncode(clientId))",
        "refresh_token=\(urlEncode(refreshToken))",
    ].joined(separator: "&")

    var request = URLRequest(url: url)
    request.httpMethod = "POST"
    request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
    request.httpBody = Data(body.utf8)

    let (data, response) = try await URLSession.shared.data(for: request)
    guard let httpResponse = response as? HTTPURLResponse,
          (200...299).contains(httpResponse.statusCode) else {
        throw JmapError.unauthorized
    }

    return try JSONDecoder().decode(OAuthTokenResponse.self, from: data)
}

// MARK: - Refreshing Token Provider

/// A TokenProvider that automatically refreshes OAuth tokens.
public actor OAuthTokenProvider: TokenProvider {
    private var accessToken: String
    private var refreshToken: String
    private let sessionUrl: String
    private let tokenEndpoint: String
    private let clientId: String
    private var expiresAt: Date
    private let refreshMargin: TimeInterval = 60 // refresh 60s before expiry
    private var onTokenRefreshed: (@Sendable (OAuthCredential) -> Void)?

    public init(credential: OAuthCredential,
                onTokenRefreshed: (@Sendable (OAuthCredential) -> Void)? = nil) {
        self.accessToken = credential.accessToken
        self.refreshToken = credential.refreshToken
        self.sessionUrl = credential.sessionUrl
        self.tokenEndpoint = credential.tokenEndpoint
        self.clientId = credential.clientId
        self.expiresAt = credential.expiresAt
        self.onTokenRefreshed = onTokenRefreshed
    }

    public func currentToken() async throws -> String {
        // Proactively refresh if close to expiry
        if Date().addingTimeInterval(refreshMargin) >= expiresAt {
            try await refresh()
        }
        return accessToken
    }

    /// Force a refresh (e.g., after 401).
    public func refresh() async throws {
        let response = try await oauthRefreshToken(
            tokenEndpoint: tokenEndpoint,
            clientId: clientId,
            refreshToken: refreshToken
        )

        accessToken = response.accessToken
        if let newRefresh = response.refreshToken {
            refreshToken = newRefresh
        }
        expiresAt = Date().addingTimeInterval(TimeInterval(response.expiresIn))

        // Notify caller so they can persist the new tokens
        let credential = OAuthCredential(
            sessionUrl: sessionUrl,
            accessToken: accessToken,
            refreshToken: refreshToken,
            tokenEndpoint: tokenEndpoint,
            clientId: clientId,
            expiresAt: expiresAt
        )
        onTokenRefreshed?(credential)
    }
}

// MARK: - Helpers

private func urlEncode(_ string: String) -> String {
    string.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? string
}
