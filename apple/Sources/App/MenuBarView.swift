import SwiftUI

#if os(macOS)
struct MenuBarView: View {
    @ObservedObject var appState: AppState

    var body: some View {
        if appState.accounts.isEmpty {
            Text("No accounts configured")
                .foregroundColor(.secondary)
            Divider()
            Button("Add Account...") {
                NSApp.activate(ignoringOtherApps: true)
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
            }
        } else {
            ForEach(appState.accounts) { account in
                HStack {
                    Image(systemName: statusIcon(for: account.status))
                        .foregroundColor(statusColor(for: account.status))
                    VStack(alignment: .leading) {
                        Text(account.displayName)
                            .font(.body)
                        Text(statusText(for: account.status))
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }

                Button("Sync Now") {
                    appState.syncNow(account.accountId)
                }
            }
            Divider()
            Button("Settings...") {
                NSApp.activate(ignoringOtherApps: true)
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
            }
        }
        Divider()
        Button("Quit") {
            NSApplication.shared.terminate(nil)
        }
        .keyboardShortcut("q")
    }

    private func statusIcon(for status: SyncStatus) -> String {
        switch status {
        case .idle: return "checkmark.circle.fill"
        case .syncing: return "arrow.triangle.2.circlepath"
        case .error: return "exclamationmark.triangle.fill"
        case .offline: return "wifi.slash"
        case .paused: return "pause.circle.fill"
        }
    }

    private func statusColor(for status: SyncStatus) -> Color {
        switch status {
        case .idle: return .green
        case .syncing: return .blue
        case .error: return .red
        case .offline: return .gray
        case .paused: return .orange
        }
    }

    private func statusText(for status: SyncStatus) -> String {
        switch status {
        case .idle: return "Up to date"
        case .syncing: return "Syncing..."
        case .error: return "Error — check settings"
        case .offline: return "Offline"
        case .paused: return "Paused"
        }
    }
}
#endif
