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

// MARK: - App State

@MainActor
class AppState: ObservableObject {
    @Published var accounts: [AccountInfo] = []
    @Published var isOnline = true
    @Published var showingAddAccount = false

    private let defaults: UserDefaults?

    #if os(macOS)
    static let appGroupId = "BJL34Q426G.com.fastmail.files"
    #else
    static let appGroupId = "group.com.fastmail.files"
    #endif

    var statusIcon: String {
        if !isOnline { return "icloud.slash" }
        if accounts.contains(where: { $0.status == .error }) { return "exclamationmark.icloud" }
        if accounts.contains(where: { $0.status == .syncing }) { return "arrow.clockwise.icloud" }
        return "icloud"
    }

    init() {
        self.defaults = UserDefaults(suiteName: Self.appGroupId)
        loadAccounts()
    }

    /// Add an account with a static token (app password).
    func addAccount(accountId: String, displayName: String,
                    sessionURL: String, token: String) async throws {
        // Skip if already added
        guard !accounts.contains(where: { $0.accountId == accountId }) else { return }

        try KeychainTokenProvider.storeToken(
            token, account: accountId, accessGroup: Self.appGroupId)

        defaults?.set(sessionURL, forKey: "sessionURL-\(accountId)")

        try await registerDomain(accountId: accountId, displayName: displayName)

        accounts.append(AccountInfo(accountId: accountId, displayName: displayName, status: .idle))
        saveAccounts()
    }

    /// Add an account with OAuth credentials.
    func addAccountWithOAuth(accountId: String, displayName: String,
                             sessionURL: String, credential: OAuthCredential) async throws {
        // Skip if already added
        guard !accounts.contains(where: { $0.accountId == accountId }) else { return }

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let credData = try encoder.encode(credential)
        let credString = String(data: credData, encoding: .utf8)!
        try KeychainTokenProvider.storeToken(
            credString, account: accountId, accessGroup: Self.appGroupId)

        defaults?.set(sessionURL, forKey: "sessionURL-\(accountId)")
        defaults?.set("oauth", forKey: "authType-\(accountId)")

        try await registerDomain(accountId: accountId, displayName: displayName)

        accounts.append(AccountInfo(accountId: accountId, displayName: displayName, status: .idle))
        saveAccounts()
    }

    private func registerDomain(accountId: String, displayName: String) async throws {
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

    func removeAccount(_ accountId: String) async throws {
        // Try to remove FileProvider domain
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
                displayName: ""
            )
            try await NSFileProviderManager.remove(domain)
        } catch {
            print("FileProvider domain removal skipped: \(error.localizedDescription)")
        }

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
