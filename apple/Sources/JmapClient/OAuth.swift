import Foundation
import Darwin
#if canImport(CryptoKit)
import CryptoKit
#endif

// MARK: - Discovery Models

/// Response from `ua-auto-config.{domain}/.well-known/user-agent-configuration.json`
/// (draft-ietf-mailmaint-pacc-01)
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
    public static let discoveryURL = "https://ua-auto-config.fastmail.com/.well-known/user-agent-configuration.json"
}

// MARK: - OAuth Discovery

/// Discover JMAP session URL and OAuth metadata.
/// Delegates to PaccDiscovery.discover(email:) using a fixed Fastmail domain.
/// Callers that accept arbitrary email addresses should call PaccDiscovery.discover directly.
public func oauthDiscover() async throws -> (sessionUrl: String, metadata: OAuthServerMetadata) {
    return try await PaccDiscovery.discover(email: "discover@fastmail.com")
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
/// `resource` (RFC 8707) is required by the Fastmail OAuth server for
/// dynamically-registered clients; pass the JMAP session URL here.
public func oauthAuthorizationURL(
    authorizationEndpoint: String,
    clientId: String,
    redirectURI: String,
    codeChallenge: String,
    state: String,
    resource: String
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
        URLQueryItem(name: "resource", value: resource),
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
        // RFC 6749 §5.2: token endpoint returns 400 with {"error": "invalid_grant"} when
        // a rotating refresh token has already been consumed by another process.
        if (response as? HTTPURLResponse)?.statusCode == 400,
           let body = try? JSONDecoder().decode([String: String].self, from: data),
           body["error"] == "invalid_grant" {
            throw JmapError.tokenRotated
        }
        throw JmapError.unauthorized
    }

    return try JSONDecoder().decode(OAuthTokenResponse.self, from: data)
}

// MARK: - flock shim
//
// Swift imports 'flock' as the struct type (struct flock, used with fcntl), which
// shadows the BSD flock(2) syscall. @_silgen_name resolves to the actual C function.
// BSD flock(2) has better per-fd semantics than POSIX record locks: the lock is owned
// by the file description, not the process, so it auto-releases on process death or
// fd close without the POSIX footgun of "close any fd to the file = release all locks".

@_silgen_name("flock")
private func _flock(_ fd: Int32, _ operation: Int32) -> Int32

// MARK: - Refreshing Token Provider

/// A TokenProvider that automatically refreshes OAuth tokens.
///
/// CRITICAL invariant: when refreshing, the new refresh token MUST be persisted
/// to durable storage BEFORE the new access token is used. OAuth refresh tokens
/// are single-use (rotating) — once used, the server invalidates the old one.
/// If we use the new access token but crash before persisting the new refresh
/// token, we're locked out on restart because the old refresh token is gone.
///
/// The flow is: acquire cross-process lock → check keychain → call token endpoint
///              → persist new credential → update in-memory state → release lock.
///
/// The cross-process lock (flock on a shared file) ensures that when multiple
/// extension instances share the same login, only one hits the token endpoint.
/// The others block at the lock and read fresh credentials from keychain once
/// the winner releases it — no polling, no races, no redundant HTTP requests.
public actor OAuthTokenProvider: TokenProvider {
    private var accessToken: String
    private var refreshToken: String
    private let sessionUrl: String
    private var tokenEndpoint: String
    private var clientId: String
    private var expiresAt: Date
    private let refreshMargin: TimeInterval = 60 // refresh 60s before expiry
    /// In-flight refresh task. Same-process concurrent callers await this instead
    /// of starting a second refresh — the cross-process flock handles other processes.
    private var refreshTask: Task<Void, Error>?
    private var onTokenRefreshed: (@Sendable (OAuthCredential) -> Void)?
    /// Optional callback to reload credential from keychain (e.g. after app reauthenticates).
    private var reloadCredential: (@Sendable () -> OAuthCredential?)?
    /// File descriptor for the cross-process refresh lock file.
    /// -1 = not opened (no lockFilePath supplied, or open failed).
    private var lockFd: Int32 = -1
    private let lockFilePath: String?

    public init(credential: OAuthCredential,
                lockFilePath: String? = nil,
                onTokenRefreshed: (@Sendable (OAuthCredential) -> Void)? = nil,
                reloadCredential: (@Sendable () -> OAuthCredential?)? = nil) {
        self.accessToken = credential.accessToken
        self.refreshToken = credential.refreshToken
        self.sessionUrl = credential.sessionUrl
        self.tokenEndpoint = credential.tokenEndpoint
        self.clientId = credential.clientId
        self.expiresAt = credential.expiresAt
        self.lockFilePath = lockFilePath
        self.onTokenRefreshed = onTokenRefreshed
        self.reloadCredential = reloadCredential
    }

    deinit {
        if lockFd >= 0 { Darwin.close(lockFd) }
    }

    public func currentToken() async throws -> String {
        // Proactively refresh if close to expiry
        if Date().addingTimeInterval(refreshMargin) >= expiresAt {
            try await refresh()
        }
        return accessToken
    }

    /// Invalidate the current access token (e.g. after a 401).
    /// Next call to currentToken() will force a refresh.
    public func invalidateAccessToken() {
        expiresAt = .distantPast
    }

    /// Apply a new credential (e.g. loaded from keychain after reauthentication).
    public func applyCredential(_ credential: OAuthCredential) {
        accessToken = credential.accessToken
        refreshToken = credential.refreshToken
        tokenEndpoint = credential.tokenEndpoint
        clientId = credential.clientId
        expiresAt = credential.expiresAt
    }

    /// Force a refresh (e.g., after 401).
    ///
    /// Two-level concurrency control:
    /// - Same process: `refreshTask` single-flight guard — concurrent calls join the in-flight task.
    /// - Cross process: `flock(LOCK_EX)` on a shared file — the winner refreshes and writes
    ///   keychain; all other processes block at the lock and read fresh credentials when they
    ///   get in. No polling, no redundant HTTP requests, no races.
    public func refresh() async throws {
        if let existing = refreshTask {
            try await existing.value
            return
        }

        let task = Task<Void, Error> {
            try await self.withRefreshLock { try await self.performRefresh() }
        }
        refreshTask = task
        defer { refreshTask = nil }
        try await task.value
    }

    /// Acquire the cross-process flock, run `body`, then release.
    /// If the lock file is unavailable the body still runs — same-process guard still applies.
    private func withRefreshLock<T>(_ body: () async throws -> T) async throws -> T {
        openLockFileIfNeeded()
        guard lockFd >= 0 else { return try await body() }

        try await acquireFlock(fd: lockFd)
        defer { _ = _flock(lockFd, LOCK_UN) }
        return try await body()
    }

    /// Open (or create) the lock file. Write-once: safe to call multiple times.
    private func openLockFileIfNeeded() {
        guard lockFd < 0, let path = lockFilePath else { return }
        lockFd = Darwin.open(path, O_RDWR | O_CREAT, 0o600)
    }

    /// Dispatch a blocking flock(LOCK_EX) to a background thread so the actor's
    /// executor is free while we wait for another process to release the lock.
    private func acquireFlock(fd: Int32) async throws {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                if _flock(fd, LOCK_EX) == 0 {
                    continuation.resume()
                } else {
                    let code = Darwin.errno
                    continuation.resume(throwing: POSIXError(
                        POSIXErrorCode(rawValue: code) ?? .EINVAL))
                }
            }
        }
    }

    private func performRefresh() async throws {
        // Check keychain first — if another process held the lock and refreshed
        // while we were waiting, we find fresh tokens here and skip the HTTP call.
        if let reload = reloadCredential, let fresh = reload() {
            if fresh.accessToken != accessToken || fresh.refreshToken != refreshToken {
                applyCredential(fresh)
                return
            }
        }

        // We hold the lock and have the latest credential — we're the refresher.
        do {
            let response = try await oauthRefreshToken(
                tokenEndpoint: tokenEndpoint,
                clientId: clientId,
                refreshToken: refreshToken
            )

            let newRefreshToken = response.refreshToken ?? refreshToken
            let newExpiresAt = Date().addingTimeInterval(TimeInterval(response.expiresIn))
            let credential = OAuthCredential(
                sessionUrl: sessionUrl,
                accessToken: response.accessToken,
                refreshToken: newRefreshToken,
                tokenEndpoint: tokenEndpoint,
                clientId: clientId,
                expiresAt: newExpiresAt
            )

            // PERSIST FIRST — before updating in-memory state.
            onTokenRefreshed?(credential)

            // NOW update in-memory state.
            accessToken = response.accessToken
            refreshToken = newRefreshToken
            expiresAt = newExpiresAt
        } catch JmapError.tokenRotated {
            // invalid_grant with the lock held: should be impossible in normal operation
            // (the lock serialises access so only one process calls the token endpoint).
            // Fallback: one final keychain check in case the lock file wasn't configured.
            if let reload = reloadCredential, let fresh = reload() {
                if fresh.accessToken != accessToken || fresh.refreshToken != refreshToken {
                    applyCredential(fresh)
                    return
                }
            }
            throw JmapError.unauthorized
        } catch {
            // Network or server failure — one keychain check in case the app process
            // refreshed out-of-band (e.g. reauthentication flow) while this call failed.
            if let reload = reloadCredential, let fresh = reload() {
                if fresh.accessToken != accessToken || fresh.refreshToken != refreshToken {
                    applyCredential(fresh)
                    return
                }
            }
            throw error
        }
    }
}

// MARK: - Helpers

private func urlEncode(_ string: String) -> String {
    string.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? string
}
