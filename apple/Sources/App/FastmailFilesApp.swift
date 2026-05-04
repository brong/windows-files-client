import SwiftUI
import FileProvider
import JmapClient

@main
struct FastmailFilesApp: App {
    @StateObject private var appState = AppViewModel()
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
        for window in sender.windows {
            if window.canBecomeKey {
                window.makeKeyAndOrderFront(nil)
                return false
            }
        }
        NotificationCenter.default.post(name: .openSettings, object: nil)
        return true
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
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

// MARK: - Legacy Migration Helper

struct LegacyAccountInfo: Codable {
    let accountId: String
    let displayName: String
    var status: String
}
