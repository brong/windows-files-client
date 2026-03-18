import Foundation

/// Status reported by a FileProvider extension instance.
/// Written to the shared App Group container by the extension,
/// read by the app UI. This is the single source of truth for account status.
public struct ExtensionStatus: Codable, Sendable {
    public let accountId: String
    public var state: State
    public var lastSyncTime: Date?
    public var nodeCount: Int
    public var error: String?
    public var updatedAt: Date

    public enum State: String, Codable, Sendable {
        case initializing   // extension just started, loading cache
        case syncing        // fetching changes from server
        case idle           // up to date
        case error          // auth failed or other error
        case offline        // can't reach server
    }

    public init(accountId: String, state: State = .initializing,
                nodeCount: Int = 0, error: String? = nil) {
        self.accountId = accountId
        self.state = state
        self.lastSyncTime = nil
        self.nodeCount = nodeCount
        self.error = error
        self.updatedAt = Date()
    }
}

/// Writes extension status to shared container. Used by the extension.
public final class ExtensionStatusWriter: @unchecked Sendable {
    private let fileURL: URL
    private let lock = NSLock()
    private var current: ExtensionStatus

    public init(containerURL: URL, accountId: String) {
        self.fileURL = containerURL.appendingPathComponent("status-\(accountId).json")
        self.current = ExtensionStatus(accountId: accountId)
    }

    public func update(_ modify: (inout ExtensionStatus) -> Void) {
        lock.lock()
        modify(&current)
        current.updatedAt = Date()
        lock.unlock()
        persist()
        postNotification()
    }

    public func setState(_ state: ExtensionStatus.State) {
        update { $0.state = state }
    }

    public func setError(_ error: String) {
        update { $0.state = .error; $0.error = error }
    }

    public func setSyncing() {
        update { $0.state = .syncing; $0.error = nil }
    }

    public func setIdle(nodeCount: Int? = nil) {
        update {
            $0.state = .idle
            $0.error = nil
            $0.lastSyncTime = Date()
            if let count = nodeCount { $0.nodeCount = count }
        }
    }

    private func persist() {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        lock.lock()
        let snapshot = current
        lock.unlock()
        guard let data = try? encoder.encode(snapshot) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }

    private func postNotification() {
        let center = CFNotificationCenterGetDarwinNotifyCenter()
        CFNotificationCenterPostNotification(
            center, CFNotificationName(ExtensionStatusReader.notificationName),
            nil, nil, true)
    }
}

/// Reads extension status from shared container. Used by the app UI.
public final class ExtensionStatusReader: @unchecked Sendable {
    nonisolated(unsafe) public static let notificationName =
        "com.fastmail.files.statusChanged" as CFString

    private let containerURL: URL
    private let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    public init(containerURL: URL) {
        self.containerURL = containerURL
    }

    /// Read status for a specific account.
    public func status(for accountId: String) -> ExtensionStatus? {
        let fileURL = containerURL.appendingPathComponent("status-\(accountId).json")
        guard let data = try? Data(contentsOf: fileURL) else { return nil }
        return try? decoder.decode(ExtensionStatus.self, from: data)
    }

    /// Read status for all accounts that have status files.
    public func allStatuses() -> [ExtensionStatus] {
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: containerURL, includingPropertiesForKeys: nil) else { return [] }
        return files.compactMap { url -> ExtensionStatus? in
            guard url.lastPathComponent.hasPrefix("status-"),
                  url.pathExtension == "json" else { return nil }
            guard let data = try? Data(contentsOf: url) else { return nil }
            return try? decoder.decode(ExtensionStatus.self, from: data)
        }
    }
}
