import SwiftUI
import FileProvider
import JmapClient

@main
struct FastmailFilesApp: App {
    @StateObject private var appState = AppState()

    var body: some Scene {
        #if os(macOS)
        MenuBarExtra {
            MenuBarView(appState: appState)
        } label: {
            Image("MenuBarIcon")
                .renderingMode(.template)
        }

        Window("Fastmail Files Settings", id: "settings") {
            SettingsView(appState: appState)
        }
        #else
        WindowGroup {
            ContentView(appState: appState)
        }
        #endif
    }
}

// MARK: - Data Model

/// A login session — one OAuth or token auth, can have multiple accounts.
struct LoginInfo: Codable, Identifiable {
    let loginId: String          // unique ID (e.g. "user@host")
    let sessionURL: String
    let authType: AuthType       // oauth or token
    var accounts: [AccountInfo]  // all discovered accounts for this login
    var connectionStatus: ConnectionStatus

    var id: String { loginId }

    /// Display label for the login (e.g. "brong@brong.net (api.fastmail.com)")
    var displayLabel: String {
        if let url = URL(string: sessionURL), let host = url.host {
            return "\(loginId) (\(host))"
        }
        return loginId
    }

    init(loginId: String, sessionURL: String, authType: AuthType,
         accounts: [AccountInfo], connectionStatus: ConnectionStatus = .unknown) {
        self.loginId = loginId
        self.sessionURL = sessionURL
        self.authType = authType
        self.accounts = accounts
        self.connectionStatus = connectionStatus
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        loginId = try container.decode(String.self, forKey: .loginId)
        sessionURL = try container.decode(String.self, forKey: .sessionURL)
        authType = try container.decode(AuthType.self, forKey: .authType)
        accounts = try container.decode([AccountInfo].self, forKey: .accounts)
        connectionStatus = (try? container.decode(ConnectionStatus.self, forKey: .connectionStatus)) ?? .unknown
    }
}

enum ConnectionStatus: String, Codable {
    case connected
    case connecting
    case authFailed      // token expired, refresh failed
    case networkError    // can't reach server
    case unknown
}

enum AuthType: String, Codable {
    case oauth
    case token
}

/// An individual account within a login.
struct AccountInfo: Codable, Identifiable {
    let accountId: String
    let displayName: String
    var isSynced: Bool           // whether FileProvider domain is registered
    var status: SyncStatus

    var id: String { accountId }
}

enum SyncStatus: String, Codable {
    case idle
    case syncing
    case error
    case offline
    case paused
    case notSynced               // discovered but not enabled
}

// MARK: - App State

@MainActor
class AppState: ObservableObject {
    @Published var logins: [LoginInfo] = []
    @Published var isOnline = true
    @Published var showingAddAccount = false

    private let defaults: UserDefaults?

    #if os(macOS)
    static let appGroupId = "BJL34Q426G.com.fastmail.files"
    #else
    static let appGroupId = "group.com.fastmail.files"
    #endif

    /// All synced accounts across all logins.
    var syncedAccounts: [AccountInfo] {
        logins.flatMap { $0.accounts.filter { $0.isSynced } }
    }

    var statusIcon: String {
        let synced = syncedAccounts
        if !isOnline { return "icloud.slash" }
        if synced.contains(where: { $0.status == .error }) { return "exclamationmark.icloud" }
        if synced.contains(where: { $0.status == .syncing }) { return "arrow.clockwise.icloud" }
        return "icloud"
    }

    init() {
        self.defaults = UserDefaults(suiteName: Self.appGroupId)
        loadState()
        // Check connection status for all logins on startup
        Task { await checkAllConnections() }
    }

    /// Check connection status for all logins by trying to fetch the JMAP session.
    func checkAllConnections() async {
        for i in logins.indices {
            let login = logins[i]
            logins[i].connectionStatus = .connecting

            guard let url = URL(string: login.sessionURL) else {
                logins[i].connectionStatus = .networkError
                continue
            }

            let tokenProvider: TokenProvider
            let appGroup = Self.appGroupId
            let loginAccounts = login.accounts
            if login.authType == .oauth, let credential = loadLoginCredential(loginId: login.loginId) {
                let lid = login.loginId
                tokenProvider = OAuthTokenProvider(credential: credential,
                    onTokenRefreshed: { updated in
                        let encoder = JSONEncoder()
                        encoder.dateEncodingStrategy = .iso8601
                        if let data = try? encoder.encode(updated),
                           let str = String(data: data, encoding: .utf8) {
                            try? KeychainTokenProvider.storeToken(
                                str, service: "com.fastmail.files.login",
                                account: lid, accessGroup: appGroup)
                            for acct in loginAccounts where acct.isSynced {
                                try? KeychainTokenProvider.storeToken(
                                    str, account: acct.accountId, accessGroup: appGroup)
                            }
                        }
                    })
            } else if let token = loadLoginToken(loginId: login.loginId) {
                tokenProvider = StaticTokenProvider(token: token)
            } else {
                logins[i].connectionStatus = .authFailed
                continue
            }

            let sessionManager = SessionManager(sessionURL: url, tokenProvider: tokenProvider)
            do {
                _ = try await sessionManager.session()
                logins[i].connectionStatus = .connected
            } catch let error as JmapError {
                switch error {
                case .unauthorized, .forbidden:
                    logins[i].connectionStatus = .authFailed
                default:
                    logins[i].connectionStatus = .networkError
                }
            } catch {
                logins[i].connectionStatus = .networkError
            }
        }
        saveState()
    }

    // MARK: - Add Login

    /// Add a new login with OAuth credentials and selected accounts.
    func addLogin(loginId: String, sessionURL: String, credential: OAuthCredential,
                  discoveredAccounts: [(accountId: String, name: String, isPrimary: Bool)],
                  selectedAccountIds: Set<String>) async throws {
        // Don't add duplicate logins
        guard !logins.contains(where: { $0.loginId == loginId }) else { return }

        // Store credential in keychain keyed by loginId
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let credData = try encoder.encode(credential)
        let credString = String(data: credData, encoding: .utf8)!
        try KeychainTokenProvider.storeToken(
            credString, service: "com.fastmail.files.login",
            account: loginId, accessGroup: Self.appGroupId)

        // Store session URL
        defaults?.set(sessionURL, forKey: "sessionURL-\(loginId)")

        // Build account list
        var accounts: [AccountInfo] = []
        for acct in discoveredAccounts {
            let synced = selectedAccountIds.contains(acct.accountId)
            accounts.append(AccountInfo(
                accountId: acct.accountId, displayName: acct.name,
                isSynced: synced, status: synced ? .idle : .notSynced))
        }

        // Register FileProvider domains for selected accounts
        for i in accounts.indices where accounts[i].isSynced {
            do {
                try await registerDomain(accountId: accounts[i].accountId, displayName: accounts[i].displayName,
                                         loginId: loginId, credential: credential, sessionURL: sessionURL)
            } catch {
                accounts[i].isSynced = false
                accounts[i].status = .error
            }
        }

        logins.append(LoginInfo(
            loginId: loginId, sessionURL: sessionURL,
            authType: .oauth, accounts: accounts, connectionStatus: .connected))
        saveState()
    }

    /// Add a new login with a static token.
    func addLoginWithToken(loginId: String, sessionURL: String, token: String,
                           discoveredAccounts: [(accountId: String, name: String, isPrimary: Bool)],
                           selectedAccountIds: Set<String>) async throws {
        guard !logins.contains(where: { $0.loginId == loginId }) else { return }

        // Store token keyed by loginId
        try KeychainTokenProvider.storeToken(
            token, service: "com.fastmail.files.login",
            account: loginId, accessGroup: Self.appGroupId)

        defaults?.set(sessionURL, forKey: "sessionURL-\(loginId)")

        var accounts: [AccountInfo] = []
        for acct in discoveredAccounts {
            let synced = selectedAccountIds.contains(acct.accountId)
            accounts.append(AccountInfo(
                accountId: acct.accountId, displayName: acct.name,
                isSynced: synced, status: synced ? .idle : .notSynced))
        }

        for i in accounts.indices where accounts[i].isSynced {
            do {
                try await registerDomainWithToken(
                    accountId: accounts[i].accountId, displayName: accounts[i].displayName,
                    loginId: loginId, token: token, sessionURL: sessionURL)
            } catch {
                accounts[i].isSynced = false
                accounts[i].status = .error
            }
        }

        logins.append(LoginInfo(
            loginId: loginId, sessionURL: sessionURL,
            authType: .token, accounts: accounts, connectionStatus: .connected))
        saveState()
    }

    // MARK: - Remove Login

    /// Remove a login and all its synced accounts.
    func removeLogin(_ loginId: String) async {
        guard let login = logins.first(where: { $0.loginId == loginId }) else { return }

        // Remove all synced account domains
        for acct in login.accounts where acct.isSynced {
            await removeDomain(accountId: acct.accountId)
        }

        // Remove credential from keychain
        let deleteQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.fastmail.files.login",
            kSecAttrAccount as String: loginId,
            kSecUseDataProtectionKeychain as String: true,
        ]
        SecItemDelete(deleteQuery as CFDictionary)

        // Also remove per-account credentials (legacy)
        for acct in login.accounts {
            let acctQuery: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: "com.fastmail.files",
                kSecAttrAccount as String: acct.accountId,
                kSecUseDataProtectionKeychain as String: true,
            ]
            SecItemDelete(acctQuery as CFDictionary)
        }

        defaults?.removeObject(forKey: "sessionURL-\(loginId)")

        logins.removeAll { $0.loginId == loginId }
        saveState()
    }

    // MARK: - Per-Account Actions

    /// Enable syncing for an account (register FileProvider domain).
    func enableAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId })
        else { return }

        let login = logins[loginIdx]
        let acct = login.accounts[acctIdx]

        do {
            if login.authType == .oauth, let credential = loadLoginCredential(loginId: loginId) {
                try await registerDomain(accountId: acct.accountId, displayName: acct.displayName,
                                         loginId: loginId, credential: credential, sessionURL: login.sessionURL)
            } else if let token = loadLoginToken(loginId: loginId) {
                try await registerDomainWithToken(
                    accountId: acct.accountId, displayName: acct.displayName,
                    loginId: loginId, token: token, sessionURL: login.sessionURL)
            }
        } catch {
            logins[loginIdx].accounts[acctIdx].status = .error
            saveState()
            return
        }

        logins[loginIdx].accounts[acctIdx].isSynced = true
        logins[loginIdx].accounts[acctIdx].status = .idle
        saveState()
    }

    /// Disable syncing for an account (remove FileProvider domain).
    func disableAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId })
        else { return }

        await removeDomain(accountId: accountId)

        logins[loginIdx].accounts[acctIdx].isSynced = false
        logins[loginIdx].accounts[acctIdx].status = .notSynced
        saveState()
    }

    /// Clean an account: remove domain, wipe local caches, re-register.
    /// This forces a full re-fetch from the server.
    func cleanAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId }),
              logins[loginIdx].accounts[acctIdx].isSynced
        else { return }

        let login = logins[loginIdx]
        let acct = logins[loginIdx].accounts[acctIdx]

        // 1. Remove the FileProvider domain (this clears the system's local state)
        await removeDomain(accountId: accountId)

        // 2. Wipe the extension's shared container data for this account
        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            // Node database
            let nodeCache = containerURL.appendingPathComponent("nodes-\(accountId).json")
            try? FileManager.default.removeItem(at: nodeCache)
            // Blob cache directory
            let blobDir = containerURL.appendingPathComponent("blobs-\(accountId)")
            try? FileManager.default.removeItem(at: blobDir)
        }

        // 3. Re-register the domain so it starts fresh
        do {
            if login.authType == .oauth, let credential = loadLoginCredential(loginId: loginId) {
                try await registerDomain(accountId: acct.accountId, displayName: acct.displayName,
                                         loginId: loginId, credential: credential, sessionURL: login.sessionURL)
            } else if let token = loadLoginToken(loginId: loginId) {
                try await registerDomainWithToken(
                    accountId: acct.accountId, displayName: acct.displayName,
                    loginId: loginId, token: token, sessionURL: login.sessionURL)
            }
            logins[loginIdx].accounts[acctIdx].status = .idle
        } catch {
            logins[loginIdx].accounts[acctIdx].isSynced = false
            logins[loginIdx].accounts[acctIdx].status = .error
        }
        saveState()
    }

    /// Update the OAuth credential for all accounts in a login.
    /// Used by Reauthenticate to replace an expired/broken token.
    func updateLoginCredential(loginId: String, credential: OAuthCredential) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }) else { return }

        // Update login-level credential in keychain
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        if let credData = try? encoder.encode(credential),
           let credString = String(data: credData, encoding: .utf8) {
            try? KeychainTokenProvider.storeToken(
                credString, service: "com.fastmail.files.login",
                account: loginId, accessGroup: Self.appGroupId)
        }

        // Update per-account credentials for all synced accounts
        for acct in logins[loginIdx].accounts where acct.isSynced {
            if let credData = try? encoder.encode(credential),
               let credString = String(data: credData, encoding: .utf8) {
                try? KeychainTokenProvider.storeToken(
                    credString, account: acct.accountId, accessGroup: Self.appGroupId)
            }
            defaults?.set(credential.sessionUrl, forKey: "sessionURL-\(acct.accountId)")
            defaults?.set("oauth", forKey: "authType-\(acct.accountId)")
        }

        // Signal each account's FileProvider domain to restart
        for acct in logins[loginIdx].accounts where acct.isSynced {
            syncNow(acct.accountId)
        }
    }

    /// Sync now for a specific account.
    func syncNow(_ accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
            displayName: ""
        )
        NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
    }

    /// Remove all logins and clean up everything.
    func removeAll() async {
        let allLogins = logins
        for login in allLogins {
            await removeLogin(login.loginId)
        }
        await cleanupOrphanedDomains()
    }

    // MARK: - Domain Management

    private func registerDomain(accountId: String, displayName: String,
                                loginId: String, credential: OAuthCredential,
                                sessionURL: String) async throws {
        // Store per-account credential for the extension to read
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        if let credData = try? encoder.encode(credential),
           let credString = String(data: credData, encoding: .utf8) {
            try? KeychainTokenProvider.storeToken(
                credString, account: accountId, accessGroup: Self.appGroupId)
        }
        defaults?.set(sessionURL, forKey: "sessionURL-\(accountId)")
        defaults?.set("oauth", forKey: "authType-\(accountId)")

        let domainName = displayName.isEmpty ? accountId : "\(displayName) Files"
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
                displayName: domainName
            )
            try await NSFileProviderManager.add(domain)
        } catch {
            print("FileProvider domain registration failed: \(error.localizedDescription)")
            // Clean up the credentials we just stored since domain failed
            let deleteQuery: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: "com.fastmail.files",
                kSecAttrAccount as String: accountId,
                kSecUseDataProtectionKeychain as String: true,
            ]
            SecItemDelete(deleteQuery as CFDictionary)
            defaults?.removeObject(forKey: "sessionURL-\(accountId)")
            defaults?.removeObject(forKey: "authType-\(accountId)")
            throw error
        }
    }

    private func registerDomainWithToken(accountId: String, displayName: String,
                                         loginId: String, token: String,
                                         sessionURL: String) async throws {
        try? KeychainTokenProvider.storeToken(
            token, account: accountId, accessGroup: Self.appGroupId)
        defaults?.set(sessionURL, forKey: "sessionURL-\(accountId)")

        let domainName = displayName.isEmpty ? accountId : "\(displayName) Files"
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
                displayName: domainName
            )
            try await NSFileProviderManager.add(domain)
        } catch {
            print("FileProvider domain registration failed: \(error.localizedDescription)")
            let deleteQuery: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: "com.fastmail.files",
                kSecAttrAccount as String: accountId,
                kSecUseDataProtectionKeychain as String: true,
            ]
            SecItemDelete(deleteQuery as CFDictionary)
            defaults?.removeObject(forKey: "sessionURL-\(accountId)")
            throw error
        }
    }

    private func removeDomain(accountId: String) async {
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
                displayName: ""
            )
            try await NSFileProviderManager.remove(domain)
        } catch {
            print("FileProvider domain removal failed: \(error.localizedDescription)")
        }

        // Clean node cache and blob cache from shared container
        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            let nodeCache = containerURL.appendingPathComponent("nodes-\(accountId).json")
            try? FileManager.default.removeItem(at: nodeCache)
            let blobDir = containerURL.appendingPathComponent("blobs-\(accountId)")
            try? FileManager.default.removeItem(at: blobDir)
        }

        // Clean per-account keychain entry
        let deleteQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.fastmail.files",
            kSecAttrAccount as String: accountId,
            kSecUseDataProtectionKeychain as String: true,
        ]
        SecItemDelete(deleteQuery as CFDictionary)

        defaults?.removeObject(forKey: "sessionURL-\(accountId)")
        defaults?.removeObject(forKey: "authType-\(accountId)")
    }

    /// Find and remove FileProvider domains that aren't in our account list.
    func cleanupOrphanedDomains() async {
        let domains = await listDomains()
        let knownIds = Set(logins.flatMap { $0.accounts.map { $0.accountId } })
        for domain in domains {
            if !knownIds.contains(domain.identifier.rawValue) {
                print("Removing orphaned domain: \(domain.displayName) (\(domain.identifier.rawValue))")
                try? await NSFileProviderManager.remove(domain)
            }
        }
    }

    /// List all registered FileProvider domains.
    func listDomains() async -> [NSFileProviderDomain] {
        await withUnsafeContinuation { continuation in
            NSFileProviderManager.getDomainsWithCompletionHandler { domains, _ in
                nonisolated(unsafe) let result = domains
                continuation.resume(returning: result)
            }
        }
    }

    // MARK: - Credential Helpers

    func loadLoginCredential(loginId: String) -> OAuthCredential? {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.fastmail.files.login",
            kSecAttrAccount as String: loginId,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecUseDataProtectionKeychain as String: true,
        ]
        if !Self.appGroupId.isEmpty {
            query[kSecAttrAccessGroup as String] = Self.appGroupId
        }
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(OAuthCredential.self, from: data)
    }

    func loadLoginToken(loginId: String) -> String? {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "com.fastmail.files.login",
            kSecAttrAccount as String: loginId,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecUseDataProtectionKeychain as String: true,
        ]
        if !Self.appGroupId.isEmpty {
            query[kSecAttrAccessGroup as String] = Self.appGroupId
        }
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data,
              let str = String(data: data, encoding: .utf8) else { return nil }
        return str
    }

    // MARK: - Persistence

    private func loadState() {
        guard let data = defaults?.data(forKey: "logins"),
              let decoded = try? JSONDecoder().decode([LoginInfo].self, from: data)
        else {
            // Try loading legacy flat account list
            migrateFromLegacy()
            return
        }
        logins = decoded
    }

    private func saveState() {
        // Safety: never overwrite with empty if we previously had logins
        // (protects against decode failures wiping saved state)
        if logins.isEmpty, let existing = defaults?.data(forKey: "logins"), !existing.isEmpty {
            // Only allow empty save if explicitly removing all
            if (try? JSONDecoder().decode([LoginInfo].self, from: existing))?.isEmpty == false {
                return
            }
        }
        guard let data = try? JSONEncoder().encode(logins) else { return }
        defaults?.set(data, forKey: "logins")
    }

    /// Migrate from old flat [AccountInfo] to new login-grouped model.
    private func migrateFromLegacy() {
        guard let data = defaults?.data(forKey: "accounts"),
              let oldAccounts = try? JSONDecoder().decode([LegacyAccountInfo].self, from: data)
        else { return }

        // Group by session URL
        var bySession: [String: [LegacyAccountInfo]] = [:]
        for acct in oldAccounts {
            let url = defaults?.string(forKey: "sessionURL-\(acct.accountId)") ?? "unknown"
            bySession[url, default: []].append(acct)
        }

        for (sessionURL, accounts) in bySession {
            let loginId = accounts.first?.displayName ?? "unknown"
            let accountInfos = accounts.map {
                AccountInfo(accountId: $0.accountId, displayName: $0.displayName,
                            isSynced: true, status: .idle)
            }
            let authType: AuthType = defaults?.string(forKey: "authType-\(accounts.first?.accountId ?? "")") == "oauth"
                ? .oauth : .token
            logins.append(LoginInfo(
                loginId: loginId, sessionURL: sessionURL,
                authType: authType, accounts: accountInfos, connectionStatus: .unknown))
        }

        if !logins.isEmpty {
            saveState()
            defaults?.removeObject(forKey: "accounts") // clean up legacy key
        }
    }
}

/// Legacy model for migration.
private struct LegacyAccountInfo: Codable {
    let accountId: String
    let displayName: String
    var status: String
}
