import SwiftUI
import JmapClient
#if canImport(AppKit)
import AppKit
#endif

#if os(macOS)
struct DiagnosticsView: View {
    @ObservedObject var appState: AppState
    @State private var reportURL: URL?
    @State private var isBuilding = false
    @State private var buildError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Diagnostics")
                .font(.title2)
                .bold()

            Text("Export a report for Fastmail support. No file contents or personal data are included — only account IDs, node counts, and error status.")
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if let err = buildError {
                Text(err).foregroundColor(.red).font(.caption)
            }

            HStack(spacing: 12) {
                Button("Build Report") {
                    isBuilding = true
                    buildError = nil
                    reportURL = nil
                    Task {
                        do {
                            reportURL = try await buildReport()
                        } catch {
                            buildError = error.localizedDescription
                        }
                        isBuilding = false
                    }
                }
                .disabled(isBuilding)

                if isBuilding {
                    ProgressView().scaleEffect(0.7)
                }

                if let url = reportURL {
                    ShareLink(item: url, subject: Text("Fastmail Files Diagnostics"))
                }
            }
        }
        .padding()
        .frame(width: 420)
    }

    // MARK: - Report builder

    private func buildReport() async throws -> URL {
        var lines: [String] = []

        lines.append("=== Fastmail Files Diagnostic Report ===")
        lines.append("Date:    \(Date())")

        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build   = info?["CFBundleVersion"] as? String ?? "?"
        lines.append("Version: \(version) (\(build))")
        lines.append("OS:      \(ProcessInfo.processInfo.operatingSystemVersionString)")
        lines.append("")

        lines.append("=== Accounts ===")
        let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: AppState.appGroupId
        )

        for login in appState.logins {
            lines.append("Login \(login.loginId): connection=\(login.connectionStatus)")
            for acct in login.accounts {
                let status = appState.liveStatus(for: acct.accountId)
                lines.append("  Account \(acct.accountId): synced=\(acct.isSynced) status=\(status)")
                if let container = containerURL {
                    let db = NodeDatabase(containerURL: container, accountId: acct.accountId)
                    let nodeCount = await db.count
                    let hasToken  = await db.stateToken != nil
                    lines.append("  DB: \(nodeCount) nodes  stateToken=\(hasToken ? "yes" : "no")")
                }
            }
        }
        lines.append("")

        lines.append("=== Recent Activity (last 20) ===")
        for activity in appState.recentActivities.prefix(20) {
            lines.append("[\(activity.status.rawValue)] \(activity.action.rawValue) — \(activity.accountId)")
        }

        if !appState.pendingActivities.isEmpty {
            lines.append("")
            lines.append("=== Pending Uploads (\(appState.pendingActivities.count)) ===")
            for activity in appState.pendingActivities.prefix(20) {
                lines.append("  \(activity.action.rawValue) — \(activity.accountId)")
            }
        }

        let text = lines.joined(separator: "\n") + "\n"
        let tmpURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("fastmail-files-diag-\(Int(Date().timeIntervalSince1970)).txt")
        try text.write(to: tmpURL, atomically: true, encoding: .utf8)
        return tmpURL
    }
}
#endif
