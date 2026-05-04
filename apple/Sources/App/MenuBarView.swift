import FileProvider
import SwiftUI
import JmapClient

#if os(macOS)
struct MenuBarView: View {
    @ObservedObject var appState: AppViewModel
    @ObservedObject var updateManager: UpdateManager
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        let state = appState.menuBarState

        // Status header — always visible, non-interactive
        HStack(spacing: 6) {
            Image(systemName: state.symbolName)
                .foregroundColor(headerIconColor(for: state))
                .imageScale(.medium)
            Text(state.statusText)
                .font(.callout)
                .fontWeight(.medium)
            Spacer()
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)

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
            // Blocked uploads — shown above everything else so the user can't miss it
            let blocked = appState.totalBlockedUploadCount
            if blocked > 0 {
                Button {
                    appState.retryAllBlockedUploads()
                } label: {
                    let noun = blocked == 1 ? "upload" : "uploads"
                    Label("Retry \(blocked) stuck \(noun)", systemImage: "exclamationmark.arrow.triangle.2.circlepath")
                        .foregroundColor(.red)
                }
                Divider()
            }

            // Active/pending operations section — shown when operations are in flight
            if !appState.activeOperationHints.isEmpty {
                operationSection(hints: appState.activeOperationHints)
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
        Button("Check for Updates…") {
            updateManager.checkForUpdates()
        }
        Divider()
        Button("Quit") {
            NSApplication.shared.terminate(nil)
        }
        .keyboardShortcut("q")
    }

    @ViewBuilder
    private func operationSection(hints: [ExtensionStatus.OperationHint]) -> some View {
        let shown = hints.prefix(5)
        ForEach(shown) { hint in
            HStack(spacing: 6) {
                Image(systemName: iconForVerb(hint.actionVerb))
                    .foregroundColor(.blue)
                    .frame(width: 16)
                VStack(alignment: .leading, spacing: 1) {
                    Text(hint.fileName).lineLimit(1)
                    Text(hint.actionVerb).font(.caption).foregroundColor(.secondary)
                }
            }
            .padding(.vertical, 2)
        }
        if hints.count > 5 {
            Text("and \(hints.count - 5) more…")
                .font(.caption)
                .foregroundColor(.secondary)
        }
    }

    private func iconForVerb(_ verb: String) -> String {
        switch verb {
        case "Uploading":   return "arrow.up.doc"
        case "Downloading": return "arrow.down.doc"
        case "Deleting":    return "trash"
        default:            return "arrow.triangle.2.circlepath"
        }
    }

    private func headerIconColor(for state: MenuBarState) -> Color {
        switch state {
        case .pending: return .orange
        case .error:   return .red
        default:       return .primary
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
