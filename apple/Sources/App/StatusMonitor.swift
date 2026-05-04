import Foundation
import JmapClient

/// Reads extension status files and listens for Darwin notifications.
@MainActor
final class StatusMonitor: ObservableObject {
    @Published var statuses: [String: ExtensionStatus] = [:]
    @Published var activitySnapshot: ActivityTracker.Snapshot? = nil

    private let containerURL: URL
    private let knownAccountIds: () -> Set<String>

    init(containerURL: URL, knownAccountIds: @escaping () -> Set<String>) {
        self.containerURL = containerURL
        self.knownAccountIds = knownAccountIds
    }

    // MARK: - Observers

    func startObserving() {
        let center = CFNotificationCenterGetDarwinNotifyCenter()
        let observer = Unmanaged.passUnretained(self).toOpaque()
        let callback: CFNotificationCallback = { _, observer, _, _, _ in
            guard let observer = observer else { return }
            let monitor = Unmanaged<StatusMonitor>.fromOpaque(observer).takeUnretainedValue()
            DispatchQueue.main.async { monitor.reload() }
        }
        CFNotificationCenterAddObserver(center, observer, callback,
            ExtensionStatusReader.notificationName, nil, .deliverImmediately)
        CFNotificationCenterAddObserver(center, observer, callback,
            ActivityTracker.darwinNotificationName, nil, .deliverImmediately)
        reload()
    }

    // MARK: - Reload

    func reload() {
        let reader = ExtensionStatusReader(containerURL: containerURL)
        let known = knownAccountIds()
        var newStatuses: [String: ExtensionStatus] = [:]
        let now = Date()
        for var status in reader.allStatuses() {
            if !known.contains(status.accountId) {
                let orphan = containerURL.appendingPathComponent("status-\(status.accountId).json")
                try? FileManager.default.removeItem(at: orphan)
                continue
            }
            // Normalize stale syncing/initializing with no active ops to idle.
            if now.timeIntervalSince(status.updatedAt) > 300,
               (status.state == .syncing || status.state == .initializing),
               status.activeOperationCount == 0 {
                status.state = .idle
            }
            newStatuses[status.accountId] = status
        }
        statuses = newStatuses
        activitySnapshot = ActivityTracker.loadShared(containerURL: containerURL)
    }
}
