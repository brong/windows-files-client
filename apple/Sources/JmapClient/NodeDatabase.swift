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

    /// After this many consecutive failures the extension stops auto-retrying and
    /// requires the user to explicitly press Retry in the menu bar.
    public static let uploadFailureThreshold = 5

    private static func registerMigrations(in migrator: inout DatabaseMigrator) {
        migrator.registerMigration("v5_sha1_cache") { db in
            // Content-addressed chunk cache: maps SHA1 → blobId for already-uploaded data.
            // Enables two optimisations:
            //   1. Skip re-uploading a chunk whose bytes were already sent in a prior upload.
            //   2. Reuse chunks from any file when the same bytes appear again (dedup).
            // last_seen is updated on every cache hit so actively-used entries survive sweeps.
            //
            // NOTE: sha1 is PRIMARY KEY (one blobId per content hash). This relies on the
            // Fastmail server using content-addressed storage where blobId == SHA1 of the
            // content, so the same bytes always map to the same blobId. On a generic JMAP
            // server this is not guaranteed — the same sha1 could map to multiple distinct
            // blobIds — but because we always overwrite with the most-recently-uploaded id
            // (ON CONFLICT DO UPDATE), the cached entry is always a recently live blob.
            // If a cached blobId turns out to be GC'd, the Blob/set call fails and the
            // caller falls back to re-uploading that chunk.
            try db.create(table: "sha1_cache", ifNotExists: true) { t in
                t.column("sha1", .text).primaryKey()
                t.column("blob_id", .text).notNull()
                t.column("last_seen", .double).notNull().defaults(to: 0)
            }
        }
        migrator.registerMigration("v4_upload_chunks") { db in
            // Per-chunk upload state for resumable chunked uploads.
            // Keyed by (upload_id, chunk_index). blob_id is NULL until the chunk completes.
            // task_id is the URLSessionTask.taskIdentifier — used to match orphaned background
            // session completions (tasks that finished after the extension was killed) back to
            // their chunk so the blobId can be persisted without re-uploading.
            try db.create(table: "upload_chunks", ifNotExists: true) { t in
                t.column("upload_id", .text).notNull()
                t.column("chunk_index", .integer).notNull()
                t.column("task_id", .integer)
                t.column("blob_id", .text)
                t.column("sha1", .text)
                t.column("created_at", .double).notNull().defaults(sql: "(strftime('%s','now'))")
                t.primaryKey(["upload_id", "chunk_index"])
            }
            try db.create(
                index: "idx_upload_chunks_task_id", on: "upload_chunks",
                columns: ["task_id"], ifNotExists: true
            )
        }
        migrator.registerMigration("v3_upload_failures") { db in
            // Per-item failure tracking. Keyed by the FileProvider item identifier
            // (system-assigned for new files, server nodeId for modifications).
            // Kept separate from nodes so it survives node-table resets and also
            // covers in-flight creates that don't have a server nodeId yet.
            try db.create(table: "upload_failures", ifNotExists: true) { t in
                t.column("itemIdentifier", .text).primaryKey()
                t.column("failureCount", .integer).notNull().defaults(to: 0)
                t.column("lastError", .text).notNull().defaults(to: "")
            }
        }
        migrator.registerMigration("v2_pinned_nodes") { db in
            // Local-only pin state; kept in a separate table so it survives node-table resets.
            try db.create(table: "pinned_nodes", ifNotExists: true) { t in
                t.column("nodeId", .text).primaryKey()
            }
        }
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

    // MARK: - Enumeration Failure Counter (BUG-007)

    /// Number of consecutive syncAnchorExpired returns from the empty-DB guard.
    /// Persisted across extension process restarts so the loop cap survives kills.
    public var enumerationFailureCount: Int {
        Int(syncStateValue(for: "enumerationFailureCount") ?? "0") ?? 0
    }

    public func setEnumerationFailureCount(_ count: Int) {
        setSyncStateValue(String(count), for: "enumerationFailureCount")
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

    // MARK: - Pin State (local-only, survives node-table resets)

    public func isPinned(id: String) -> Bool {
        (try? pool.read { db in
            try Int.fetchOne(
                db, sql: "SELECT COUNT(*) FROM pinned_nodes WHERE nodeId = ?",
                arguments: [id]) ?? 0
        }) == 1
    }

    /// Remove pinned_nodes entries whose nodeId no longer exists in nodes.
    /// Called after a full BFS completes so stale pins from a previous account
    /// incarnation (different node IDs) don't accumulate indefinitely.
    public func cleanupOrphanedPins() {
        try? pool.write { db in
            try db.execute(sql: """
                DELETE FROM pinned_nodes
                WHERE nodeId NOT IN (SELECT id FROM nodes)
            """)
        }
    }

    public func setPinned(id: String, pinned: Bool) {
        try? pool.write { db in
            if pinned {
                try db.execute(
                    sql: "INSERT OR IGNORE INTO pinned_nodes (nodeId) VALUES (?)",
                    arguments: [id])
            } else {
                try db.execute(
                    sql: "DELETE FROM pinned_nodes WHERE nodeId = ?",
                    arguments: [id])
            }
        }
    }

    /// Returns the set of all pinned node IDs. Used during enumeration to
    /// batch-resolve contentPolicy without one query per item.
    public var allPinnedIds: Set<String> {
        (try? pool.read { db in
            let rows = try Row.fetchAll(db, sql: "SELECT nodeId FROM pinned_nodes")
            return Set(rows.map { $0["nodeId"] as String })
        }) ?? []
    }

    // MARK: - Upload Failure Tracking

    /// Record a failure for the given item. Returns the new consecutive failure count.
    @discardableResult
    public func incrementUploadFailure(itemIdentifier: String, error: String) -> Int {
        (try? pool.write { db in
            try db.execute(sql: """
                INSERT INTO upload_failures (itemIdentifier, failureCount, lastError)
                VALUES (?, 1, ?)
                ON CONFLICT(itemIdentifier) DO UPDATE SET
                    failureCount = failureCount + 1,
                    lastError    = excluded.lastError
            """, arguments: [itemIdentifier, error])
            return try Int.fetchOne(
                db, sql: "SELECT failureCount FROM upload_failures WHERE itemIdentifier = ?",
                arguments: [itemIdentifier]) ?? 1
        }) ?? 1
    }

    /// Clear the failure record for an item (called on successful upload).
    public func resetUploadFailure(itemIdentifier: String) {
        try? pool.write { db in
            try db.execute(
                sql: "DELETE FROM upload_failures WHERE itemIdentifier = ?",
                arguments: [itemIdentifier])
        }
    }

    /// Clear all failure records (called when the user presses Retry in the menu bar).
    public func resetAllUploadFailures() {
        try? pool.write { db in
            try db.execute(sql: "DELETE FROM upload_failures")
        }
    }

    /// Number of items that have hit the failure threshold and need user action.
    public var blockedUploadCount: Int {
        (try? pool.read { db in
            try Int.fetchOne(
                db,
                sql: "SELECT COUNT(*) FROM upload_failures WHERE failureCount >= ?",
                arguments: [Self.uploadFailureThreshold]) ?? 0
        }) ?? 0
    }

    // MARK: - Resumable Upload Chunk State

    /// All completed chunks for the given upload session (blob_id IS NOT NULL).
    public func completedUploadChunks(uploadId: String) -> [(index: Int, blobId: String, sha1: String)] {
        (try? pool.read { db in
            let rows = try Row.fetchAll(
                db,
                sql: "SELECT chunk_index, blob_id, sha1 FROM upload_chunks WHERE upload_id = ? AND blob_id IS NOT NULL ORDER BY chunk_index",
                arguments: [uploadId]
            )
            return rows.compactMap { row -> (Int, String, String)? in
                guard let blobId = row["blob_id"] as String?,
                      let sha1 = row["sha1"] as String? else { return nil }
                return (row["chunk_index"] as Int, blobId, sha1)
            }
        }) ?? []
    }

    /// Record that a background upload task has been started for a chunk.
    /// Called synchronously in the taskIdSink callback before the task starts.
    public func startUploadChunk(uploadId: String, index: Int, taskId: Int) {
        try? pool.write { db in
            try db.execute(
                sql: "INSERT OR REPLACE INTO upload_chunks (upload_id, chunk_index, task_id) VALUES (?, ?, ?)",
                arguments: [uploadId, index, taskId]
            )
        }
    }

    /// Record that a chunk upload completed successfully.
    public func completeUploadChunk(uploadId: String, index: Int, blobId: String, sha1: String) {
        try? pool.write { db in
            try db.execute(
                sql: """
                    INSERT INTO upload_chunks (upload_id, chunk_index, blob_id, sha1)
                    VALUES (?, ?, ?, ?)
                    ON CONFLICT(upload_id, chunk_index) DO UPDATE SET blob_id = excluded.blob_id, sha1 = excluded.sha1
                """,
                arguments: [uploadId, index, blobId, sha1]
            )
        }
    }

    /// Mark a chunk complete using only its task ID (for orphaned background session completions).
    /// Returns true if the task ID was found and the row updated.
    @discardableResult
    public func completeUploadChunkByTaskId(_ taskId: Int, blobId: String, sha1: String) -> Bool {
        (try? pool.write { db in
            try db.execute(
                sql: "UPDATE upload_chunks SET blob_id = ?, sha1 = ? WHERE task_id = ?",
                arguments: [blobId, sha1, taskId]
            )
            return db.changesCount > 0
        }) ?? false
    }

    /// Remove all chunk rows for an upload session after it completes successfully.
    public func clearUploadSession(uploadId: String) {
        try? pool.write { db in
            try db.execute(
                sql: "DELETE FROM upload_chunks WHERE upload_id = ?",
                arguments: [uploadId]
            )
        }
    }

    /// Remove upload sessions older than `maxAge` seconds (default 7 days).
    /// Called at extension startup to prevent the table from growing indefinitely.
    public func sweepOldUploadSessions(maxAge: TimeInterval = 7 * 86400) {
        let cutoff = Date().timeIntervalSince1970 - maxAge
        try? pool.write { db in
            try db.execute(
                sql: "DELETE FROM upload_chunks WHERE created_at < ?",
                arguments: [cutoff]
            )
        }
    }

    // MARK: - SHA1→blobId Cache

    /// Look up a cached blobId for the given chunk SHA1, updating last_seen if found.
    public func cachedBlobId(forSha1 sha1: String) -> String? {
        try? pool.write { db in
            guard let blobId = try String.fetchOne(
                db, sql: "SELECT blob_id FROM sha1_cache WHERE sha1 = ?", arguments: [sha1]
            ) else { return nil }
            let now = Date().timeIntervalSince1970
            try db.execute(
                sql: "UPDATE sha1_cache SET last_seen = ? WHERE sha1 = ?",
                arguments: [now, sha1]
            )
            return blobId
        }
    }

    /// Store or update a SHA1→blobId mapping.
    public func cacheChunkSha1(_ sha1: String, blobId: String) {
        let now = Date().timeIntervalSince1970
        try? pool.write { db in
            try db.execute(
                sql: """
                    INSERT INTO sha1_cache (sha1, blob_id, last_seen) VALUES (?, ?, ?)
                    ON CONFLICT(sha1) DO UPDATE SET blob_id = excluded.blob_id, last_seen = excluded.last_seen
                """,
                arguments: [sha1, blobId, now]
            )
        }
    }

    /// Remove entries not accessed in `maxAge` seconds (default 30 days).
    public func sweepSha1Cache(maxAge: TimeInterval = 30 * 86400) {
        let cutoff = Date().timeIntervalSince1970 - maxAge
        try? pool.write { db in
            try db.execute(
                sql: "DELETE FROM sha1_cache WHERE last_seen < ?",
                arguments: [cutoff]
            )
        }
    }

    /// MARK: - Node Lookup

    /// Look up the node ID of an item by exact name and parent folder.
    /// Used to detect prior successful uploads whose completionHandler was never called.
    public func nodeId(forName name: String, parentId: String) -> String? {
        try? pool.read { db in
            try String.fetchOne(
                db,
                sql: "SELECT id FROM nodes WHERE parentId = ? AND name = ?",
                arguments: [parentId, name]
            )
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
