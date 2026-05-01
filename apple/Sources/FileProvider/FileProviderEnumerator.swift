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
    private let accountId: String
    private let homeNodeId: String
    private let trashNodeId: String?
    private let activityTracker: ActivityTracker?
    private let statusWriter: ExtensionStatusWriter?

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "Enumerator")
    #endif

    init(
        container: NSFileProviderItemIdentifier,
        database: NodeDatabase,
        client: JmapClient,
        accountId: String,
        homeNodeId: String,
        trashNodeId: String?,
        activityTracker: ActivityTracker? = nil,
        statusWriter: ExtensionStatusWriter? = nil
    ) {
        self.containerIdentifier = container
        self.database = database
        self.client = client
        self.accountId = accountId
        self.homeNodeId = homeNodeId
        self.trashNodeId = trashNodeId
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
    private func enumerateWorkingSet(
        for observer: NSFileProviderEnumerationObserver,
        startingAt page: NSFileProviderPage
    ) async throws {
        let activityId = "enum:\(accountId):working-set"
        statusWriter?.setSyncing()
        await activityTracker?.start(
            id: activityId, accountId: accountId,
            fileName: "Downloading node list", action: .sync)

        // Fetch all nodes from the server
        let allIds = try await client.queryAllNodeIds(accountId: accountId)
        await activityTracker?.updateProgress(id: activityId, progress: 0.3)
        let allNodes = try await client.getNodes(accountId: accountId, ids: allIds)
        await activityTracker?.updateProgress(id: activityId, progress: 0.8)

        // Update database
        for node in allNodes {
            await database.upsertFromServer(node)
        }

        // Get the state token from the last get response
        // For now, do another changes call to get the current state
        let stateToken = try await fetchCurrentState()
        await database.setStateToken(stateToken)
        try await database.save()

        // Convert to FileProviderItems and report
        let items = allNodes.compactMap { node -> FileProviderItem? in
            // Skip home/root/trash nodes — they map to .rootContainer / .trashContainer
            if node.isHome || node.isRoot { return nil }
            if let trashId = trashNodeId, node.id == trashId { return nil }
            return FileProviderItem(node: node, homeNodeId: homeNodeId, trashNodeId: trashNodeId)
        }

        // Report in batches
        let batchSize = 100
        for startIndex in stride(from: 0, to: items.count, by: batchSize) {
            let endIndex = min(startIndex + batchSize, items.count)
            let batch = Array(items[startIndex..<endIndex])
            observer.didEnumerate(batch)
        }

        await activityTracker?.complete(id: activityId)
        statusWriter?.setIdle(nodeCount: allNodes.count)
        observer.finishEnumerating(upTo: nil)
    }

    /// Enumerate children of a specific folder.
    private func enumerateFolder(
        for observer: NSFileProviderEnumerationObserver,
        startingAt page: NSFileProviderPage
    ) async throws {
        let parentId = resolveParentId(containerIdentifier)

        // Get children from cache first
        let children = await database.children(of: parentId)

        let items = children.map { (id, entry) in
            FileProviderItem(nodeId: id, entry: entry, homeNodeId: homeNodeId, trashNodeId: trashNodeId)
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
        statusWriter?.setSyncing()
        await activityTracker?.start(
            id: activityId, accountId: accountId,
            fileName: "Checking for changes", action: .sync)

        do {
            let (changes, createdNodes, updatedNodes) = try await client.getChanges(
                accountId: accountId,
                sinceState: stateToken
            )

            // Process updates — report to system and update database
            var updatedItems: [FileProviderItem] = []

            for node in createdNodes {
                await database.upsertFromServer(node)
                if !node.isHome && !node.isRoot {
                    updatedItems.append(
                        FileProviderItem(node: node, homeNodeId: homeNodeId, trashNodeId: trashNodeId))
                }
            }

            for node in updatedNodes {
                await database.upsertFromServer(node)
                if !node.isHome && !node.isRoot {
                    updatedItems.append(
                        FileProviderItem(node: node, homeNodeId: homeNodeId, trashNodeId: trashNodeId))
                }
            }

            if !updatedItems.isEmpty {
                observer.didUpdate(updatedItems)
            }

            // Process deletes
            let destroyedIds = changes.destroyed.map { NSFileProviderItemIdentifier($0) }
            if !destroyedIds.isEmpty {
                observer.didDeleteItems(withIdentifiers: destroyedIds)
                await database.remove(nodeIds: changes.destroyed)
            }

            // Save new state
            await database.setStateToken(changes.newState)
            try await database.save()

            // Finish with new anchor
            let newAnchor = NSFileProviderSyncAnchor(changes.newState.data(using: .utf8)!)
            let totalChanges = createdNodes.count + updatedNodes.count + changes.destroyed.count
            if totalChanges > 0 {
                await activityTracker?.complete(id: activityId)
            } else {
                await activityTracker?.remove(id: activityId)
            }
            statusWriter?.setIdle()
            observer.finishEnumeratingChanges(upTo: newAnchor, moreComing: changes.hasMoreChanges ?? false)

        } catch JmapError.cannotCalculateChanges {
            await activityTracker?.remove(id: activityId)
            // State token too old — clear it immediately so a crash won't loop
            await database.setStateToken("")
            try? await database.save()
            #if canImport(os)
            logger.info("State token expired, requesting full re-enumeration")
            #endif
            observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
        }
    }

    // MARK: - Helpers

    /// Resolve a FileProvider item identifier to a JMAP nodeId.
    private func resolveParentId(_ identifier: NSFileProviderItemIdentifier) -> String {
        switch identifier {
        case .rootContainer: return homeNodeId
        case .trashContainer: return trashNodeId ?? homeNodeId
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
