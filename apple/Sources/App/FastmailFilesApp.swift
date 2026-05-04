import SwiftUI
import FileProvider
import JmapClient

@main
struct FastmailFilesApp: App {
    @StateObject private var appState = AppState()
    @StateObject private var updateManager = UpdateManager()
    #if os(macOS)
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @Environment(\.openWindow) private var openWindow
    #endif

    var body: some Scene {
        #if os(macOS)
        MenuBarExtra {
            MenuBarView(appState: appState, updateManager: updateManager)
        } label: {
            MenuBarIconLabel(state: appState.menuBarState)
        }

        Window("Fastmail Files Settings", id: "settings") {
            SettingsView(appState: appState)
                .onReceive(NotificationCenter.default.publisher(for: .openSettings)) { _ in
                    openWindow(id: "settings")
                }
        }

        Window("Diagnostics", id: "diagnostics") {
            DiagnosticsView(appState: appState)
        }
        .defaultSize(width: 420, height: 280)
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

/// Menu bar icon that changes symbol and color based on sync state.
struct MenuBarIconLabel: View {
    let state: MenuBarState

    var body: some View {
        if #available(macOS 14.0, *), state.isSyncing {
            Image(systemName: state.symbolName)
                .symbolEffect(.variableColor.iterative, options: .repeating)
                .foregroundStyle(iconColor)
        } else {
            Image(systemName: state.symbolName)
                .foregroundStyle(iconColor)
        }
    }

    private var iconColor: Color {
        switch state {
        case .pending: return .orange
        case .error:   return .red
        default:       return .primary
        }
    }
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
    /// Extension-reported statuses, keyed by accountId. Single source of truth.
    @Published var extensionStatuses: [String: ExtensionStatus] = [:]
    /// Full activity snapshot read directly from activity.json.
    @Published var activitySnapshot: ActivityTracker.Snapshot? = nil
    /// Quota info per accountId, fetched on demand.
    @Published var quotaInfo: [String: QuotaInfo] = [:]

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

    /// Get the live status for an account.
    /// ExtensionStatus is the single source of truth for account sync state.
    func liveStatus(for accountId: String) -> SyncStatus {
        for login in logins {
            guard let acct = login.accounts.first(where: { $0.accountId == accountId }) else { continue }
            if !acct.isSynced { return .notSynced }

            if let extStatus = extensionStatuses[accountId] {
                let hasActive = extStatus.activeOperationCount > 0
                switch extStatus.state {
                case .initializing: return hasActive ? .syncing : .idle
                case .syncing:      return .syncing
                case .idle:         return hasActive ? .syncing : .idle
                case .error:        return .error
                case .offline:      return .offline
                }
            }

            // No extension status yet — use connection check result as fallback
            switch login.connectionStatus {
            case .authFailed:               return .error
            case .networkError:             return .offline
            case .connecting, .unknown, .connected: return .syncing
            }
        }
        return .notSynced
    }

    /// Reload extension statuses from shared container.
    func reloadExtensionStatuses() {
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) else { return }
        let reader = ExtensionStatusReader(containerURL: containerURL)
        var newStatuses: [String: ExtensionStatus] = [:]
        for status in reader.allStatuses() {
            newStatuses[status.accountId] = status
        }
        extensionStatuses = newStatuses
        activitySnapshot = ActivityTracker.loadShared(containerURL: containerURL)
    }

    init() {
        self.defaults = UserDefaults(suiteName: Self.appGroupId)
        if let containerURL = FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            TrafficLog.shared.configure(containerURL: containerURL)
        }
        loadState()
        Task { await checkAllConnections() }
        startObservers()
    }

    private func startObservers() {
        let center = CFNotificationCenterGetDarwinNotifyCenter()
        let observer = Unmanaged.passUnretained(self).toOpaque()
        let callback: CFNotificationCallback = { _, observer, _, _, _ in
            guard let observer = observer else { return }
            let state = Unmanaged<AppState>.fromOpaque(observer).takeUnretainedValue()
            DispatchQueue.main.async { state.reloadExtensionStatuses() }
        }
        // Extension status updates (state, nodeCount, operationHints)
        CFNotificationCenterAddObserver(center, observer, callback,
            ExtensionStatusReader.notificationName, nil, .deliverImmediately)
        // Activity updates (full pending/active/error list from activity.json)
        CFNotificationCenterAddObserver(center, observer, callback,
            ActivityTracker.darwinNotificationName, nil, .deliverImmediately)
        reloadExtensionStatuses()
    }

    var menuBarState: MenuBarState {
        let statuses = Array(extensionStatuses.values)
        return MenuBarState.derive(
            pendingCount: statuses.reduce(0) { $0 + $1.pendingOperationCount },
            activeCount: statuses.reduce(0) { $0 + $1.activeOperationCount },
            extensionStatuses: statuses,
            isOnline: isOnline
        )
    }

    var activeOperationHints: [ExtensionStatus.OperationHint] {
        extensionStatuses.values.flatMap { $0.operationHints }
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

        for acct in login.accounts {
            if acct.isSynced {
                await removeDomain(accountId: acct.accountId)
            } else {
                // Not synced so no FileProvider domain to remove, but the extension
                // may have created database/cache files — clean those up too.
                await purgeAccountFiles(accountId: acct.accountId)
            }
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
        cleanSharedContainerFiles()
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

    func removeAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId })
        else { return }

        if logins[loginIdx].accounts[acctIdx].isSynced {
            await removeDomain(accountId: accountId)
        }

        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            let cacheDir = containerURL
                .appendingPathComponent("NodeCache", isDirectory: true)
                .appendingPathComponent(accountId, isDirectory: true)
            try? FileManager.default.removeItem(at: cacheDir)
        }

        logins[loginIdx].accounts.remove(at: acctIdx)
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

    /// Evict all downloaded file content for an account without touching sync state.
    /// The extension will re-download files on demand when opened.
    /// Use this to free up disk space without losing the file list or re-syncing metadata.
    func evictDownloadedFiles(accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
        NSFileProviderManager(for: domain)?.evictItem(identifier: .rootContainer) { _ in }
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

        // Delete the local node cache so the re-registered domain starts fresh
        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            let cacheDir = containerURL
                .appendingPathComponent("NodeCache", isDirectory: true)
                .appendingPathComponent(accountId, isDirectory: true)
            try? FileManager.default.removeItem(at: cacheDir)
        }

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

    var totalBlockedUploadCount: Int {
        extensionStatuses.values.reduce(0) { $0 + $1.blockedUploadCount }
    }

    /// Clear all upload failure records for an account and re-trigger the system
    /// so it retries the stuck items.
    func retryBlockedUploads(for accountId: String) {
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) else { return }
        Task {
            let db = NodeDatabase(containerURL: containerURL, accountId: accountId)
            await db.resetAllUploadFailures()
            // Signal the working set so the system re-drives the pending creates/modifies.
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
            NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
        }
    }

    func retryAllBlockedUploads() {
        for status in extensionStatuses.values where status.blockedUploadCount > 0 {
            retryBlockedUploads(for: status.accountId)
        }
    }

    func refreshQuota(for accountId: String) {
        guard let login = logins.first(where: { $0.accounts.contains { $0.accountId == accountId } }),
              let url = URL(string: login.sessionURL) else { return }

        let tokenProvider = makeTokenProvider(for: login)
        guard let tokenProvider else { return }

        Task {
            let sessionManager = SessionManager(sessionURL: url, tokenProvider: tokenProvider)
            let client = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider)
            if let info = try? await client.fetchQuota(accountId: accountId) {
                await MainActor.run { self.quotaInfo[accountId] = info }
            }
        }
    }

    private func makeTokenProvider(for login: LoginInfo) -> TokenProvider? {
        let kcService = Self.loginKeychainService
        let appGroup = Self.appGroupId
        let lid = login.loginId
        if login.authType == .oauth, let credential = loadLoginCredential(loginId: lid) {
            return OAuthTokenProvider(credential: credential, onTokenRefreshed: { updated in
                let encoder = JSONEncoder()
                encoder.dateEncodingStrategy = .iso8601
                if let data = try? encoder.encode(updated),
                   let str = String(data: data, encoding: .utf8) {
                    try? KeychainTokenProvider.storeToken(
                        str, service: kcService, account: lid, accessGroup: appGroup)
                }
            })
        } else if let token = loadLoginToken(loginId: lid) {
            return StaticTokenProvider(token: token)
        }
        return nil
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

            if let containerURL = FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: Self.appGroupId) {
                // Belt-and-suspenders: clear the state token so enumerateWorkingSet is
                // forced on the next extension wake. removeDomain tries to delete the
                // whole NodeCache directory, but that can fail silently when the extension
                // process still has the SQLite file open. Writing "" via the DB API is
                // atomic and survives that race.
                let db = NodeDatabase(containerURL: containerURL, accountId: accountId)
                await db.setStateToken("")
                await db.setEnumerationFailureCount(0)

                // Pre-write a syncing status so the UI can't show "Up to date" before
                // enumerateWorkingSet has actually run.
                let writer = ExtensionStatusWriter(containerURL: containerURL, accountId: accountId)
                writer.setSyncing()
            }

            // Signal the system to drive enumerateItems on the working set.
            NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
        } catch {
            // Clean up mapping on failure
            defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
            defaults?.removeObject(forKey: "sessionURL-\(accountId)")
            defaults?.removeObject(forKey: "authType-\(accountId)")
            throw error
        }
    }

    /// Delete all local files for an account without touching the FileProvider domain.
    /// Used for accounts that were never synced (no registered domain).
    private func purgeAccountFiles(accountId: String) async {
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) else { return }
        let nodeCacheDir = containerURL
            .appendingPathComponent("NodeCache", isDirectory: true)
            .appendingPathComponent(accountId, isDirectory: true)
        try? FileManager.default.removeItem(at: nodeCacheDir)
        let blobDir = containerURL.appendingPathComponent("blobs-\(accountId)")
        try? FileManager.default.removeItem(at: blobDir)
        let sessionCache = containerURL.appendingPathComponent("session-\(accountId).json")
        try? FileManager.default.removeItem(at: sessionCache)
        let statusFile = containerURL.appendingPathComponent("status-\(accountId).json")
        try? FileManager.default.removeItem(at: statusFile)
        defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
        defaults?.removeObject(forKey: "sessionURL-\(accountId)")
        defaults?.removeObject(forKey: "authType-\(accountId)")
        RoleCache.clear(accountId: accountId, defaults: defaults)
    }

    private func removeDomain(accountId: String) async {
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
            try await NSFileProviderManager.remove(domain)
        } catch {
            print("FileProvider domain removal failed: \(error.localizedDescription)")
        }

        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            // Node cache (SQLite) and blob cache
            let nodeCacheDir = containerURL
                .appendingPathComponent("NodeCache", isDirectory: true)
                .appendingPathComponent(accountId, isDirectory: true)
            try? FileManager.default.removeItem(at: nodeCacheDir)
            let blobDir = containerURL.appendingPathComponent("blobs-\(accountId)")
            try? FileManager.default.removeItem(at: blobDir)
            // Session document disk cache
            let sessionCache = containerURL.appendingPathComponent("session-\(accountId).json")
            try? FileManager.default.removeItem(at: sessionCache)
            // Extension status file
            let statusFile = containerURL.appendingPathComponent("status-\(accountId).json")
            try? FileManager.default.removeItem(at: statusFile)
        }

        // Per-account UserDefaults keys
        defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
        defaults?.removeObject(forKey: "sessionURL-\(accountId)")
        defaults?.removeObject(forKey: "authType-\(accountId)")
        RoleCache.clear(accountId: accountId, defaults: defaults)
    }

    private func cleanSharedContainerFiles() {
        guard logins.isEmpty,
              let containerURL = FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: Self.appGroupId)
        else { return }
        try? FileManager.default.removeItem(at: containerURL.appendingPathComponent("activity.json"))
        try? FileManager.default.removeItem(at: containerURL.appendingPathComponent("jmap-traffic.log"))
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
