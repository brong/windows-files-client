import FileProvider
import SwiftUI

#if os(macOS)
struct MenuBarView: View {
    @ObservedObject var appState: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if appState.logins.isEmpty {
            Text("No accounts configured")
                .foregroundColor(.secondary)
            Divider()
            Button("Add Login...") {
                appState.showingAddAccount = true
                openSettings()
            }
            Button("Settings...") {
                openSettings()
            }
        } else {
            ForEach(appState.logins) { login in
                Text(login.displayLabel)
                    .font(.caption)
                    .foregroundColor(.secondary)

                ForEach(login.accounts.filter { $0.isSynced }) { account in
                    Button {
                        openInFinder(accountId: account.accountId)
                    } label: {
                        let status = appState.liveStatus(for: account.accountId)
                        Label {
                            Text(account.displayName.isEmpty ? account.accountId : account.displayName)
                        } icon: {
                            Image(systemName: statusIcon(for: status))
                                .foregroundColor(statusColor(for: status))
                        }
                    }
                }
            }
            Divider()
            Button("Sync All") {
                for acct in appState.syncedAccounts {
                    appState.syncNow(acct.accountId)
                }
            }
            Button("Settings...") {
                openSettings()
            }
        }
        Divider()
        Button("Quit") {
            NSApplication.shared.terminate(nil)
        }
        .keyboardShortcut("q")
    }

    private func openSettings() {
        NSApp.activate(ignoringOtherApps: true)
        openWindow(id: "settings")
    }

    private func openInFinder(accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
            displayName: "")
        if let manager = NSFileProviderManager(for: domain) {
            manager.getUserVisibleURL(for: .rootContainer) { url, _ in
                if let url = url {
                    DispatchQueue.main.async {
                        NSWorkspace.shared.activateFileViewerSelecting([url])
                    }
                }
            }
        }
    }

    private func statusIcon(for status: SyncStatus) -> String {
        switch status {
        case .idle: return "checkmark.circle.fill"
        case .syncing: return "arrow.triangle.2.circlepath"
        case .error: return "exclamationmark.triangle.fill"
        case .offline: return "wifi.slash"
        case .paused: return "pause.circle.fill"
        case .notSynced: return "circle"
        }
    }

    private func statusColor(for status: SyncStatus) -> Color {
        switch status {
        case .idle: return .green
        case .syncing: return .blue
        case .error: return .red
        case .offline: return .gray
        case .paused: return .orange
        case .notSynced: return .gray
        }
    }
}
#endif
