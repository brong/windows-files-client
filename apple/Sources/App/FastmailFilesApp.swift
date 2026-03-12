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

        Settings {
            SettingsView(appState: appState)
        }
        #else
        WindowGroup {
            ContentView(appState: appState)
        }
        #endif
    }
}

// MARK: - App State

@MainActor
class AppState: ObservableObject {
    @Published var accounts: [AccountInfo] = []
    @Published var isOnline = true

    private let defaults: UserDefaults?

    // TODO: Replace with actual team ID
    #if os(macOS)
    static let appGroupId = "TEAMID.com.fastmail.files"
    #else
    static let appGroupId = "group.com.fastmail.files"
    #endif

    var statusIcon: String {
        if !isOnline { return "cloud.slash" }
        if accounts.contains(where: { $0.status == .error }) { return "exclamationmark.cloud" }
        if accounts.contains(where: { $0.status == .syncing }) { return "arrow.triangle.2.circlepath.cloud" }
        return "checkmark.cloud"
    }

    init() {
        self.defaults = UserDefaults(suiteName: Self.appGroupId)
        loadAccounts()
    }

    func addAccount(sessionURL: String, token: String) async throws {
        guard let url = URL(string: sessionURL) else {
            throw JmapError.invalidResponse
        }

        let tokenProvider = StaticTokenProvider(token: token)
        let sessionManager = SessionManager(sessionURL: url, tokenProvider: tokenProvider)
        let session = try await sessionManager.session()

        guard let accountId = session.fileNodeAccountId() else {
            throw JmapError.noAccountId
        }

        guard let account = session.accounts[accountId] else {
            throw JmapError.noAccountId
        }

        // Store token in shared Keychain
        try KeychainTokenProvider.storeToken(
            token,
            account: accountId,
            accessGroup: Self.appGroupId
        )

        // Store session URL in shared UserDefaults
        defaults?.set(sessionURL, forKey: "sessionURL-\(accountId)")

        // Register FileProvider domain
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
            displayName: "Fastmail Files (\(account.name))"
        )

        try await NSFileProviderManager.add(domain)

        let info = AccountInfo(
            accountId: accountId,
            displayName: account.name,
            status: .idle
        )

        accounts.append(info)
        saveAccounts()
    }

    func removeAccount(_ accountId: String) async throws {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
            displayName: ""
        )

        try await NSFileProviderManager.remove(domain)

        // Clean up stored credentials
        defaults?.removeObject(forKey: "sessionURL-\(accountId)")

        accounts.removeAll { $0.accountId == accountId }
        saveAccounts()
    }

    func syncNow(_ accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
            displayName: ""
        )
        NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
    }

    private func loadAccounts() {
        guard let data = defaults?.data(forKey: "accounts"),
              let decoded = try? JSONDecoder().decode([AccountInfo].self, from: data)
        else { return }
        accounts = decoded
    }

    private func saveAccounts() {
        guard let data = try? JSONEncoder().encode(accounts) else { return }
        defaults?.set(data, forKey: "accounts")
    }
}

// MARK: - Account Info

struct AccountInfo: Codable, Identifiable {
    let accountId: String
    let displayName: String
    var status: SyncStatus

    var id: String { accountId }
}

enum SyncStatus: String, Codable {
    case idle
    case syncing
    case error
    case offline
    case paused
}
