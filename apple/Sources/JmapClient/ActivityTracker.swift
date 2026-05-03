import Foundation
import Darwin

/// Tracks active file operations across accounts.
/// Shared between the FileProvider extension and the app via the App Group container.
public actor ActivityTracker {
    /// A single activity entry.
    public struct Activity: Codable, Sendable, Identifiable {
        public let id: String              // unique key
        public let accountId: String
        public let fileName: String
        public let action: Action
        public let fileSize: Int?
        public var startedAt: Date
        public var completedAt: Date?      // when it finished
        public var progress: Double?       // 0.0-1.0, nil = indeterminate
        public var status: Status
        public var error: String?

        public enum Action: String, Codable, Sendable {
            case download
            case upload
            case sync
            case delete
        }

        public enum Status: String, Codable, Sendable {
            case active
            case pending
            case completed
            case error
        }
    }

    /// Snapshot of all activities for display.
    public struct Snapshot: Codable, Sendable {
        public let activities: [Activity]
        public let updatedAt: Date
    }

    private var activities: [String: Activity] = [:]
    private let sharedFileURL: URL?
    private var lastPersistTime: Date = .distantPast
    private static let persistThrottle: TimeInterval = 0.25 // max 4 writes/sec
    private static let completedRetention: TimeInterval = 30 // keep completed items 30s
    private var statusWriter: ExtensionStatusWriter?

    public init(containerURL: URL? = nil) {
        if let url = containerURL {
            self.sharedFileURL = url.appendingPathComponent("activity.json")
        } else {
            self.sharedFileURL = nil
        }
    }

    public func setStatusWriter(_ writer: ExtensionStatusWriter) {
        statusWriter = writer
    }

    /// Start tracking an operation.
    /// Pass status: .pending when the operation is queued but not yet executing (e.g.
    /// before upload bytes start flowing). Call markActive(id:) when execution begins.
    public func start(id: String, accountId: String, fileName: String,
                      action: Activity.Action, fileSize: Int? = nil,
                      status: Activity.Status = .active) {
        activities[id] = Activity(
            id: id, accountId: accountId, fileName: fileName,
            action: action, fileSize: fileSize, startedAt: Date(),
            completedAt: nil, progress: nil, status: status, error: nil)
        persist()
        pushToStatus()
    }

    /// Transition a pending activity to active (bytes are now flowing).
    public func markActive(id: String) {
        guard activities[id] != nil else { return }
        activities[id]?.status = .active
        activities[id]?.progress = nil
        persist()
        pushToStatus()
    }

    /// On extension startup, convert any stale .active entries to .pending.
    /// The extension may have been killed mid-operation; the system will retry,
    /// so these are genuinely "queued" not "in progress".
    public func recoverStaledActives() {
        var changed = false
        for key in activities.keys where activities[key]?.status == .active {
            activities[key]?.status = .pending
            activities[key]?.progress = nil
            changed = true
        }
        if changed {
            persist()
            pushToStatus()
        }
    }

    /// Update progress for an operation (0.0-1.0).
    public func updateProgress(id: String, progress: Double) {
        activities[id]?.progress = progress
        persistThrottled()
    }

    /// Mark an operation as completed. Stays visible for 30 seconds.
    public func complete(id: String) {
        activities[id]?.status = .completed
        activities[id]?.progress = 1.0
        activities[id]?.completedAt = Date()
        persist()
        pushToStatus()
    }

    /// Mark an operation as failed.
    public func fail(id: String, error: String) {
        activities[id]?.status = .error
        activities[id]?.error = error
        persist()
        pushToStatus()
    }

    /// Remove a specific activity.
    public func remove(id: String) {
        activities.removeValue(forKey: id)
        persist()
        pushToStatus()
    }

    /// Drop any persisted error activities and publish the clean state.
    /// Called on extension startup to clear stale errors from previous runs.
    public func clearPersistedErrors() {
        activities = activities.filter { $0.value.status != .error }
        persist()
        pushToStatus()
    }

    /// Get current snapshot, pruning old completed items.
    public func snapshot() -> Snapshot {
        // Prune completed items older than retention period
        let cutoff = Date().addingTimeInterval(-Self.completedRetention)
        for (id, activity) in activities {
            if activity.status == .completed,
               let completedAt = activity.completedAt,
               completedAt < cutoff {
                activities.removeValue(forKey: id)
            }
        }

        let sorted = Array(activities.values).sorted { a, b in
            // Active first, then completed, then errors
            if a.status == .active && b.status != .active { return true }
            if a.status != .active && b.status == .active { return false }
            return a.startedAt > b.startedAt
        }
        return Snapshot(activities: sorted, updatedAt: Date())
    }

    /// Get count of active operations.
    public var activeCount: Int {
        activities.values.filter { $0.status == .active }.count
    }

    /// Load snapshot from shared file (for app to read extension state).
    public static func loadShared(containerURL: URL) -> Snapshot? {
        let fileURL = containerURL.appendingPathComponent("activity.json")
        guard let data = try? Data(contentsOf: fileURL) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(Snapshot.self, from: data)
    }

    /// Force an immediate write to the shared file, bypassing the throttle.
    /// Use before reading the file from another process or in tests.
    public func flush() {
        persist()
    }

    // MARK: - Persistence & Notification

    /// Darwin notification name for cross-process activity updates.
    nonisolated(unsafe) public static let darwinNotificationName = "com.fastmail.files.activityChanged" as CFString

    /// Push current active/pending counts and hints into the ExtensionStatus file.
    private func pushToStatus() {
        guard let writer = statusWriter else { return }
        let activeItems = activities.values.filter { $0.status == .active }
        let pendingItems = activities.values.filter { $0.status == .pending }
        let hints = (activeItems + pendingItems).prefix(5).map { a -> ExtensionStatus.OperationHint in
            let verb: String
            switch a.action {
            case .upload:   verb = "Uploading"
            case .download: verb = "Downloading"
            case .sync:     verb = "Syncing"
            case .delete:   verb = "Deleting"
            }
            return ExtensionStatus.OperationHint(id: a.id, fileName: a.fileName, actionVerb: verb)
        }
        writer.setActivityCounts(
            active: activeItems.count,
            pending: pendingItems.count,
            hints: Array(hints)
        )
    }

    private func persist() {
        lastPersistTime = Date()
        guard let fileURL = sharedFileURL else { return }
        let snap = snapshot()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(snap) else { return }
        try? data.write(to: fileURL, options: .atomic)
        postDarwinNotification()
    }

    /// Persist at most every 250ms — avoids hammering disk during rapid small-file downloads.
    private func persistThrottled() {
        let now = Date()
        guard now.timeIntervalSince(lastPersistTime) >= Self.persistThrottle else { return }
        persist()
    }

    private func postDarwinNotification() {
        let center = CFNotificationCenterGetDarwinNotifyCenter()
        CFNotificationCenterPostNotification(center, CFNotificationName(Self.darwinNotificationName), nil, nil, true)
    }
}

// MARK: - Activity Observer (for app UI)

/// Observes Darwin notifications from the extension's ActivityTracker.
/// Call `start()` to begin listening; the `onChange` callback fires on each update.
public final class ActivityObserver: @unchecked Sendable {
    private let onChange: @Sendable () -> Void
    private let lock = NSLock()
    private var isObserving = false

    public init(onChange: @escaping @Sendable () -> Void) {
        self.onChange = onChange
    }

    deinit {
        stop()
    }

    public func start() {
        lock.lock()
        guard !isObserving else { lock.unlock(); return }
        isObserving = true
        lock.unlock()

        let center = CFNotificationCenterGetDarwinNotifyCenter()
        let observer = Unmanaged.passUnretained(self).toOpaque()

        CFNotificationCenterAddObserver(
            center,
            observer,
            { _, observer, _, _, _ in
                guard let observer = observer else { return }
                let myself = Unmanaged<ActivityObserver>.fromOpaque(observer).takeUnretainedValue()
                myself.onChange()
            },
            ActivityTracker.darwinNotificationName,
            nil,
            .deliverImmediately
        )
    }

    public func stop() {
        lock.lock()
        guard isObserving else { lock.unlock(); return }
        isObserving = false
        lock.unlock()

        let center = CFNotificationCenterGetDarwinNotifyCenter()
        let observer = Unmanaged.passUnretained(self).toOpaque()
        CFNotificationCenterRemoveObserver(center, observer, CFNotificationName(ActivityTracker.darwinNotificationName), nil)
    }
}
