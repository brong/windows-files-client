import FileProvider
import SwiftUI
import JmapClient

#if os(macOS)
struct MenuBarView: View {
    @ObservedObject var appState: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        let state = appState.menuBarState

        // Status header — always visible
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Image(systemName: state.symbolName)
                    .foregroundColor(headerIconColor(for: state))
                Text(state.statusText)
                    .font(.callout)
                    .fontWeight(.medium)
            }
        }
        .padding(.vertical, 4)

        Divider()

        if appState.logins.isEmpty {
            Text("No accounts configured")
                .foregroundColor(.secondary)
            Divider()
            Button("Add Login...") {
                appState.showingAddAccount = true
                openSettings()
            }
        } else {
            // Pending changes section — shown when local edits are not yet uploaded
            if !appState.pendingActivities.isEmpty {
                pendingSection(activities: appState.pendingActivities)
                Divider()
            }

            // Per-account buttons
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
        }

        Button("Settings...") {
            openSettings()
        }
        Button("Diagnostics...") {
            openDiagnostics()
        }
        Divider()
        Button("Quit") {
            NSApplication.shared.terminate(nil)
        }
        .keyboardShortcut("q")
    }

    @ViewBuilder
    private func pendingSection(activities: [ActivityTracker.Activity]) -> some View {
        let shown = activities.prefix(5)
        ForEach(shown) { activity in
            HStack(spacing: 6) {
                Image(systemName: pendingIcon(for: activity.action))
                    .foregroundColor(.orange)
                    .frame(width: 16)
                VStack(alignment: .leading, spacing: 1) {
                    Text(activity.fileName)
                        .lineLimit(1)
                    Text(activity.action.rawValue.capitalized)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            .padding(.vertical, 2)
        }
        if activities.count > 5 {
            Text("and \(activities.count - 5) more pending…")
                .font(.caption)
                .foregroundColor(.secondary)
        }
    }

    private func headerIconColor(for state: MenuBarState) -> Color {
        switch state {
        case .pending: return .orange
        case .error:   return .red
        default:       return .primary
        }
    }

    private func pendingIcon(for action: ActivityTracker.Activity.Action) -> String {
        switch action {
        case .upload:   return "arrow.up.doc"
        case .download: return "arrow.down.doc"
        case .delete:   return "trash"
        case .sync:     return "arrow.triangle.2.circlepath"
        }
    }

    private func openSettings() {
        NSApp.activate(ignoringOtherApps: true)
        openWindow(id: "settings")
    }

    private func openDiagnostics() {
        NSApp.activate(ignoringOtherApps: true)
        openWindow(id: "diagnostics")
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
        case .idle:      return "checkmark.circle.fill"
        case .syncing:   return "arrow.triangle.2.circlepath"
        case .error:     return "exclamationmark.triangle.fill"
        case .offline:   return "wifi.slash"
        case .paused:    return "pause.circle.fill"
        case .notSynced: return "circle"
        }
    }

    private func statusColor(for status: SyncStatus) -> Color {
        switch status {
        case .idle:      return .green
        case .syncing:   return .blue
        case .error:     return .red
        case .offline:   return .gray
        case .paused:    return .orange
        case .notSynced: return .gray
        }
    }
}
#endif
