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

            Text("Export a report for Fastmail support. No file contents or personal data are included — only account IDs, node counts, sync status, and JMAP traffic logs.")
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

            Text("To capture system logs: open Console.app, filter by \"com.fastmail.files\", and share the relevant window.")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding()
        .frame(width: 420)
    }

    // MARK: - Report builder

    private func buildReport() async throws -> URL {
        var lines: [String] = []
        let separator = String(repeating: "-", count: 60)

        // Header
        lines.append("=== Fastmail Files Diagnostic Report ===")
        lines.append("Date:    \(Date())")
        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build   = info?["CFBundleVersion"] as? String ?? "?"
        lines.append("Version: \(version) (\(build))")
        lines.append("OS:      \(ProcessInfo.processInfo.operatingSystemVersionString)")
        lines.append("")

        let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: AppState.appGroupId)

        // Accounts + DB stats
        lines.append("=== Accounts ===")
        for login in appState.logins {
            lines.append("Login \(login.loginId)  connection=\(login.connectionStatus)")
            for acct in login.accounts {
                let status = appState.liveStatus(for: acct.accountId)
                lines.append("  Account \(acct.accountId)  synced=\(acct.isSynced)  status=\(status)")
                if let container = containerURL {
                    let db = NodeDatabase(containerURL: container, accountId: acct.accountId)
                    let nodeCount = await db.count
                    let hasToken  = await db.stateToken != nil
                    let failCount = await db.enumerationFailureCount
                    lines.append("    DB: \(nodeCount) nodes  stateToken=\(hasToken ? "yes" : "no")  failCount=\(failCount)")
                }
            }
        }
        lines.append("")

        // Activity snapshot
        lines.append("=== Active Operations ===")
        if let container = containerURL,
           let snapshot = ActivityTracker.loadShared(containerURL: container) {
            let items = snapshot.activities
            if items.isEmpty {
                lines.append("  (none)")
            } else {
                for item in items.prefix(30) {
                    let progress = item.progress.map { String(format: " %.0f%%", $0 * 100) } ?? ""
                    lines.append("  [\(item.status.rawValue)] \(item.action.rawValue) \(item.fileName)\(progress)")
                    if let err = item.error { lines.append("    error: \(err)") }
                }
            }
        } else {
            lines.append("  (no activity.json found)")
        }
        lines.append("")

        // Status files (raw JSON — useful for debugging status state machine)
        lines.append("=== Status Files ===")
        if let container = containerURL {
            let files = (try? FileManager.default.contentsOfDirectory(
                at: container, includingPropertiesForKeys: nil)) ?? []
            let statusFiles = files.filter {
                $0.lastPathComponent.hasPrefix("status-") && $0.pathExtension == "json"
            }.sorted { $0.lastPathComponent < $1.lastPathComponent }

            for fileURL in statusFiles {
                lines.append(separator)
                lines.append(fileURL.lastPathComponent)
                if let text = try? String(contentsOf: fileURL, encoding: .utf8) {
                    // Pretty-print the JSON if possible
                    if let data = text.data(using: .utf8),
                       let obj = try? JSONSerialization.jsonObject(with: data),
                       let pretty = try? JSONSerialization.data(withJSONObject: obj, options: .prettyPrinted),
                       let prettyStr = String(data: pretty, encoding: .utf8) {
                        lines.append(prettyStr)
                    } else {
                        lines.append(text)
                    }
                }
            }
        }
        lines.append("")

        // JMAP traffic log — last 50 KB to keep the report manageable
        lines.append("=== JMAP Traffic Log (last 50 KB) ===")
        if let container = containerURL {
            let trafficURL = container.appendingPathComponent("jmap-traffic.log")
            if let text = try? String(contentsOf: trafficURL, encoding: .utf8) {
                let maxBytes = 50_000
                if text.utf8.count <= maxBytes {
                    lines.append(text)
                } else {
                    // Take the last maxBytes of UTF-8, then find the next newline to avoid mid-line truncation
                    let allBytes = Array(text.utf8)
                    let startIdx = allBytes.count - maxBytes
                    let slice = allBytes[startIdx...]
                    if let truncated = String(bytes: slice, encoding: .utf8) {
                        let firstNewline = truncated.firstIndex(of: "\n") ?? truncated.startIndex
                        lines.append("[... truncated to last 50 KB ...]")
                        lines.append(String(truncated[firstNewline...]))
                    } else {
                        lines.append("[... truncated, UTF-8 boundary error ...]")
                        lines.append(text.suffix(maxBytes / 2).description)
                    }
                }
            } else {
                lines.append("  (jmap-traffic.log not found or unreadable)")
            }
        }

        let text = lines.joined(separator: "\n") + "\n"
        let timestamp = Int(Date().timeIntervalSince1970)
        let tmpURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("fastmail-files-diag-\(timestamp).txt")
        try text.write(to: tmpURL, atomically: true, encoding: .utf8)
        return tmpURL
    }
}
#endif
