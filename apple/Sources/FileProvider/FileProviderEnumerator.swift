import FileProvider
import JmapClient
#if canImport(os)
import os
#endif

/// Enumerates items for the FileProvider system.
///
/// Two modes:
/// - Initial enumeration: lists all items in a container (folder or working set)
/// - Change enumeration: reports changes since a sync anchor (JMAP state token)
public final class FileProviderEnumerator: NSObject, NSFileProviderEnumerator, @unchecked Sendable {
    private let containerIdentifier: NSFileProviderItemIdentifier
    private let database: NodeDatabase
    private let client: JmapClient
    private let syncEngine: SyncEngine
    private let accountId: String
    private let accountName: String
    private let specialNodes: Task<SpecialNodes, Error>
    private let activityTracker: ActivityTracker?
    private let statusWriter: ExtensionStatusWriter?

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "Enumerator")
    #endif

    init(
        container: NSFileProviderItemIdentifier,
        database: NodeDatabase,
        client: JmapClient,
        syncEngine: SyncEngine,
        accountId: String,
        accountName: String = "",
        specialNodes: Task<SpecialNodes, Error>,
        activityTracker: ActivityTracker? = nil,
        statusWriter: ExtensionStatusWriter? = nil
    ) {
        self.containerIdentifier = container
        self.database = database
        self.client = client
        self.syncEngine = syncEngine
        self.accountId = accountId
        self.accountName = accountName
        self.specialNodes = specialNodes
        self.activityTracker = activityTracker
        self.statusWriter = statusWriter
    }

    // MARK: - NSFileProviderEnumerator

    public func invalidate() {
        // Nothing to clean up
    }

    /// Return the current sync anchor (JMAP state token).
    public func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        let db = database
        Task.detached {
            let stateToken = await db.stateToken
            if let token = stateToken, let data = token.data(using: .utf8) {
                completionHandler(NSFileProviderSyncAnchor(data))
            } else {
                completionHandler(nil)
            }
        }
    }

    /// Enumerate items in a container (initial load or pagination).
    public func enumerateItems(
        for observer: NSFileProviderEnumerationObserver,
        startingAt page: NSFileProviderPage
    ) {
        let enumerator = self
        Task.detached {
            do {
                try await enumerator.performEnumeration(for: observer, startingAt: page)
            } catch {
                observer.finishEnumeratingWithError(enumerator.mapError(error))
            }
        }
    }

    /// Enumerate changes since a sync anchor.
    public func enumerateChanges(
        for observer: NSFileProviderChangeObserver,
        from syncAnchor: NSFileProviderSyncAnchor
    ) {
        let enumerator = self
        Task.detached {
            do {
                try await enumerator.performChangeEnumeration(for: observer, from: syncAnchor)
            } catch {
                observer.finishEnumeratingWithError(enumerator.mapError(error))
            }
        }
    }

    // MARK: - Enumeration Implementation

    private func performEnumeration(
        for observer: NSFileProviderEnumerationObserver,
        startingAt page: NSFileProviderPage
    ) async throws {
        if containerIdentifier == .workingSet {
            try await enumerateWorkingSet(for: observer, startingAt: page)
        } else {
            try await enumerateFolder(for: observer, startingAt: page)
        }
    }

    /// Enumerate the working set — all items the system needs to know about.
    ///
    /// BFS from home using per-folder parentId queries, batching up to 16 folders per
    /// HTTP request. Only fetches nodes reachable from home (excludes orphaned nodes).
    /// Reports items progressively so Finder populates as data arrives.
    private func enumerateWorkingSet(
        for observer: NSFileProviderEnumerationObserver,
        startingAt page: NSFileProviderPage
    ) async throws {
        let nodes = try await specialNodes.value

        let activityId = "enum:\(accountId):working-set"
        let label = accountName.isEmpty ? "Downloading node list" : "Downloading node list (\(accountName))"
        statusWriter?.setSyncing()
        await activityTracker?.start(
            id: activityId, accountId: accountId,
            fileName: label, action: .sync)

        var effectiveTrashId: String? = nodes.trashId
        var allNodes: [FileNode] = []
        var finalState: String = ""

        // BFS queue of folder IDs whose children need to be fetched.
        var folderQueue: [String] = [nodes.homeId]
        var itemBatch: [FileProviderItem] = []
        let itemBatchSize = 100
        let folderBatchSize = 16  // 16 folders → 32 method calls per HTTP request

        do {
            while !folderQueue.isEmpty {
                let batchParents = Array(folderQueue.prefix(folderBatchSize))
                folderQueue.removeFirst(min(folderBatchSize, folderQueue.count))

                let (childrenByParent, state) = try await client.getChildrenBatched(
                    accountId: accountId, parentIds: batchParents)
                if !state.isEmpty { finalState = state }

                for parentId in batchParents {
                    for node in childrenByParent[parentId] ?? [] {
                        allNodes.append(node)
                        if node.isTrash { effectiveTrashId = node.id }

                        if !node.isHome && !node.isRoot {
                            itemBatch.append(FileProviderItem(
                                node: node, homeNodeId: nodes.homeId, trashNodeId: effectiveTrashId))
                            if itemBatch.count >= itemBatchSize {
                                observer.didEnumerate(itemBatch)
                                itemBatch.removeAll()
                            }
                        }

                        if node.isFolder {
                            folderQueue.append(node.id)
                        }
                    }
                }
            }

            if !itemBatch.isEmpty {
                observer.didEnumerate(itemBatch)
            }

            for node in allNodes {
                await database.upsertFromServer(node)
            }
            await database.setHomeNodeId(nodes.homeId)
            await database.setTrashNodeId(effectiveTrashId)
            await database.setStateToken(finalState)
            try await database.save()

            await activityTracker?.complete(id: activityId)
            statusWriter?.setIdle(nodeCount: allNodes.count)
            observer.finishEnumerating(upTo: nil)
        } catch {
            await activityTracker?.remove(id: activityId)
            statusWriter?.setIdle()
            throw error
        }
    }

    /// Enumerate children of a specific folder.
    private func enumerateFolder(
        for observer: NSFileProviderEnumerationObserver,
        startingAt page: NSFileProviderPage
    ) async throws {
        let nodes = try await specialNodes.value
        let parentId = resolveParentId(containerIdentifier, nodes: nodes)

        // Get children from cache first
        let children = await database.children(of: parentId)

        let items = children.map { (id, entry) in
            FileProviderItem(nodeId: id, entry: entry, homeNodeId: nodes.homeId, trashNodeId: nodes.trashId)
        }

        observer.didEnumerate(items)
        observer.finishEnumerating(upTo: nil)
    }

    // MARK: - Change Enumeration Implementation

    private func performChangeEnumeration(
        for observer: NSFileProviderChangeObserver,
        from syncAnchor: NSFileProviderSyncAnchor
    ) async throws {
        guard let stateToken = String(data: syncAnchor.rawValue, encoding: .utf8) else {
            throw NSFileProviderError(.syncAnchorExpired)
        }

        let activityId = "sync:\(accountId):changes"
        let label = accountName.isEmpty ? "Checking for changes" : "Checking for changes (\(accountName))"
        statusWriter?.setSyncing()
        await activityTracker?.start(
            id: activityId, accountId: accountId,
            fileName: label, action: .sync)

        do {
            let nodes = try await specialNodes.value
            // SyncEngine.fetchChanges handles the JMAP protocol + database update.
            // The enumerator is responsible for the FileProvider observer callbacks only.
            let (updatedNodes, deletedIds) = try await syncEngine.fetchChanges()

            let updatedItems = updatedNodes.filter { !$0.isHome && !$0.isRoot }.map {
                FileProviderItem(node: $0, homeNodeId: nodes.homeId, trashNodeId: nodes.trashId)
            }
            if !updatedItems.isEmpty { observer.didUpdate(updatedItems) }

            let deletedIdentifiers = deletedIds.map { NSFileProviderItemIdentifier($0) }
            if !deletedIdentifiers.isEmpty { observer.didDeleteItems(withIdentifiers: deletedIdentifiers) }

            let newToken = await database.stateToken ?? stateToken
            let newAnchor = NSFileProviderSyncAnchor(newToken.data(using: .utf8)!)
            let totalChanges = updatedNodes.count + deletedIds.count
            if totalChanges > 0 {
                await activityTracker?.complete(id: activityId)
            } else {
                await activityTracker?.remove(id: activityId)
            }
            statusWriter?.setIdle()
            observer.finishEnumeratingChanges(upTo: newAnchor, moreComing: false)

        } catch JmapError.cannotCalculateChanges {
            await activityTracker?.remove(id: activityId)
            statusWriter?.setIdle()
            // State token too old — clear it immediately so a crash won't loop
            await database.setStateToken("")
            try? await database.save()
            #if canImport(os)
            logger.info("State token expired, requesting full re-enumeration")
            #endif
            observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
        } catch {
            await activityTracker?.remove(id: activityId)
            statusWriter?.setIdle()
            throw error
        }
    }

    // MARK: - Helpers

    /// Resolve a FileProvider item identifier to a JMAP nodeId.
    private func resolveParentId(_ identifier: NSFileProviderItemIdentifier, nodes: SpecialNodes) -> String {
        switch identifier {
        case .rootContainer: return nodes.homeId
        case .trashContainer: return nodes.trashId ?? nodes.homeId
        default: return identifier.rawValue
        }
    }

    /// Fetch the current JMAP state token.
    private func fetchCurrentState() async throws -> String {
        // Use FileNode/changes with current state to get the latest
        // If we have no state, query to get one
        if let existing = await database.stateToken {
            return existing
        }

        // Fetch a single node to get the state token
        let responses = try await client.call([
            JmapMethodCall(
                name: "FileNode/get",
                args: [
                    "accountId": AnyCodable(accountId),
                    "ids": AnyCodable([AnyCodable]()),
                    "properties": AnyCodable([AnyCodable("id")]),
                ],
                callId: "g0"
            ),
        ])

        // Extract state from the response
        for response in responses {
            guard response.count >= 3,
                  response[2].stringValue == "g0",
                  let dict = response[1].dictValue,
                  let state = dict["state"]?.stringValue
            else { continue }
            return state
        }

        throw JmapError.invalidResponse
    }

    /// Map internal errors to NSFileProviderError for the system.
    private func mapError(_ error: Error) -> Error {
        if let jmapError = error as? JmapError {
            switch jmapError {
            case .unauthorized, .forbidden:
                return NSFileProviderError(.notAuthenticated)
            case .cannotCalculateChanges:
                return NSFileProviderError(.syncAnchorExpired)
            case .notFound(let id):
                return NSFileProviderError(.noSuchItem)
            case .httpError(let code, _) where code >= 500:
                return NSFileProviderError(.serverUnreachable)
            case .rateLimited:
                return NSFileProviderError(.serverUnreachable)
            case .payloadTooLarge:
                return NSFileProviderError(.insufficientQuota)
            default:
                return NSFileProviderError(.cannotSynchronize)
            }
        }
        if (error as NSError).domain == NSURLErrorDomain {
            return NSFileProviderError(.serverUnreachable)
        }
        return error
    }
}
