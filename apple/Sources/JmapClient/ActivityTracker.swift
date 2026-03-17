import Foundation

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

    public init(containerURL: URL? = nil) {
        if let url = containerURL {
            self.sharedFileURL = url.appendingPathComponent("activity.json")
        } else {
            self.sharedFileURL = nil
        }
    }

    /// Start tracking an operation.
    public func start(id: String, accountId: String, fileName: String,
                      action: Activity.Action, fileSize: Int? = nil) {
        activities[id] = Activity(
            id: id, accountId: accountId, fileName: fileName,
            action: action, fileSize: fileSize, startedAt: Date(),
            progress: nil, status: .active, error: nil)
        persist()
    }

    /// Update progress for an operation (0.0-1.0).
    public func updateProgress(id: String, progress: Double) {
        activities[id]?.progress = progress
        persist()
    }

    /// Mark an operation as completed (removes it after a short delay).
    public func complete(id: String) {
        activities[id]?.status = .completed
        activities[id]?.progress = 1.0
        persist()
        // Remove after 2 seconds so it briefly shows as completed
        Task {
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            activities.removeValue(forKey: id)
            persist()
        }
    }

    /// Mark an operation as failed.
    public func fail(id: String, error: String) {
        activities[id]?.status = .error
        activities[id]?.error = error
        persist()
    }

    /// Remove a specific activity.
    public func remove(id: String) {
        activities.removeValue(forKey: id)
        persist()
    }

    /// Get current snapshot of all activities.
    public func snapshot() -> Snapshot {
        Snapshot(activities: Array(activities.values)
            .sorted { $0.startedAt > $1.startedAt },
                 updatedAt: Date())
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

    // MARK: - Persistence

    private func persist() {
        guard let fileURL = sharedFileURL else { return }
        let snapshot = snapshot()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(snapshot) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }
}
