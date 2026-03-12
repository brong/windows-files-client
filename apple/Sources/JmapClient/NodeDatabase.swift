import Foundation

/// Persistent node cache stored as JSON in the App Group shared container.
/// Equivalent to Windows nodecache.json — stores node metadata and JMAP state token
/// so the extension can resume incrementally after being terminated.
///
/// Thread-safe via actor isolation.
public actor NodeDatabase {
    private let fileURL: URL
    private var cache: NodeCache
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    /// Current cache version. Increment to force a full re-fetch on next launch.
    private static let currentVersion = 1

    public init(containerURL: URL, accountId: String) {
        let dir = containerURL
            .appendingPathComponent("NodeCache", isDirectory: true)
            .appendingPathComponent(accountId, isDirectory: true)
        self.fileURL = dir.appendingPathComponent("nodecache.json")

        self.encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        encoder.dateEncodingStrategy = .iso8601

        self.decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        // Try loading existing cache
        if let data = try? Data(contentsOf: fileURL),
           let loaded = try? decoder.decode(NodeCache.self, from: data),
           loaded.version == Self.currentVersion
        {
            self.cache = loaded
        } else {
            self.cache = NodeCache(version: Self.currentVersion)
        }
    }

    // MARK: - State Token

    public var stateToken: String? {
        cache.stateToken
    }

    public func setStateToken(_ token: String) {
        cache.stateToken = token
    }

    // MARK: - Special Node IDs

    public var homeNodeId: String? {
        cache.homeNodeId
    }

    public var trashNodeId: String? {
        cache.trashNodeId
    }

    public func setHomeNodeId(_ id: String) {
        cache.homeNodeId = id
    }

    public func setTrashNodeId(_ id: String?) {
        cache.trashNodeId = id
    }

    // MARK: - Node Entries

    /// Get a cached node entry by ID.
    public func entry(for nodeId: String) -> NodeCacheEntry? {
        cache.entries[nodeId]
    }

    /// Get all cached node entries.
    public var allEntries: [String: NodeCacheEntry] {
        cache.entries
    }

    /// Number of cached entries.
    public var count: Int {
        cache.entries.count
    }

    /// Store or update a node entry.
    public func upsert(nodeId: String, entry: NodeCacheEntry) {
        cache.entries[nodeId] = entry
    }

    /// Store a FileNode from the server.
    public func upsertFromServer(_ node: FileNode) {
        cache.entries[node.id] = NodeCacheEntry(
            parentId: node.parentId,
            name: node.name,
            blobId: node.blobId,
            size: node.size ?? 0,
            modified: node.modified,
            isFolder: node.isFolder,
            type: node.type,
            myRights: node.myRights
        )
    }

    /// Remove a node entry.
    public func remove(nodeId: String) {
        cache.entries.removeValue(forKey: nodeId)
    }

    /// Remove multiple node entries.
    public func remove(nodeIds: [String]) {
        for id in nodeIds {
            cache.entries.removeValue(forKey: id)
        }
    }

    /// Clear all entries (for full re-population).
    public func clearEntries() {
        cache.entries.removeAll()
        cache.stateToken = nil
    }

    /// Get all children of a node.
    public func children(of parentId: String) -> [(id: String, entry: NodeCacheEntry)] {
        cache.entries.compactMap { (id, entry) in
            entry.parentId == parentId ? (id, entry) : nil
        }
    }

    // MARK: - Persistence

    /// Save to disk. Uses atomic write (temp file + rename).
    public func save() throws {
        let dir = fileURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        let data = try encoder.encode(cache)
        let tempURL = fileURL.appendingPathExtension("tmp")
        try data.write(to: tempURL, options: .atomic)
        // .atomic already does temp+rename, but let's be explicit
        if FileManager.default.fileExists(atPath: fileURL.path) {
            try FileManager.default.removeItem(at: fileURL)
        }
        try FileManager.default.moveItem(at: tempURL, to: fileURL)
    }

    /// Delete the cache file (for clean start).
    public func delete() throws {
        cache = NodeCache(version: Self.currentVersion)
        if FileManager.default.fileExists(atPath: fileURL.path) {
            try FileManager.default.removeItem(at: fileURL)
        }
    }

    /// Build the complete path for a node by walking up the parent chain.
    /// Returns nil if the chain doesn't reach the home node.
    public func relativePath(for nodeId: String) -> String? {
        guard let homeId = cache.homeNodeId else { return nil }
        if nodeId == homeId { return "" }

        var components: [String] = []
        var currentId = nodeId

        // Walk up the tree with cycle detection
        var visited = Set<String>()
        while currentId != homeId {
            guard !visited.contains(currentId) else { return nil }
            visited.insert(currentId)

            guard let entry = cache.entries[currentId] else { return nil }
            components.append(entry.name)

            guard let parentId = entry.parentId else { return nil }
            currentId = parentId
        }

        return components.reversed().joined(separator: "/")
    }
}

// MARK: - Cache Data Model

struct NodeCache: Codable {
    var version: Int
    var homeNodeId: String?
    var trashNodeId: String?
    var stateToken: String?
    var entries: [String: NodeCacheEntry]

    init(version: Int) {
        self.version = version
        self.entries = [:]
    }
}

public struct NodeCacheEntry: Codable, Sendable {
    public let parentId: String?
    public let name: String
    public let blobId: String?
    public let size: Int
    public let modified: Date?
    public let isFolder: Bool
    public let type: String?
    public let myRights: FileNodeRights?

    public init(
        parentId: String?,
        name: String,
        blobId: String? = nil,
        size: Int = 0,
        modified: Date? = nil,
        isFolder: Bool = false,
        type: String? = nil,
        myRights: FileNodeRights? = nil
    ) {
        self.parentId = parentId
        self.name = name
        self.blobId = blobId
        self.size = size
        self.modified = modified
        self.isFolder = isFolder
        self.type = type
        self.myRights = myRights
    }
}
