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
            if let token = stateToken, !token.isEmpty, let data = token.data(using: .utf8) {
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

        #if canImport(os)
        let existingToken = await database.stateToken ?? "(none)"
        logger.info("[\(self.accountId, privacy: .public)] enumerateWorkingSet start — stateToken=\(existingToken, privacy: .public)")
        #endif

        let activityId = "enum:\(accountId):working-set"
        let label = accountName.isEmpty ? "Downloading node list" : "Downloading node list (\(accountName))"
        statusWriter?.setSyncing()
        await activityTracker?.start(
            id: activityId, accountId: accountId,
            fileName: label, action: .sync)

        var effectiveTrashId: String? = nodes.trashId
        var allNodes: [FileNode] = []
        var finalState: String = ""
        let pinnedIds = await database.allPinnedIds

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
                                node: node, homeNodeId: nodes.homeId, trashNodeId: effectiveTrashId,
                                isPinned: pinnedIds.contains(node.id)))
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

            await database.cleanupOrphanedPins()
            await database.setEnumerationFailureCount(0)
            await activityTracker?.complete(id: activityId)
            statusWriter?.setIdle(nodeCount: allNodes.count)
            #if canImport(os)
            logger.info("[\(self.accountId, privacy: .public)] enumerateWorkingSet done — \(allNodes.count) nodes, stateToken=\(finalState, privacy: .public)")
            #endif
            observer.finishEnumerating(upTo: nil)
        } catch {
            await activityTracker?.remove(id: activityId)
            statusWriter?.setIdle()
            #if canImport(os)
            logger.error("[\(self.accountId, privacy: .public)] enumerateWorkingSet failed — \(error.localizedDescription, privacy: .public)")
            #endif
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

        let children = await database.children(of: parentId)
        let pinnedIds = await database.allPinnedIds

        let currentStateToken = await database.stateToken ?? ""
        if children.isEmpty && currentStateToken.isEmpty {
            // Database not yet populated — fetch this folder's children from the server
            // so the user sees content immediately rather than waiting for the BFS.
            let (childrenByParent, _) = try await client.getChildrenBatched(
                accountId: accountId, parentIds: [parentId])
            let fetched = childrenByParent[parentId] ?? []
            for node in fetched { await database.upsertFromServer(node) }
            let items = fetched.map {
                FileProviderItem(node: $0, homeNodeId: nodes.homeId, trashNodeId: nodes.trashId,
                                 isPinned: pinnedIds.contains($0.id))
            }
            observer.didEnumerate(items)
            observer.finishEnumerating(upTo: nil)
            return
        }

        let items = children.map { (id, entry) in
            FileProviderItem(nodeId: id, entry: entry, homeNodeId: nodes.homeId, trashNodeId: nodes.trashId,
                             isPinned: pinnedIds.contains(id))
        }
        observer.didEnumerate(items)
        observer.finishEnumerating(upTo: nil)
    }

    // MARK: - Change Enumeration Implementation

    private func performChangeEnumeration(
        for observer: NSFileProviderChangeObserver,
        from syncAnchor: NSFileProviderSyncAnchor
    ) async throws {
        guard let stateToken = String(data: syncAnchor.rawValue, encoding: .utf8),
              !stateToken.isEmpty else {
            observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
            return
        }

        // If our database has no state token the anchor is stale from our side —
        // force a full re-enumeration rather than reporting "no changes" with an empty DB.
        let dbToken = await database.stateToken ?? ""
        guard !dbToken.isEmpty else {
            let failCount = await database.enumerationFailureCount
            // Cap consecutive re-enumeration attempts. Each syncAnchorExpired here means
            // enumerateWorkingSet either hasn't run yet or failed to persist a state token.
            // After 5 consecutive failures something is broken — stop looping and surface an error.
            if failCount >= 5 {
                #if canImport(os)
                logger.error("[\(self.accountId, privacy: .public)] enumerateChanges: \(failCount) consecutive failures with no state token — sync halted, reset cache to repair")
                #endif
                statusWriter?.setError("Sync halted after repeated failures. Reset cache to repair.")
                observer.finishEnumeratingWithError(NSFileProviderError(.cannotSynchronize))
                return
            }
            await database.setEnumerationFailureCount(failCount + 1)
            #if canImport(os)
            logger.info("[\(self.accountId, privacy: .public)] enumerateChanges: DB has no state token (attempt \(failCount + 1)/5) — requesting full re-enumeration")
            #endif
            observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
            return
        }

        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] enumerateChanges start — anchor=\(stateToken, privacy: .public)")
        #endif

        let activityId = "sync:\(accountId):changes"

        do {
            let nodes = try await specialNodes.value
            // fetchChanges applies all node writes to the DB but does NOT advance the
            // state token — that is our responsibility, and we do it last (see below).
            let (updatedNodes, deletedIds, newState) = try await syncEngine.fetchChanges()

            let updatedItems = updatedNodes.filter { !$0.isHome && !$0.isRoot }.map {
                FileProviderItem(node: $0, homeNodeId: nodes.homeId, trashNodeId: nodes.trashId)
            }
            if !updatedItems.isEmpty { observer.didUpdate(updatedItems) }

            let deletedIdentifiers = deletedIds.map { NSFileProviderItemIdentifier($0) }
            if !deletedIdentifiers.isEmpty { observer.didDeleteItems(withIdentifiers: deletedIdentifiers) }

            // Use the server's new state as the anchor. Fall back to the system's
            // incoming anchor if fetchChanges returned nothing (no-op poll).
            let effectiveNewState = newState.isEmpty ? stateToken : newState
            let newAnchor = NSFileProviderSyncAnchor(effectiveNewState.data(using: .utf8)!)
            let totalChanges = updatedNodes.count + deletedIds.count
            if totalChanges > 0 {
                // Only report syncing/activity when there are actual changes to apply.
                // Routine no-op polls must not flash the status — it causes visual noise
                // because the extension process is killed and restarted frequently.
                let label = accountName.isEmpty ? "Applying changes" : "Applying changes (\(accountName))"
                statusWriter?.setSyncing()
                await activityTracker?.start(
                    id: activityId, accountId: accountId,
                    fileName: label, action: .sync)
                await activityTracker?.complete(id: activityId)
            }
            statusWriter?.setIdle()
            #if canImport(os)
            logger.info("[\(self.accountId, privacy: .public)] enumerateChanges done — \(updatedNodes.count) updated, \(deletedIds.count) deleted, newAnchor=\(effectiveNewState, privacy: .public)")
            #endif
            observer.finishEnumeratingChanges(upTo: newAnchor, moreComing: false)

            // Commit the new state token only after the system has acknowledged the
            // changes. If the extension is killed between the DB writes above and this
            // line, the old token stays in the DB and the next restart re-applies the
            // same changes via idempotent upserts/removes — no data is lost or doubled.
            if !newState.isEmpty {
                await database.setStateToken(newState)
            }

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
