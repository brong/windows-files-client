import Foundation
import JmapClient

/// Handles all keychain operations for login credentials.
/// Not an actor — keychain calls are synchronous.
final class CredentialStore: @unchecked Sendable {
    private let service = "com.fastmail.files.login"
    private let appGroupId: String

    init(appGroupId: String) {
        self.appGroupId = appGroupId
    }

    // MARK: - Store

    func storeCredential(_ credential: OAuthCredential, for loginId: String) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(credential)
        let str = String(data: data, encoding: .utf8)!
        try storeToken(str, for: loginId)
    }

    func storeToken(_ token: String, for loginId: String) throws {
        try KeychainTokenProvider.storeToken(
            token, service: service,
            account: loginId, accessGroup: appGroupId)
    }

    // MARK: - Load

    func loadCredential(for loginId: String) -> OAuthCredential? {
        guard let str = loadToken(for: loginId),
              let data = str.data(using: .utf8) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(OAuthCredential.self, from: data)
    }

    func loadToken(for loginId: String) -> String? {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: loginId,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecUseDataProtectionKeychain as String: true,
            kSecAttrAccessGroup as String: appGroupId,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data,
              let str = String(data: data, encoding: .utf8) else { return nil }
        return str
    }

    // MARK: - Delete

    func delete(for loginId: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: loginId,
            kSecUseDataProtectionKeychain as String: true,
        ]
        SecItemDelete(query as CFDictionary)
    }

    // MARK: - Token Provider Factory

    func makeTokenProvider(for login: LoginInfo) -> TokenProvider? {
        let lid = login.loginId
        let kcService = service
        let appGroup = appGroupId
        if login.authType == .oauth, let credential = loadCredential(for: lid) {
            return OAuthTokenProvider(credential: credential, onTokenRefreshed: { updated in
                let encoder = JSONEncoder()
                encoder.dateEncodingStrategy = .iso8601
                if let data = try? encoder.encode(updated),
                   let str = String(data: data, encoding: .utf8) {
                    try? KeychainTokenProvider.storeToken(
                        str, service: kcService, account: lid, accessGroup: appGroup)
                }
            })
        } else if let token = loadToken(for: lid) {
            return StaticTokenProvider(token: token)
        }
        return nil
    }
}
