import Foundation

/// Owns the [LoginInfo] array and its UserDefaults persistence.
/// Pure data — no network calls or domain registration.
@MainActor
final class AccountStore: ObservableObject {
    @Published var logins: [LoginInfo] = []

    private let defaults: UserDefaults?

    init(defaults: UserDefaults?) {
        self.defaults = defaults
        load()
    }

    // MARK: - Persistence

    func save() {
        guard let data = try? JSONEncoder().encode(logins) else { return }
        defaults?.set(data, forKey: "logins")
    }

    func load() {
        guard let data = defaults?.data(forKey: "logins"),
              let decoded = try? JSONDecoder().decode([LoginInfo].self, from: data)
        else {
            migrate()
            return
        }
        logins = decoded
    }

    func migrate() {
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
            save()
            defaults?.removeObject(forKey: "accounts")
        }
    }

    // MARK: - CRUD

    func add(login: LoginInfo) {
        logins.append(login)
        save()
    }

    func remove(loginId: String) {
        logins.removeAll { $0.loginId == loginId }
        save()
    }

    func update(loginId: String, _ modify: (inout LoginInfo) -> Void) {
        guard let idx = logins.firstIndex(where: { $0.loginId == loginId }) else { return }
        modify(&logins[idx])
        save()
    }
}
