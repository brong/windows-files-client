import Foundation

/// Handles JMAP session discovery and lifecycle.
public actor SessionManager {
    private let sessionURL: URL
    private let tokenProvider: TokenProvider
    private var cachedSession: JmapSession?
    private let diskCacheURL: URL?
    private let decoder: JSONDecoder

    private let urlSession: URLSession

    /// - Parameter diskCacheURL: Optional path to persist the session document between
    ///   process launches. On warm start the cached session is returned without a network
    ///   round-trip. Pass nil to disable persistence (in-memory cache only).
    public init(sessionURL: URL, tokenProvider: TokenProvider,
                diskCacheURL: URL? = nil,
                protocolClasses: [AnyClass]? = nil) {
        self.sessionURL = sessionURL
        self.tokenProvider = tokenProvider
        self.diskCacheURL = diskCacheURL

        if let protocols = protocolClasses {
            let config = URLSessionConfiguration.default
            config.protocolClasses = protocols
            self.urlSession = URLSession(configuration: config)
        } else {
            self.urlSession = URLSession.shared
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let dateString = try container.decode(String.self)
            if let date = ISO8601DateFormatter().date(from: dateString) {
                return date
            }
            // Try without fractional seconds
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime]
            if let date = formatter.date(from: dateString) {
                return date
            }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Cannot decode date: \(dateString)")
        }
        self.decoder = decoder
    }

    /// Fetch or return cached session.
    /// Order: in-memory cache → disk cache → network fetch.
    public func session() async throws -> JmapSession {
        if let cached = cachedSession {
            return cached
        }
        if let url = diskCacheURL,
           let data = try? Data(contentsOf: url),
           let session = try? decoder.decode(JmapSession.self, from: data) {
            cachedSession = session
            return session
        }
        return try await refreshSession()
    }

    /// Force re-fetch the session (e.g. after 401 or capability change).
    @discardableResult
    public func refreshSession() async throws -> JmapSession {
        let token = try await tokenProvider.currentToken()

        var request = URLRequest(url: sessionURL)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await urlSession.data(for: request)

        guard let httpResponse = response as? HTTPURLResponse else {
            throw JmapError.invalidResponse
        }

        switch httpResponse.statusCode {
        case 200:
            let session = try decoder.decode(JmapSession.self, from: data)
            guard session.hasFileNode else {
                throw JmapError.missingCapability("FileNode")
            }
            if let url = diskCacheURL {
                try? data.write(to: url, options: .atomic)
            }
            cachedSession = session
            return session
        case 401:
            throw JmapError.unauthorized
        default:
            throw JmapError.httpError(httpResponse.statusCode, String(data: data, encoding: .utf8))
        }
    }

    /// Invalidate the cached session.
    public func invalidate() {
        cachedSession = nil
    }
}

// MARK: - Token Provider

/// Protocol for providing auth tokens. The containing app writes tokens
/// to the Keychain; the extension reads them via this protocol.
public protocol TokenProvider: Sendable {
    func currentToken() async throws -> String
}

/// Simple token provider that holds a static token (for development/testing).
public final class StaticTokenProvider: TokenProvider, Sendable {
    private let token: String

    public init(token: String) {
        self.token = token
    }

    public func currentToken() async throws -> String {
        token
    }
}

/// Token provider that reads from the shared Keychain.
public final class KeychainTokenProvider: TokenProvider, @unchecked Sendable {
    private let service: String
    private let account: String
    private let accessGroup: String?

    public init(service: String = "com.fastmail.files",
                account: String,
                accessGroup: String? = nil) {
        self.service = service
        self.account = account
        self.accessGroup = accessGroup
    }

    public func currentToken() async throws -> String {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecUseDataProtectionKeychain as String: true,
        ]
        if let group = accessGroup {
            query[kSecAttrAccessGroup as String] = group
        }

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)

        guard status == errSecSuccess,
              let data = result as? Data,
              let token = String(data: data, encoding: .utf8)
        else {
            throw JmapError.unauthorized
        }

        return token
    }

    /// Store a token in the shared Keychain (called by the containing app).
    public static func storeToken(_ token: String,
                                  service: String = "com.fastmail.files",
                                  account: String,
                                  accessGroup: String? = nil) throws {
        let tokenData = Data(token.utf8)

        // Delete existing
        var deleteQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecUseDataProtectionKeychain as String: true,
        ]
        if let group = accessGroup {
            deleteQuery[kSecAttrAccessGroup as String] = group
        }
        SecItemDelete(deleteQuery as CFDictionary)

        // Add new
        var addQuery = deleteQuery
        addQuery[kSecValueData as String] = tokenData
        addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

        let status = SecItemAdd(addQuery as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw JmapError.keychainError(status)
        }
    }
}
