import SwiftUI
import FileProvider
import JmapClient

@main
struct FastmailFilesApp: App {
    @StateObject private var appState = AppState()
    #if os(macOS)
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @Environment(\.openWindow) private var openWindow
    #endif

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
                .onReceive(NotificationCenter.default.publisher(for: .openSettings)) { _ in
                    openWindow(id: "settings")
                }
        }
        #else
        WindowGroup {
            ContentView(appState: appState)
        }
        #endif
    }
}

#if os(macOS)
class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        sender.activate(ignoringOtherApps: true)
        // Find and show the settings window (not the menu bar status window)
        for window in sender.windows {
            if window.canBecomeKey {
                window.makeKeyAndOrderFront(nil)
                return false
            }
        }
        // No key-capable window exists — need to create it.
        // Post a notification that the SwiftUI app picks up to open the window.
        NotificationCenter.default.post(name: .openSettings, object: nil)
        return true
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Close any auto-opened windows on launch — menu bar icon is primary
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            for window in NSApplication.shared.windows where window.canBecomeKey {
                window.close()
            }
        }
    }
}

extension Notification.Name {
    static let openSettings = Notification.Name("openSettings")
}
#endif

// MARK: - Data Model

struct LoginInfo: Codable, Identifiable {
    let loginId: String
    let sessionURL: String
    let authType: AuthType
    var accounts: [AccountInfo]
    var connectionStatus: ConnectionStatus

    var id: String { loginId }

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
    case connected, connecting, authFailed, networkError, unknown
}

enum AuthType: String, Codable {
    case oauth, token
}

struct AccountInfo: Codable, Identifiable {
    let accountId: String
    let displayName: String
    var isSynced: Bool
    var status: SyncStatus  // hint only — UI derives live status from activity tracker

    var id: String { accountId }
}

enum SyncStatus: String, Codable {
    case idle, syncing, error, offline, paused, notSynced
}

// MARK: - App State

@MainActor
class AppState: ObservableObject {
    @Published var logins: [LoginInfo] = []
    @Published var isOnline = true
    @Published var showingAddAccount = false
    @Published var activeAccountIds: Set<String> = []
    /// Accounts that have completed at least one sync since app launch.
    @Published var seenAccountIds: Set<String> = []

    private let defaults: UserDefaults?

    #if os(macOS)
    static let appGroupId = "BJL34Q426G.com.fastmail.files"
    #else
    static let appGroupId = "group.com.fastmail.files"
    #endif

    static let loginKeychainService = "com.fastmail.files.login"

    var syncedAccounts: [AccountInfo] {
        logins.flatMap { $0.accounts.filter { $0.isSynced } }
    }

    func liveStatus(for accountId: String) -> SyncStatus {
        for login in logins {
            if let acct = login.accounts.first(where: { $0.accountId == accountId }) {
                if !acct.isSynced { return .notSynced }
                if login.connectionStatus == .authFailed { return .error }
                if login.connectionStatus == .networkError { return .offline }
                if activeAccountIds.contains(accountId) { return .syncing }
                // If we haven't seen any activity for this account yet, it's waiting
                if !seenAccountIds.contains(accountId) { return .syncing }
                return .idle
            }
        }
        return .notSynced
    }

    var statusIcon: String {
        let synced = syncedAccounts
        if !isOnline { return "icloud.slash" }
        if synced.contains(where: { liveStatus(for: $0.accountId) == .error }) { return "exclamationmark.icloud" }
        if synced.contains(where: { liveStatus(for: $0.accountId) == .syncing }) { return "arrow.clockwise.icloud" }
        return "icloud"
    }

    init() {
        self.defaults = UserDefaults(suiteName: Self.appGroupId)
        loadState()
        Task { await checkAllConnections() }
    }

    // MARK: - Connection Check

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
            let lid = login.loginId

            let kcService = Self.loginKeychainService
            if login.authType == .oauth, let credential = loadLoginCredential(loginId: lid) {
                tokenProvider = OAuthTokenProvider(credential: credential,
                    onTokenRefreshed: { updated in
                        let encoder = JSONEncoder()
                        encoder.dateEncodingStrategy = .iso8601
                        if let data = try? encoder.encode(updated),
                           let str = String(data: data, encoding: .utf8) {
                            try? KeychainTokenProvider.storeToken(
                                str, service: kcService,
                                account: lid, accessGroup: appGroup)
                        }
                    })
            } else if let token = loadLoginToken(loginId: lid) {
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

    func addLogin(loginId: String, sessionURL: String, credential: OAuthCredential,
                  discoveredAccounts: [(accountId: String, name: String, isPrimary: Bool)],
                  selectedAccountIds: Set<String>) async throws {
        guard !logins.contains(where: { $0.loginId == loginId }) else { return }

        // Store credential once at login level
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let credData = try encoder.encode(credential)
        let credString = String(data: credData, encoding: .utf8)!
        try KeychainTokenProvider.storeToken(
            credString, service: Self.loginKeychainService,
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
                try await registerDomain(
                    accountId: accounts[i].accountId, displayName: accounts[i].displayName,
                    loginId: loginId, sessionURL: sessionURL, authType: .oauth)
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

    func addLoginWithToken(loginId: String, sessionURL: String, token: String,
                           discoveredAccounts: [(accountId: String, name: String, isPrimary: Bool)],
                           selectedAccountIds: Set<String>) async throws {
        guard !logins.contains(where: { $0.loginId == loginId }) else { return }

        try KeychainTokenProvider.storeToken(
            token, service: Self.loginKeychainService,
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
                try await registerDomain(
                    accountId: accounts[i].accountId, displayName: accounts[i].displayName,
                    loginId: loginId, sessionURL: sessionURL, authType: .token)
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

    func removeLogin(_ loginId: String) async {
        guard let login = logins.first(where: { $0.loginId == loginId }) else { return }

        for acct in login.accounts where acct.isSynced {
            await removeDomain(accountId: acct.accountId)
        }

        // Remove login credential
        let deleteQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.loginKeychainService,
            kSecAttrAccount as String: loginId,
            kSecUseDataProtectionKeychain as String: true,
        ]
        SecItemDelete(deleteQuery as CFDictionary)

        defaults?.removeObject(forKey: "sessionURL-\(loginId)")

        logins.removeAll { $0.loginId == loginId }
        saveState()
    }

    // MARK: - Per-Account Actions

    func enableAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId })
        else { return }

        let login = logins[loginIdx]
        do {
            try await registerDomain(
                accountId: accountId, displayName: logins[loginIdx].accounts[acctIdx].displayName,
                loginId: loginId, sessionURL: login.sessionURL, authType: login.authType)
        } catch {
            logins[loginIdx].accounts[acctIdx].status = .error
            saveState()
            return
        }

        logins[loginIdx].accounts[acctIdx].isSynced = true
        logins[loginIdx].accounts[acctIdx].status = .idle
        saveState()
    }

    func disableAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId })
        else { return }

        await removeDomain(accountId: accountId)

        logins[loginIdx].accounts[acctIdx].isSynced = false
        logins[loginIdx].accounts[acctIdx].status = .notSynced
        saveState()
    }

    func cleanAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId }),
              logins[loginIdx].accounts[acctIdx].isSynced
        else { return }

        let login = logins[loginIdx]

        // Evict downloaded content
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
        if let manager = NSFileProviderManager(for: domain) {
            manager.evictItem(identifier: .rootContainer) { _ in }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }

        await removeDomain(accountId: accountId)

        // Re-register
        do {
            try await registerDomain(
                accountId: accountId,
                displayName: logins[loginIdx].accounts[acctIdx].displayName,
                loginId: loginId, sessionURL: login.sessionURL, authType: login.authType)
            logins[loginIdx].accounts[acctIdx].status = .syncing
            syncNow(accountId)
        } catch {
            logins[loginIdx].accounts[acctIdx].isSynced = false
            logins[loginIdx].accounts[acctIdx].status = .error
        }
        saveState()
    }

    func updateLoginCredential(loginId: String, credential: OAuthCredential) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }) else { return }

        // Update login-level credential only
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        if let credData = try? encoder.encode(credential),
           let credString = String(data: credData, encoding: .utf8) {
            try? KeychainTokenProvider.storeToken(
                credString, service: Self.loginKeychainService,
                account: loginId, accessGroup: Self.appGroupId)
        }

        logins[loginIdx].connectionStatus = .connected
        saveState()

        for acct in logins[loginIdx].accounts where acct.isSynced {
            syncNow(acct.accountId)
        }
    }

    func syncNow(_ accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
        NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
    }

    func removeAll() async {
        for login in logins {
            await removeLogin(login.loginId)
        }
        await cleanupOrphanedDomains()
    }

    // MARK: - Domain Management

    /// Register a FileProvider domain. Stores the account→login mapping in UserDefaults
    /// so the extension can find the login credential.
    private func registerDomain(accountId: String, displayName: String,
                                loginId: String, sessionURL: String,
                                authType: AuthType) async throws {
        // Store mapping so extension can find the login credential
        defaults?.set(loginId, forKey: "loginForAccount-\(accountId)")
        defaults?.set(sessionURL, forKey: "sessionURL-\(accountId)")
        defaults?.set(authType.rawValue, forKey: "authType-\(accountId)")

        let domainName = displayName.isEmpty ? accountId : "\(displayName) Files"
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
                displayName: domainName
            )
            try await NSFileProviderManager.add(domain)
        } catch {
            // Clean up mapping on failure
            defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
            defaults?.removeObject(forKey: "sessionURL-\(accountId)")
            defaults?.removeObject(forKey: "authType-\(accountId)")
            throw error
        }
    }

    private func removeDomain(accountId: String) async {
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
            try await NSFileProviderManager.remove(domain)
        } catch {
            print("FileProvider domain removal failed: \(error.localizedDescription)")
        }

        // Clean node cache and blob cache
        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            let nodeCache = containerURL.appendingPathComponent("nodes-\(accountId).json")
            try? FileManager.default.removeItem(at: nodeCache)
            let blobDir = containerURL.appendingPathComponent("blobs-\(accountId)")
            try? FileManager.default.removeItem(at: blobDir)
        }

        // Clean mapping
        defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
        defaults?.removeObject(forKey: "sessionURL-\(accountId)")
        defaults?.removeObject(forKey: "authType-\(accountId)")
    }

    func cleanupOrphanedDomains() async {
        let domains = await listDomains()
        let knownIds = Set(logins.flatMap { $0.accounts.map { $0.accountId } })
        for domain in domains {
            if !knownIds.contains(domain.identifier.rawValue) {
                try? await NSFileProviderManager.remove(domain)
            }
        }
    }

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
            kSecAttrService as String: Self.loginKeychainService,
            kSecAttrAccount as String: loginId,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecUseDataProtectionKeychain as String: true,
            kSecAttrAccessGroup as String: Self.appGroupId,
        ]
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
            kSecAttrService as String: Self.loginKeychainService,
            kSecAttrAccount as String: loginId,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecUseDataProtectionKeychain as String: true,
            kSecAttrAccessGroup as String: Self.appGroupId,
        ]
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
            migrateFromLegacy()
            return
        }
        logins = decoded
    }

    private func saveState() {
        if logins.isEmpty, let existing = defaults?.data(forKey: "logins"), !existing.isEmpty {
            if (try? JSONDecoder().decode([LoginInfo].self, from: existing))?.isEmpty == false {
                return
            }
        }
        guard let data = try? JSONEncoder().encode(logins) else { return }
        defaults?.set(data, forKey: "logins")
    }

    private func migrateFromLegacy() {
        guard let data = defaults?.data(forKey: "accounts"),
              let oldAccounts = try? JSONDecoder().decode([LegacyAccountInfo].self, from: data)
        else { return }

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
            defaults?.removeObject(forKey: "accounts")
        }
    }
}

private struct LegacyAccountInfo: Codable {
    let accountId: String
    let displayName: String
    var status: String
}
