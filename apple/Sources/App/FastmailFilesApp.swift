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
            Image(systemName: appState.statusIcon)
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

    var id: String { loginId }

    /// Display label for the login (e.g. "brong@brong.net (api.fastmail.com)")
    var displayLabel: String {
        if let url = URL(string: sessionURL), let host = url.host {
            return "\(loginId) (\(host))"
        }
        return loginId
    }
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
        for acct in accounts where acct.isSynced {
            await registerDomain(accountId: acct.accountId, displayName: acct.displayName,
                                 loginId: loginId, credential: credential, sessionURL: sessionURL)
        }

        logins.append(LoginInfo(
            loginId: loginId, sessionURL: sessionURL,
            authType: .oauth, accounts: accounts))
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

        for acct in accounts where acct.isSynced {
            await registerDomainWithToken(
                accountId: acct.accountId, displayName: acct.displayName,
                loginId: loginId, token: token, sessionURL: sessionURL)
        }

        logins.append(LoginInfo(
            loginId: loginId, sessionURL: sessionURL,
            authType: .token, accounts: accounts))
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

        if login.authType == .oauth, let credential = loadLoginCredential(loginId: loginId) {
            await registerDomain(accountId: acct.accountId, displayName: acct.displayName,
                                 loginId: loginId, credential: credential, sessionURL: login.sessionURL)
        } else if let token = loadLoginToken(loginId: loginId) {
            await registerDomainWithToken(
                accountId: acct.accountId, displayName: acct.displayName,
                loginId: loginId, token: token, sessionURL: login.sessionURL)
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
        if login.authType == .oauth, let credential = loadLoginCredential(loginId: loginId) {
            await registerDomain(accountId: acct.accountId, displayName: acct.displayName,
                                 loginId: loginId, credential: credential, sessionURL: login.sessionURL)
        } else if let token = loadLoginToken(loginId: loginId) {
            await registerDomainWithToken(
                accountId: acct.accountId, displayName: acct.displayName,
                loginId: loginId, token: token, sessionURL: login.sessionURL)
        }

        logins[loginIdx].accounts[acctIdx].status = .idle
        saveState()
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
                                sessionURL: String) async {
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
            print("FileProvider domain registration skipped: \(error.localizedDescription)")
        }
    }

    private func registerDomainWithToken(accountId: String, displayName: String,
                                         loginId: String, token: String,
                                         sessionURL: String) async {
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
            print("FileProvider domain registration skipped: \(error.localizedDescription)")
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
                authType: authType, accounts: accountInfos))
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
