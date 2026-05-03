import Foundation

/// Errors from PACC server discovery.
public enum PaccError: LocalizedError, Sendable, Equatable {
    case invalidEmail
    case discoveryFailed(Int)   // HTTP status code
    case noJmapProtocol
    case noOAuthIssuer
    case issuerMismatch(expected: String, got: String)

    public var errorDescription: String? {
        switch self {
        case .invalidEmail:
            return "Invalid email address — enter name@domain.com"
        case .discoveryFailed(let code):
            return "Server configuration not found (HTTP \(code))"
        case .noJmapProtocol:
            return "Server does not advertise JMAP Files support"
        case .noOAuthIssuer:
            return "Server does not support OAuth sign-in"
        case .issuerMismatch(let expected, let got):
            return "OAuth issuer mismatch: expected \(expected), got \(got)"
        }
    }
}

/// Discovers JMAP session URL and OAuth server metadata for any email address
/// using draft-ietf-mailmaint-pacc-01 (User Agent Auto-Configuration).
///
/// For Fastmail: `ua-auto-config.fastmail.com/.well-known/user-agent-configuration.json`
/// Returns (sessionUrl, OAuthServerMetadata) for use in the OAuth PKCE flow.
public struct PaccDiscovery {

    /// Constructs the PACC discovery URL for an email address.
    /// Exported for testing; not normally called directly.
    public static func discoveryURL(for email: String) throws -> URL {
        let parts = email.split(separator: "@", maxSplits: 1)
        guard parts.count == 2, !parts[1].isEmpty else {
            throw PaccError.invalidEmail
        }
        let domain = String(parts[1])
        let urlString = "https://ua-auto-config.\(domain)/.well-known/user-agent-configuration.json"
        guard let url = URL(string: urlString) else {
            throw PaccError.invalidEmail
        }
        return url
    }

    /// Discovers JMAP and OAuth endpoints for the given email address.
    public static func discover(
        email: String,
        session: URLSession = .shared
    ) async throws -> (sessionUrl: String, metadata: OAuthServerMetadata) {
        let configURL = try discoveryURL(for: email)

        let (configData, configResponse) = try await session.data(from: configURL)
        guard let http = configResponse as? HTTPURLResponse, http.statusCode == 200 else {
            let code = (configResponse as? HTTPURLResponse)?.statusCode ?? 0
            throw PaccError.discoveryFailed(code)
        }

        let config = try JSONDecoder().decode(UserAgentConfiguration.self, from: configData)

        guard let sessionUrl = config.protocols?.jmap?.url else {
            throw PaccError.noJmapProtocol
        }
        guard var issuer = config.authentication?.oauthPublic?.issuer else {
            throw PaccError.noOAuthIssuer
        }
        if !issuer.contains("://") { issuer = "https://\(issuer)" }

        let metadata = try await fetchOAuthMetadata(issuer: issuer, session: session)
        guard metadata.issuer == issuer else {
            throw PaccError.issuerMismatch(expected: issuer, got: metadata.issuer)
        }

        return (sessionUrl, metadata)
    }

    /// Discovers OAuth metadata directly from a known issuer URL and session URL,
    /// bypassing PACC DNS lookup. Used when PACC discovery fails for a custom domain.
    public static func discoverFromIssuer(
        issuer: String,
        sessionUrl: String,
        session: URLSession = .shared
    ) async throws -> (sessionUrl: String, metadata: OAuthServerMetadata) {
        var normalizedIssuer = issuer.hasSuffix("/") ? String(issuer.dropLast()) : issuer
        if !normalizedIssuer.contains("://") { normalizedIssuer = "https://\(normalizedIssuer)" }
        let metadata = try await fetchOAuthMetadata(issuer: normalizedIssuer, session: session)
        return (sessionUrl, metadata)
    }

    private static func fetchOAuthMetadata(
        issuer: String, session: URLSession
    ) async throws -> OAuthServerMetadata {
        guard let issuerURL = URL(string: issuer),
              var components = URLComponents(url: issuerURL, resolvingAgainstBaseURL: false)
        else {
            throw PaccError.noOAuthIssuer
        }

        let basePath = issuerURL.path == "/" || issuerURL.path.isEmpty ? "" : issuerURL.path
        components.path = "/.well-known/oauth-authorization-server\(basePath)"
        guard let metadataURL = components.url else { throw PaccError.noOAuthIssuer }

        let (metaData, _) = try await session.data(from: metadataURL)
        return try JSONDecoder().decode(OAuthServerMetadata.self, from: metaData)
    }
}
