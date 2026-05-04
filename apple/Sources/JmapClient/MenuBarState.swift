import Foundation

/// Derived UI state for the menu bar icon.
/// Computed from ActivityTracker snapshots and ExtensionStatus; never stored directly.
public enum MenuBarState: Equatable {
    case idle
    case syncing(count: Int)
    case pending(count: Int)    // local changes not yet uploaded — orange, data-loss risk
    case paused(reason: PauseReason)
    case error(message: String)
    case offline

    public enum PauseReason: Equatable {
        case cellular
        case lowDataMode
        case userRequested
    }

    public var symbolName: String {
        switch self {
        case .idle:    return "checkmark.icloud"
        case .syncing: return "arrow.triangle.2.circlepath.icloud"
        case .pending: return "icloud.and.arrow.up"
        case .paused:  return "pause.icloud"
        case .error:   return "exclamationmark.icloud"
        case .offline: return "icloud.slash"
        }
    }

    public var statusText: String {
        switch self {
        case .idle:
            return "All files up to date"
        case .syncing(let n):
            return "Syncing \(n) \(n == 1 ? "file" : "files")…"
        case .pending(let n):
            return "\(n) \(n == 1 ? "change" : "changes") not yet uploaded"
        case .paused(.cellular):
            return "On cellular — background sync paused"
        case .paused(.lowDataMode):
            return "Low Data Mode — sync paused"
        case .paused(.userRequested):
            return "Sync paused"
        case .error(let msg):
            return msg
        case .offline:
            return "Offline"
        }
    }

    public var isSyncing: Bool {
        if case .syncing = self { return true }
        return false
    }

    public var isPending: Bool {
        if case .pending = self { return true }
        return false
    }

    public var isError: Bool {
        if case .error = self { return true }
        return false
    }

    /// Derives MenuBarState from raw activity and status data.
    /// Priority order: error > blockedUploads > pending > syncing > offline > idle
    public static func derive(
        pendingCount: Int,
        activeCount: Int,
        extensionStatuses: [ExtensionStatus],
        isOnline: Bool
    ) -> MenuBarState {
        // Error is checked before offline: an auth error while offline is still
        // actionable by the user and must not be silently hidden.
        if let errorStatus = extensionStatuses.first(where: { $0.state == .error }) {
            return .error(message: errorStatus.error ?? "Sync error")
        }

        // Blocked uploads: files queued by the user but stuck after repeated failures.
        // Shown as an error because the user must take action (press Retry).
        let totalBlocked = extensionStatuses.reduce(0) { $0 + $1.blockedUploadCount }
        if totalBlocked > 0 {
            let noun = totalBlocked == 1 ? "upload" : "uploads"
            return .error(message: "\(totalBlocked) \(noun) stuck — press Retry")
        }

        if !isOnline { return .offline }

        if pendingCount > 0 { return .pending(count: pendingCount) }

        if activeCount > 0 { return .syncing(count: activeCount) }

        if extensionStatuses.contains(where: { $0.state == .syncing }) {
            return .syncing(count: 1)
        }

        if extensionStatuses.allSatisfy({ $0.state == .offline }) && !extensionStatuses.isEmpty {
            return .offline
        }

        return .idle
    }
}
