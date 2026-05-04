import SwiftUI
import Combine
import FileProvider
import JmapClient

/// Thin coordinator that owns the service objects and exposes a clean
/// interface to the views. No business logic here — delegates to services.
@MainActor
final class AppViewModel: ObservableObject {
    // MARK: - Published state (forwarded from services)

    @Published var logins: [LoginInfo] = []
    @Published var isOnline = true
    @Published var showingAddAccount = false
    @Published var extensionStatuses: [String: ExtensionStatus] = [:]
    @Published var activitySnapshot: ActivityTracker.Snapshot? = nil
    @Published var quotaInfo: [String: QuotaInfo] = [:]

    // MARK: - Services

    private let credentials: CredentialStore
    private let accountStore: AccountStore
    private let registrar: DomainRegistrar
    private var statusMonitor: StatusMonitor?

    private var cancellables = Set<AnyCancellable>()

    #if os(macOS)
    static let appGroupId = "BJL34Q426G.com.fastmail.files"
    #else
    static let appGroupId = "group.com.fastmail.files"
    #endif

    private let defaults: UserDefaults?

    // MARK: - Init

    init() {
        let defaults = UserDefaults(suiteName: Self.appGroupId)
        self.defaults = defaults
        self.credentials = CredentialStore(appGroupId: Self.appGroupId)
        self.accountStore = AccountStore(defaults: defaults)
        self.registrar = DomainRegistrar(appGroupId: Self.appGroupId, defaults: defaults)

        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            TrafficLog.shared.configure(containerURL: containerURL)

            let monitor = StatusMonitor(
                containerURL: containerURL,
                knownAccountIds: { [weak self] in
                    Set(self?.logins.flatMap { $0.accounts.map { $0.accountId } } ?? [])
                })
            self.statusMonitor = monitor

            // Sync statuses & activity from monitor into our @Published props
            monitor.$statuses
                .receive(on: RunLoop.main)
                .assign(to: &$extensionStatuses)
            monitor.$activitySnapshot
                .receive(on: RunLoop.main)
                .assign(to: &$activitySnapshot)

            monitor.startObserving()
        }

        // Sync logins from accountStore into our @Published logins
        accountStore.$logins
            .receive(on: RunLoop.main)
            .assign(to: &$logins)

        Task { await checkAllConnections() }
    }

    // MARK: - Computed Properties (forwarded exactly as in AppState)

    var syncedAccounts: [AccountInfo] {
        logins.flatMap { $0.accounts.filter { $0.isSynced } }
    }

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

            switch login.connectionStatus {
            case .authFailed:                           return .error
            case .networkError:                         return .offline
            case .connecting, .unknown, .connected:     return .syncing
            }
        }
        return .notSynced
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

    var totalBlockedUploadCount: Int {
        extensionStatuses.values.reduce(0) { $0 + $1.blockedUploadCount }
    }

    // MARK: - Reload (for SettingsView compatibility)

    func reloadExtensionStatuses() {
        statusMonitor?.reload()
    }

    // MARK: - Connection Check

    func checkAllConnections() async {
        for i in accountStore.logins.indices {
            let login = accountStore.logins[i]
            accountStore.logins[i].connectionStatus = .connecting

            guard let url = URL(string: login.sessionURL) else {
                accountStore.logins[i].connectionStatus = .networkError
                continue
            }

            guard let tokenProvider = credentials.makeTokenProvider(for: login) else {
                accountStore.logins[i].connectionStatus = .authFailed
                continue
            }

            let sessionManager = SessionManager(sessionURL: url, tokenProvider: tokenProvider)
            do {
                _ = try await sessionManager.session()
                accountStore.logins[i].connectionStatus = .connected
            } catch let error as JmapError {
                switch error {
                case .unauthorized, .forbidden:
                    accountStore.logins[i].connectionStatus = .authFailed
                default:
                    accountStore.logins[i].connectionStatus = .networkError
                }
            } catch {
                accountStore.logins[i].connectionStatus = .networkError
            }
        }
        accountStore.save()
    }

    // MARK: - Add Login

    func addLogin(loginId: String, sessionURL: String, credential: OAuthCredential,
                  discoveredAccounts: [(accountId: String, name: String, isPrimary: Bool)],
                  selectedAccountIds: Set<String>) async throws {
        guard !logins.contains(where: { $0.loginId == loginId }) else { return }

        try credentials.storeCredential(credential, for: loginId)
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
                try await registrar.register(
                    accountId: accounts[i].accountId, displayName: accounts[i].displayName,
                    loginId: loginId, sessionURL: sessionURL, authType: .oauth)
            } catch {
                accounts[i].isSynced = false
                accounts[i].status = .error
            }
        }

        accountStore.add(login: LoginInfo(
            loginId: loginId, sessionURL: sessionURL,
            authType: .oauth, accounts: accounts, connectionStatus: .connected))
    }

    func addLoginWithToken(loginId: String, sessionURL: String, token: String,
                           discoveredAccounts: [(accountId: String, name: String, isPrimary: Bool)],
                           selectedAccountIds: Set<String>) async throws {
        guard !logins.contains(where: { $0.loginId == loginId }) else { return }

        try credentials.storeToken(token, for: loginId)
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
                try await registrar.register(
                    accountId: accounts[i].accountId, displayName: accounts[i].displayName,
                    loginId: loginId, sessionURL: sessionURL, authType: .token)
            } catch {
                accounts[i].isSynced = false
                accounts[i].status = .error
            }
        }

        accountStore.add(login: LoginInfo(
            loginId: loginId, sessionURL: sessionURL,
            authType: .token, accounts: accounts, connectionStatus: .connected))
    }

    // MARK: - Remove Login

    func removeLogin(_ loginId: String) async {
        guard let login = logins.first(where: { $0.loginId == loginId }) else { return }

        for acct in login.accounts {
            if acct.isSynced {
                await registrar.remove(accountId: acct.accountId)
            } else {
                registrar.purgeFiles(accountId: acct.accountId)
            }
        }

        credentials.delete(for: loginId)
        defaults?.removeObject(forKey: "sessionURL-\(loginId)")
        accountStore.remove(loginId: loginId)
        cleanSharedContainerFiles()
    }

    // MARK: - Per-Account Actions

    func enableAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId })
        else { return }

        let login = logins[loginIdx]
        let displayName = logins[loginIdx].accounts[acctIdx].displayName
        do {
            try await registrar.register(
                accountId: accountId, displayName: displayName,
                loginId: loginId, sessionURL: login.sessionURL, authType: login.authType)
        } catch {
            accountStore.update(loginId: loginId) { login in
                if let idx = login.accounts.firstIndex(where: { $0.accountId == accountId }) {
                    login.accounts[idx].status = .error
                }
            }
            return
        }

        accountStore.update(loginId: loginId) { login in
            if let idx = login.accounts.firstIndex(where: { $0.accountId == accountId }) {
                login.accounts[idx].isSynced = true
                login.accounts[idx].status = .idle
            }
        }
    }

    func removeAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId })
        else { return }

        if logins[loginIdx].accounts[acctIdx].isSynced {
            await registrar.remove(accountId: accountId)
        }

        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            let cacheDir = containerURL
                .appendingPathComponent("NodeCache", isDirectory: true)
                .appendingPathComponent(accountId, isDirectory: true)
            try? FileManager.default.removeItem(at: cacheDir)
        }

        accountStore.update(loginId: loginId) { login in
            login.accounts.removeAll { $0.accountId == accountId }
        }
    }

    func disableAccount(loginId: String, accountId: String) async {
        await registrar.remove(accountId: accountId)
        accountStore.update(loginId: loginId) { login in
            if let idx = login.accounts.firstIndex(where: { $0.accountId == accountId }) {
                login.accounts[idx].isSynced = false
                login.accounts[idx].status = .notSynced
            }
        }
    }

    func evictDownloadedFiles(accountId: String) {
        registrar.evict(accountId: accountId)
    }

    func cleanAccount(loginId: String, accountId: String) async {
        guard let loginIdx = logins.firstIndex(where: { $0.loginId == loginId }),
              let acctIdx = logins[loginIdx].accounts.firstIndex(where: { $0.accountId == accountId }),
              logins[loginIdx].accounts[acctIdx].isSynced
        else { return }

        let login = logins[loginIdx]
        let displayName = logins[loginIdx].accounts[acctIdx].displayName

        // Evict downloaded content
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
        if let manager = NSFileProviderManager(for: domain) {
            manager.evictItem(identifier: .rootContainer) { _ in }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }

        await registrar.remove(accountId: accountId)

        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) {
            let cacheDir = containerURL
                .appendingPathComponent("NodeCache", isDirectory: true)
                .appendingPathComponent(accountId, isDirectory: true)
            try? FileManager.default.removeItem(at: cacheDir)
        }

        do {
            try await registrar.register(
                accountId: accountId, displayName: displayName,
                loginId: loginId, sessionURL: login.sessionURL, authType: login.authType)
            accountStore.update(loginId: loginId) { l in
                if let idx = l.accounts.firstIndex(where: { $0.accountId == accountId }) {
                    l.accounts[idx].status = .syncing
                }
            }
            syncNow(accountId)
        } catch {
            accountStore.update(loginId: loginId) { l in
                if let idx = l.accounts.firstIndex(where: { $0.accountId == accountId }) {
                    l.accounts[idx].isSynced = false
                    l.accounts[idx].status = .error
                }
            }
        }
    }

    func updateLoginCredential(loginId: String, credential: OAuthCredential) async {
        guard logins.firstIndex(where: { $0.loginId == loginId }) != nil else { return }

        try? credentials.storeCredential(credential, for: loginId)

        accountStore.update(loginId: loginId) { login in
            login.connectionStatus = .connected
        }

        for acct in logins.first(where: { $0.loginId == loginId })?.accounts ?? [] where acct.isSynced {
            syncNow(acct.accountId)
        }
    }

    func syncNow(_ accountId: String) {
        registrar.signal(accountId: accountId)
    }

    func retryBlockedUploads(for accountId: String) {
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId) else { return }
        Task {
            let db = NodeDatabase(containerURL: containerURL, accountId: accountId)
            await db.resetAllUploadFailures()
            registrar.signal(accountId: accountId)
        }
    }

    func retryAllBlockedUploads() {
        for status in extensionStatuses.values where status.blockedUploadCount > 0 {
            retryBlockedUploads(for: status.accountId)
        }
    }

    func refreshQuota(for accountId: String) {
        guard let login = logins.first(where: { $0.accounts.contains { $0.accountId == accountId } }),
              let url = URL(string: login.sessionURL),
              let tokenProvider = credentials.makeTokenProvider(for: login) else { return }

        Task {
            let sessionManager = SessionManager(sessionURL: url, tokenProvider: tokenProvider)
            let client = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider)
            if let info = try? await client.fetchQuota(accountId: accountId) {
                await MainActor.run { self.quotaInfo[accountId] = info }
            }
        }
    }

    func removeAll() async {
        for login in logins {
            await removeLogin(login.loginId)
        }
        await cleanupOrphanedDomains()
    }

    func cleanupOrphanedDomains() async {
        let knownIds = Set(logins.flatMap { $0.accounts.map { $0.accountId } })
        await registrar.cleanOrphaned(knownIds: knownIds)
    }

    func listDomains() async -> [NSFileProviderDomain] {
        await registrar.listDomains()
    }

    // MARK: - Private Helpers

    private func cleanSharedContainerFiles() {
        guard logins.isEmpty,
              let containerURL = FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: Self.appGroupId)
        else { return }
        try? FileManager.default.removeItem(at: containerURL.appendingPathComponent("activity.json"))
        try? FileManager.default.removeItem(at: containerURL.appendingPathComponent("jmap-traffic.log"))
    }
}
