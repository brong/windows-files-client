import Foundation
import CFuseT
import JmapClient

// MARK: - FUSE ↔ Swift bridge

/// Global pointer to the active filesystem, used by C callbacks.
/// FUSE callbacks are plain C function pointers (no captures), so we
/// stash our Swift object here and retrieve it via fuse_get_context().
nonisolated(unsafe) var gFilesystem: FileNodeFuseFS?

/// A FUSE filesystem backed by the JMAP FileNode API.
///
/// Maps POSIX paths to JMAP FileNode IDs. Provides on-demand hydration
/// (download on first read) and buffered writes (upload on close).
public final class FileNodeFuseFS: @unchecked Sendable {

    // MARK: - Node tree

    struct NodeEntry {
        let nodeId: String
        let parentId: String?
        var name: String
        var blobId: String?
        var size: Int
        var isFolder: Bool
        var type: String?
        var created: Date?
        var modified: Date?
        var mayWrite: Bool
    }

    /// All nodes keyed by nodeId
    var nodes: [String: NodeEntry] = [:]
    /// Children lookup: parentId → [nodeId]
    var children: [String: [String]] = [:]
    /// Home (root) node ID
    var homeNodeId: String = ""
    /// Trash node ID (optional)
    var trashNodeId: String?
    /// JMAP state token for incremental sync
    var stateToken: String = ""

    // MARK: - File handles & write buffers

    /// Next file handle to allocate
    var nextFH: UInt64 = 1
    /// Open file handles: fh → nodeId
    var openFiles: [UInt64: String] = [:]
    /// Write buffers: fh → mutable data
    var writeBuffers: [UInt64: Data] = [:]
    /// Blob cache: nodeId → file data
    var blobCache: [String: Data] = [:]
    /// In-flight downloads: blobId → semaphore (for deduplication)
    var inflightDownloads: [String: DispatchSemaphore] = [:]
    /// Blob disk cache directory (~/Library/Caches/...)
    let blobCacheDir: URL
    /// State directory for persistent node cache (~/Library/Application Support/...)
    let stateDir: URL

    // MARK: - Concurrency

    /// Lock protecting all mutable state (nodes, handles, caches)
    let lock = NSLock()

    // MARK: - JMAP client (sync access pattern)

    let jmapClient: JmapClient
    let sessionManager: SessionManager
    let accountId: String

    // MARK: - Init

    public init(jmapClient: JmapClient, sessionManager: SessionManager,
                accountId: String, stateDir: URL, blobCacheDir: URL) {
        self.jmapClient = jmapClient
        self.sessionManager = sessionManager
        self.accountId = accountId
        self.stateDir = stateDir
        self.blobCacheDir = blobCacheDir
        try? FileManager.default.createDirectory(at: stateDir, withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(at: blobCacheDir, withIntermediateDirectories: true)
    }

    // MARK: - Persistent node cache

    /// Serializable snapshot of the node tree for disk persistence.
    struct NodeCacheSnapshot: Codable {
        let version: Int
        let accountId: String
        let homeNodeId: String
        let trashNodeId: String?
        let stateToken: String
        let entries: [String: CodableNodeEntry]

        static let currentVersion = 1
    }

    struct CodableNodeEntry: Codable {
        let parentId: String?
        let name: String
        let blobId: String?
        let size: Int
        let isFolder: Bool
        let type: String?
        let created: Date?
        let modified: Date?
        let mayWrite: Bool
    }

    private var nodeCacheURL: URL {
        stateDir.appendingPathComponent("nodes-\(accountId).json")
    }

    /// Save the current node tree to disk (atomic write).
    public func saveNodeCache() {
        lock.lock()
        var entries: [String: CodableNodeEntry] = [:]
        for (id, node) in nodes {
            entries[id] = CodableNodeEntry(
                parentId: node.parentId, name: node.name, blobId: node.blobId,
                size: node.size, isFolder: node.isFolder, type: node.type,
                created: node.created, modified: node.modified, mayWrite: node.mayWrite
            )
        }
        let snapshot = NodeCacheSnapshot(
            version: NodeCacheSnapshot.currentVersion,
            accountId: accountId,
            homeNodeId: homeNodeId,
            trashNodeId: trashNodeId,
            stateToken: stateToken,
            entries: entries
        )
        lock.unlock()

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(snapshot) else { return }

        // Atomic write: temp file + rename
        let tempURL = nodeCacheURL.appendingPathExtension("tmp")
        do {
            try data.write(to: tempURL, options: .atomic)
            try FileManager.default.moveItem(at: tempURL, to: nodeCacheURL)
        } catch {
            // Move can fail if destination exists; overwrite
            try? FileManager.default.removeItem(at: nodeCacheURL)
            try? FileManager.default.moveItem(at: tempURL, to: nodeCacheURL)
        }
    }

    /// Load node tree from disk cache. Returns true if cache was loaded.
    @discardableResult
    public func loadNodeCache() -> Bool {
        guard let data = try? Data(contentsOf: nodeCacheURL) else { return false }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let snapshot = try? decoder.decode(NodeCacheSnapshot.self, from: data),
              snapshot.version == NodeCacheSnapshot.currentVersion,
              snapshot.accountId == accountId,
              !snapshot.stateToken.isEmpty
        else { return false }

        lock.lock()
        defer { lock.unlock() }

        nodes.removeAll()
        children.removeAll()
        homeNodeId = snapshot.homeNodeId
        trashNodeId = snapshot.trashNodeId
        stateToken = snapshot.stateToken

        for (id, entry) in snapshot.entries {
            nodes[id] = NodeEntry(
                nodeId: id, parentId: entry.parentId, name: entry.name,
                blobId: entry.blobId, size: entry.size, isFolder: entry.isFolder,
                type: entry.type, created: entry.created, modified: entry.modified,
                mayWrite: entry.mayWrite
            )
            if let pid = entry.parentId {
                children[pid, default: []].append(id)
            }
        }
        return true
    }

    // MARK: - Smart startup

    /// Load cached data and sync incrementally if possible, otherwise full populate.
    /// Returns a description of what happened for logging.
    public func startUp() async throws -> String {
        let hasCachedData = loadNodeCache()

        if hasCachedData {
            let cachedCount = nodeCount
            // Try incremental sync
            do {
                try await sync()
                saveNodeCache()
                let newCount = nodeCount
                return "Loaded \(cachedCount) cached nodes, synced to \(newCount)"
            } catch let error as JmapError {
                if case .cannotCalculateChanges = error {
                    // State token too old, fall back to full populate
                } else {
                    // Network error — use cached data (offline mode)
                    return "Offline: using \(cachedCount) cached nodes"
                }
            } catch {
                // Network unreachable — use cached data
                return "Offline: using \(nodeCount) cached nodes"
            }
        }

        // Full populate (first run or state token expired)
        try await populate()
        saveNodeCache()
        return "Fetched \(nodeCount) nodes from server"
    }

    // MARK: - Populate (initial sync)

    /// Fetch all nodes from the server and build the tree.
    public func populate() async throws {
        let home = try await jmapClient.findHomeNode(accountId: accountId)
        let trash = try await jmapClient.findTrashNode(accountId: accountId)

        let allIds = try await jmapClient.queryAllNodeIds(accountId: accountId)
        let allNodes = try await jmapClient.getNodes(accountId: accountId, ids: allIds)

        // Get the current state token via a minimal get call
        let responses = try await jmapClient.call([
            JmapMethodCall(name: "FileNode/get", args: [
                "accountId": AnyCodable(accountId),
                "ids": AnyCodable([AnyCodable(home.id)]),
            ], callId: "g0")
        ])
        var state: String? = nil
        if let stateResponse = responses.first,
           stateResponse.count >= 3,
           let argsDict = stateResponse[1].dictValue {
            state = argsDict["state"]?.stringValue
        }

        // Apply everything under lock (non-async)
        applyPopulate(homeId: home.id, trashId: trash?.id,
                      allNodes: allNodes, state: state)
    }

    /// Apply populate results under lock. Must not be called from async context.
    private func applyPopulate(homeId: String, trashId: String?,
                               allNodes: [FileNode], state: String?) {
        lock.lock()
        defer { lock.unlock() }

        nodes.removeAll()
        children.removeAll()
        homeNodeId = homeId
        trashNodeId = trashId

        for node in allNodes {
            guard let nodeName = node.name else { continue }
            let entry = NodeEntry(
                nodeId: node.id, parentId: node.parentId,
                name: nodeName, blobId: node.blobId,
                size: node.size ?? 0, isFolder: node.isFolder,
                type: node.type, created: node.created,
                modified: node.modified, mayWrite: node.myRights?.mayWrite ?? true
            )
            nodes[node.id] = entry
            if let pid = node.parentId {
                children[pid, default: []].append(node.id)
            }
        }

        if let state = state {
            stateToken = state
        }
    }

    // MARK: - Incremental sync

    /// Apply incremental changes from the server. Saves node cache after.
    public func sync() async throws {
        let currentState = withLock({ self.stateToken })
        guard !currentState.isEmpty else { return }

        let result = try await jmapClient.getChanges(accountId: accountId, sinceState: currentState)
        applyChanges(changes: result.changes, created: result.created, updated: result.updated)
        saveNodeCache()
    }

    /// Apply changes under lock. Must not be called from async context.
    private func applyChanges(changes: FileNodeChangesResponse,
                              created: [FileNode], updated: [FileNode]) {
        lock.lock()
        defer { lock.unlock() }

        for id in changes.destroyed {
            if let node = nodes.removeValue(forKey: id),
               let pid = node.parentId {
                children[pid]?.removeAll { $0 == id }
            }
            blobCache.removeValue(forKey: id)
        }

        for node in created {
            upsertNode(node)
        }

        for node in updated {
            if let existing = nodes[node.id], existing.blobId != node.blobId {
                blobCache.removeValue(forKey: node.id)
            }
            upsertNode(node)
        }

        stateToken = changes.newState
    }

    private func upsertNode(_ node: FileNode) {
        guard let nodeName = node.name else { return } // Skip partial set responses
        // Remove from old parent's children list if parent changed
        if let existing = nodes[node.id],
           existing.parentId != node.parentId,
           let oldPid = existing.parentId {
            children[oldPid]?.removeAll { $0 == node.id }
        }

        let entry = NodeEntry(
            nodeId: node.id, parentId: node.parentId,
            name: nodeName, blobId: node.blobId,
            size: node.size ?? 0, isFolder: node.isFolder,
            type: node.type, created: node.created,
            modified: node.modified, mayWrite: node.myRights?.mayWrite ?? true
        )
        nodes[node.id] = entry

        if let pid = node.parentId {
            if children[pid] == nil {
                children[pid] = [node.id]
            } else if !children[pid]!.contains(node.id) {
                children[pid]!.append(node.id)
            }
        }
    }

    /// Number of nodes currently loaded.
    public var nodeCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return nodes.count
    }

    // MARK: - Lock helper

    /// Execute a closure under the lock (non-async safe).
    func withLock<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    // MARK: - Path resolution

    /// Resolve a POSIX path to a node ID. "/" maps to homeNodeId.
    func resolveNodeId(path: String) -> String? {
        if path == "/" { return homeNodeId }

        let components = path.split(separator: "/", omittingEmptySubsequences: true)
        var currentId = homeNodeId

        for component in components {
            let name = String(component)
            guard let childIds = children[currentId] else { return nil }
            var found = false
            for childId in childIds {
                if let child = nodes[childId], child.name == name {
                    currentId = childId
                    found = true
                    break
                }
            }
            if !found { return nil }
        }
        return currentId
    }

    // MARK: - Disk cache

    func diskCachePath(blobId: String) -> URL {
        let safe = blobId.replacingOccurrences(of: "/", with: "_")
                         .replacingOccurrences(of: ":", with: "_")
        return blobCacheDir.appendingPathComponent(safe)
    }

    func readDiskCache(blobId: String) -> Data? {
        try? Data(contentsOf: diskCachePath(blobId: blobId))
    }

    func writeDiskCache(blobId: String, data: Data) {
        try? data.write(to: diskCachePath(blobId: blobId))
    }

    // MARK: - Hydration (blocking bridge to async)

    /// Download a blob synchronously (blocks the FUSE thread).
    /// Deduplicates concurrent requests for the same blobId — only one download
    /// runs at a time, others wait for it to complete and share the result.
    func hydrateBlob(nodeId: String, blobId: String, name: String, type: String?) -> Data? {
        // Check memory cache (under lock)
        lock.lock()
        if let cached = blobCache[nodeId] {
            lock.unlock()
            return cached
        }
        lock.unlock()

        // Check disk cache
        if let cached = readDiskCache(blobId: blobId) {
            lock.lock()
            blobCache[nodeId] = cached
            lock.unlock()
            return cached
        }

        // Check if another thread is already downloading this blob
        lock.lock()
        if let existingSemaphore = inflightDownloads[blobId] {
            lock.unlock()
            // Wait for the other download to finish
            existingSemaphore.wait()
            existingSemaphore.signal() // Re-signal so other waiters also proceed
            // Now check caches — the first downloader populated them
            if let cached = readDiskCache(blobId: blobId) {
                lock.lock()
                blobCache[nodeId] = cached
                lock.unlock()
                return cached
            }
            return nil // Download failed
        }

        // We're the first — create a semaphore and start the download
        let downloadSemaphore = DispatchSemaphore(value: 0)
        inflightDownloads[blobId] = downloadSemaphore
        lock.unlock()

        // Download from server
        let client = self.jmapClient
        let acctId = self.accountId
        let data: Data? = blockingAsync { () async -> Data? in
            do {
                let tempDir = FileManager.default.temporaryDirectory
                let fileURL = try await client.downloadBlob(
                    accountId: acctId, blobId: blobId,
                    name: name, type: type, destinationDir: tempDir)
                let data = try Data(contentsOf: fileURL)
                try? FileManager.default.removeItem(at: fileURL)
                return data
            } catch {
                return nil
            }
        }

        // Populate caches and signal waiters
        if let data = data {
            writeDiskCache(blobId: blobId, data: data)
            lock.lock()
            blobCache[nodeId] = data
            lock.unlock()
        }

        lock.lock()
        inflightDownloads.removeValue(forKey: blobId)
        lock.unlock()
        downloadSemaphore.signal()

        return data
    }

    /// Upload data and create/replace a node synchronously.
    func uploadAndReplace(parentId: String, name: String, data: Data,
                          existingNodeId: String?) -> FileNode? {
        let client = self.jmapClient
        let acctId = self.accountId
        return blockingAsync { () async -> FileNode? in
            do {
                let tempFile = FileManager.default.temporaryDirectory
                    .appendingPathComponent(UUID().uuidString)
                try data.write(to: tempFile)
                defer { try? FileManager.default.removeItem(at: tempFile) }

                let contentType = "application/octet-stream"
                let blob = try await client.uploadBlobChunked(
                    accountId: acctId, fileURL: tempFile, contentType: contentType)

                let node = try await client.createNode(
                    accountId: acctId, parentId: parentId, name: name,
                    blobId: blob.blobId, onExists: "replace")

                if let oldId = existingNodeId, oldId != node.id {
                    try? await client.destroyNode(accountId: acctId, nodeId: oldId)
                }

                return node
            } catch {
                return nil
            }
        }
    }

    /// Create a folder synchronously.
    func createFolderSync(parentId: String, name: String) -> FileNode? {
        let client = self.jmapClient
        let acctId = self.accountId
        return blockingAsync { () async -> FileNode? in
            try? await client.createNode(accountId: acctId, parentId: parentId, name: name)
        }
    }

    /// Rename/move a node synchronously.
    func renameSync(nodeId: String, newParentId: String?, newName: String?) -> Bool {
        let client = self.jmapClient
        let acctId = self.accountId
        return blockingAsync { () async -> Bool in
            do {
                try await client.updateNode(
                    accountId: acctId, nodeId: nodeId,
                    parentId: newParentId, name: newName)
                return true
            } catch {
                return false
            }
        }
    }

    /// Destroy a node synchronously.
    func destroySync(nodeId: String) -> Bool {
        let client = self.jmapClient
        let acctId = self.accountId
        return blockingAsync { () async -> Bool in
            do {
                try await client.destroyNode(accountId: acctId, nodeId: nodeId)
                return true
            } catch {
                return false
            }
        }
    }
}

// MARK: - Async → Sync Bridge

/// Run an async closure synchronously by blocking on a semaphore.
/// The closure must capture only Sendable values.
func blockingAsync<T: Sendable>(_ operation: @escaping @Sendable () async -> T) -> T {
    nonisolated(unsafe) var result: T?
    let semaphore = DispatchSemaphore(value: 0)
    Task {
        result = await operation()
        semaphore.signal()
    }
    semaphore.wait()
    return result!
}
