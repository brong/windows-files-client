import Foundation
import GRDB

/// Persistent node cache backed by SQLite (GRDB DatabasePool).
///
/// DatabasePool enables WAL mode by default, which allows safe concurrent
/// access from both the containing app and the FileProvider extension.
/// This replaces the previous JSON approach that was vulnerable to corruption
/// when the extension was killed mid-write.
///
/// Thread-safe via actor isolation + GRDB's WAL mode.
public actor NodeDatabase {
    private let pool: DatabasePool

    public init(containerURL: URL, accountId: String) {
        self.pool = Self.openPool(containerURL: containerURL, accountId: accountId)
    }

    // MARK: - Schema

    private static func registerMigrations(in migrator: inout DatabaseMigrator) {
        migrator.registerMigration("v1_initial") { db in
            try db.create(table: "nodes", ifNotExists: true) { t in
                t.column("id", .text).primaryKey()
                t.column("parentId", .text)
                t.column("name", .text).notNull()
                t.column("blobId", .text)
                t.column("size", .integer).notNull().defaults(to: 0)
                t.column("modified", .double)  // seconds since epoch; nil = unknown
                t.column("isFolder", .boolean).notNull().defaults(to: false)
                t.column("type", .text)
                t.column("mayRead", .boolean).notNull().defaults(to: true)
                t.column("mayWrite", .boolean).notNull().defaults(to: true)
            }
            try db.create(
                index: "idx_nodes_parentId", on: "nodes",
                columns: ["parentId"], ifNotExists: true
            )
            try db.create(table: "sync_state", ifNotExists: true) { t in
                t.column("key", .text).primaryKey()
                t.column("value", .text)
            }
        }
    }

    private static func openPool(containerURL: URL, accountId: String) -> DatabasePool {
        do {
            return try makePool(at: containerURL, accountId: accountId)
        } catch {
            // Fallback: corrupted or inaccessible DB — start fresh in a temp location.
            // This should only happen in unusual circumstances (disk full, permissions).
            let tmp = FileManager.default.temporaryDirectory
                .appendingPathComponent("FastmailFilesFallback-\(accountId)-\(UUID().uuidString)")
            return (try? makePool(at: tmp, accountId: accountId)) ?? {
                fatalError("NodeDatabase: cannot open even a fallback SQLite — \(error)")
            }()
        }
    }

    private static func makePool(at containerURL: URL, accountId: String) throws -> DatabasePool {
        let dir = containerURL
            .appendingPathComponent("NodeCache", isDirectory: true)
            .appendingPathComponent(accountId, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let dbURL = dir.appendingPathComponent("nodecache.sqlite")
        // Allow up to 5 seconds of retries when the app and extension contend for
        // the WAL write lock. Without this, concurrent writes silently fail.
        var config = Configuration()
        config.busyMode = .timeout(5)
        let pool = try DatabasePool(path: dbURL.path, configuration: config)
        var migrator = DatabaseMigrator()
        registerMigrations(in: &migrator)
        try migrator.migrate(pool)
        return pool
    }

    // MARK: - State Token

    public var stateToken: String? {
        syncStateValue(for: "stateToken")
    }

    public func setStateToken(_ token: String) {
        setSyncStateValue(token, for: "stateToken")
    }

    // MARK: - Special Node IDs

    public var homeNodeId: String? {
        syncStateValue(for: "homeNodeId")
    }

    public var trashNodeId: String? {
        syncStateValue(for: "trashNodeId")
    }

    public func setHomeNodeId(_ id: String) {
        setSyncStateValue(id, for: "homeNodeId")
    }

    public func setTrashNodeId(_ id: String?) {
        if let id {
            setSyncStateValue(id, for: "trashNodeId")
        } else {
            try? pool.write { db in
                try db.execute(sql: "DELETE FROM sync_state WHERE key = 'trashNodeId'")
            }
        }
    }

    // MARK: - Node Entries

    public func entry(for nodeId: String) -> NodeCacheEntry? {
        try? pool.read { db in
            guard let row = try Row.fetchOne(
                db, sql: "SELECT * FROM nodes WHERE id = ?", arguments: [nodeId]
            ) else { return nil }
            return Self.entry(from: row)
        }
    }

    public var allEntries: [String: NodeCacheEntry] {
        (try? pool.read { db in
            let rows = try Row.fetchAll(db, sql: "SELECT * FROM nodes")
            return Dictionary(uniqueKeysWithValues: rows.map { row in
                (row["id"] as String, Self.entry(from: row))
            })
        }) ?? [:]
    }

    public var count: Int {
        (try? pool.read { db in
            try Int.fetchOne(db, sql: "SELECT COUNT(*) FROM nodes") ?? 0
        }) ?? 0
    }

    public func upsert(nodeId: String, entry: NodeCacheEntry) {
        try? pool.write { db in
            try Self.write(nodeId: nodeId, entry: entry, to: db)
        }
    }

    public func upsertFromServer(_ node: FileNode) {
        guard let name = node.name else { return }
        let entry = NodeCacheEntry(
            parentId: node.parentId,
            name: name,
            blobId: node.blobId,
            size: node.size ?? 0,
            modified: node.modified,
            isFolder: node.isFolder,
            type: node.type,
            myRights: node.myRights
        )
        upsert(nodeId: node.id, entry: entry)
    }

    public func remove(nodeId: String) {
        try? pool.write { db in
            try db.execute(sql: "DELETE FROM nodes WHERE id = ?", arguments: [nodeId])
        }
    }

    public func remove(nodeIds: [String]) {
        guard !nodeIds.isEmpty else { return }
        let placeholders = nodeIds.map { _ in "?" }.joined(separator: ", ")
        try? pool.write { db in
            try db.execute(
                sql: "DELETE FROM nodes WHERE id IN (\(placeholders))",
                arguments: StatementArguments(nodeIds)
            )
        }
    }

    public func clearEntries() {
        try? pool.write { db in
            try db.execute(sql: "DELETE FROM nodes")
            try db.execute(sql: "DELETE FROM sync_state WHERE key = 'stateToken'")
        }
    }

    public func children(of parentId: String) -> [(id: String, entry: NodeCacheEntry)] {
        (try? pool.read { db in
            let rows = try Row.fetchAll(
                db, sql: "SELECT * FROM nodes WHERE parentId = ?", arguments: [parentId]
            )
            return rows.map { row in (row["id"] as String, Self.entry(from: row)) }
        }) ?? []
    }

    // MARK: - Persistence

    /// No-op: SQLite writes are committed immediately on each mutation.
    /// Kept for API compatibility with code that calls save() explicitly.
    public func save() throws {}

    /// Delete all node data. The SQLite file stays open but all rows are removed.
    public func delete() throws {
        try pool.write { db in
            try db.execute(sql: "DELETE FROM nodes")
            try db.execute(sql: "DELETE FROM sync_state")
        }
    }

    /// Build the relative path for a node by walking up the parent chain.
    /// Returns nil if the chain doesn't reach the home node (orphaned or cycle).
    public func relativePath(for nodeId: String) -> String? {
        guard let homeId = homeNodeId else { return nil }
        if nodeId == homeId { return "" }

        var components: [String] = []
        var currentId = nodeId
        var visited = Set<String>()

        while currentId != homeId {
            guard !visited.contains(currentId) else { return nil }
            visited.insert(currentId)

            guard let row = try? pool.read({ db in
                try Row.fetchOne(
                    db, sql: "SELECT parentId, name FROM nodes WHERE id = ?",
                    arguments: [currentId]
                )
            }) else { return nil }

            components.append(row["name"] as String)
            guard let parentId = row["parentId"] as String? else { return nil }
            currentId = parentId
        }

        return components.reversed().joined(separator: "/")
    }

    // MARK: - Private Helpers

    private func syncStateValue(for key: String) -> String? {
        try? pool.read { db in
            try String.fetchOne(
                db, sql: "SELECT value FROM sync_state WHERE key = ?", arguments: [key]
            )
        }
    }

    private func setSyncStateValue(_ value: String, for key: String) {
        try? pool.write { db in
            try db.execute(
                sql: "INSERT OR REPLACE INTO sync_state (key, value) VALUES (?, ?)",
                arguments: [key, value]
            )
        }
    }

    private static func entry(from row: Row) -> NodeCacheEntry {
        let modified: Double? = row["modified"]
        let mayRead = (row["mayRead"] as Bool?) ?? true
        let mayWrite = (row["mayWrite"] as Bool?) ?? true
        return NodeCacheEntry(
            parentId: row["parentId"],
            name: row["name"] as String,
            blobId: row["blobId"],
            size: (row["size"] as Int?) ?? 0,
            modified: modified.map { Date(timeIntervalSince1970: $0) },
            isFolder: (row["isFolder"] as Bool?) ?? false,
            type: row["type"],
            myRights: FileNodeRights(mayRead: mayRead, mayWrite: mayWrite)
        )
    }

    private static func write(nodeId: String, entry: NodeCacheEntry, to db: Database) throws {
        try db.execute(
            sql: """
                INSERT OR REPLACE INTO nodes
                    (id, parentId, name, blobId, size, modified, isFolder, type, mayRead, mayWrite)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            arguments: [
                nodeId,
                entry.parentId,
                entry.name,
                entry.blobId,
                entry.size,
                entry.modified?.timeIntervalSince1970,
                entry.isFolder,
                entry.type,
                entry.myRights?.mayRead ?? true,
                entry.myRights?.mayWrite ?? true,
            ]
        )
    }
}

// MARK: - NodeCacheEntry (public type — unchanged)

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
