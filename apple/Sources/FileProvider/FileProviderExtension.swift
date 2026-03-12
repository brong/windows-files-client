import FileProvider
import UniformTypeIdentifiers
import JmapClient
#if canImport(os)
import os
#endif

/// The main FileProvider extension class.
///
/// Implements `NSFileProviderReplicatedExtension` — the system calls this
/// to enumerate items, download content, and propagate local changes.
///
/// Lifecycle: the system launches this extension on demand and may terminate
/// it at any time. All state is persisted to the App Group shared container.
public final class FileProviderExtension: NSObject, NSFileProviderReplicatedExtension,
    NSFileProviderEnumerating, NSFileProviderThumbnailing
{
    private let domain: NSFileProviderDomain
    private var database: NodeDatabase!
    private var client: JmapClient!
    private var sessionManager: SessionManager!
    private var pushWatcher: PushWatcher!
    private var accountId: String!
    private var homeNodeId: String!
    private var trashNodeId: String?

    // App Group identifier — must match entitlements
    #if os(macOS)
    // macOS: TEAMID.com.fastmail.files
    // TODO: Replace TEAMID with actual team identifier
    private static let appGroupId = "TEAMID.com.fastmail.files"
    #else
    // iOS: group.com.fastmail.files
    private static let appGroupId = "group.com.fastmail.files"
    #endif

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "Extension")
    #endif

    // MARK: - Lifecycle

    public required init(domain: NSFileProviderDomain) {
        self.domain = domain
        super.init()

        // The domain identifier is the JMAP accountId
        self.accountId = domain.identifier.rawValue

        // Initialize from shared container
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId)
        else {
            #if canImport(os)
            logger.error("Failed to access App Group container")
            #endif
            return
        }

        // Load config from shared UserDefaults
        let defaults = UserDefaults(suiteName: Self.appGroupId)
        let sessionURLString = defaults?.string(forKey: "sessionURL-\(accountId!)")
            ?? "https://api.fastmail.com/jmap/session"
        guard let sessionURL = URL(string: sessionURLString) else { return }

        // Set up auth
        let tokenProvider = KeychainTokenProvider(
            account: accountId,
            accessGroup: Self.appGroupId
        )

        // Initialize components
        self.sessionManager = SessionManager(sessionURL: sessionURL, tokenProvider: tokenProvider)
        self.client = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider)
        self.database = NodeDatabase(containerURL: containerURL, accountId: accountId)

        // Set up push watcher
        self.pushWatcher = PushWatcher(sessionManager: sessionManager, tokenProvider: tokenProvider, accountId: accountId)

        Task {
            await initializeFromDatabase()
            await startPushWatcher()
        }
    }

    public func invalidate() {
        #if canImport(os)
        logger.info("Extension invalidating")
        #endif
        Task {
            await pushWatcher.stop()
        }
    }

    // MARK: - Item Lookup

    public func item(
        for identifier: NSFileProviderItemIdentifier,
        request: NSFileProviderRequest,
        completionHandler: @escaping (NSFileProviderItem?, Error?) -> Void
    ) -> Progress {
        Task {
            do {
                let item = try await lookupItem(identifier)
                completionHandler(item, nil)
            } catch {
                completionHandler(nil, mapError(error))
            }
        }
        return Progress()
    }

    // MARK: - Content Fetch (Hydration)

    public func fetchContents(
        for itemIdentifier: NSFileProviderItemIdentifier,
        version requestedVersion: NSFileProviderItemVersion?,
        request: NSFileProviderRequest,
        completionHandler: @escaping (URL?, NSFileProviderItem?, Error?) -> Void
    ) -> Progress {
        let progress = Progress(totalUnitCount: 100)

        Task {
            do {
                let nodeId = itemIdentifier.rawValue
                guard let entry = await database.entry(for: nodeId) else {
                    throw JmapError.notFound(nodeId)
                }

                guard let blobId = entry.blobId else {
                    throw JmapError.serverError("isFolder", "Cannot fetch contents of a folder")
                }

                // Download to temp directory in the App Group container
                guard let containerURL = FileManager.default.containerURL(
                    forSecurityApplicationGroupIdentifier: Self.appGroupId)
                else {
                    throw JmapError.invalidResponse
                }

                let tempDir = containerURL.appendingPathComponent("tmp", isDirectory: true)
                try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)

                let tempURL = try await client.downloadBlob(
                    accountId: accountId,
                    blobId: blobId,
                    name: entry.name,
                    type: entry.type,
                    destinationDir: tempDir
                ) { downloaded, total in
                    if total > 0 {
                        progress.completedUnitCount = Int64(Double(downloaded) / Double(total) * 100)
                    }
                }

                progress.completedUnitCount = 100

                let item = FileProviderItem(
                    nodeId: nodeId, entry: entry,
                    homeNodeId: homeNodeId, trashNodeId: trashNodeId)
                completionHandler(tempURL, item, nil)
            } catch {
                completionHandler(nil, nil, mapError(error))
            }
        }

        progress.cancellationHandler = {
            // TODO: Cancel in-flight download
        }

        return progress
    }

    // MARK: - Create Item

    public func createItem(
        basedOn itemTemplate: NSFileProviderItem,
        fields: NSFileProviderItemFields,
        contents url: URL?,
        options: NSFileProviderCreateItemOptions,
        request: NSFileProviderRequest,
        completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
    ) -> Progress {
        let progress = Progress(totalUnitCount: 100)

        Task {
            do {
                let parentId = resolveNodeId(itemTemplate.parentItemIdentifier)

                if itemTemplate.contentType == .folder {
                    // Create folder
                    let node = try await client.createNode(
                        accountId: accountId,
                        parentId: parentId,
                        name: desanitizeFilename(itemTemplate.filename)
                    )
                    await database.upsertFromServer(node)
                    try await database.save()

                    let item = FileProviderItem(
                        node: node, homeNodeId: homeNodeId, trashNodeId: trashNodeId)
                    completionHandler(item, [], false, nil)
                } else {
                    // Create file — upload blob then create node
                    guard let contentURL = url else {
                        throw JmapError.uploadFailed("No content URL provided")
                    }

                    let contentType = itemTemplate.contentType?.preferredMIMEType ?? "application/octet-stream"
                    let blob = try await client.uploadBlob(
                        accountId: accountId,
                        fileURL: contentURL,
                        contentType: contentType
                    )

                    progress.completedUnitCount = 80

                    let node = try await client.createNode(
                        accountId: accountId,
                        parentId: parentId,
                        name: desanitizeFilename(itemTemplate.filename),
                        blobId: blob.blobId,
                        type: contentType,
                        created: itemTemplate.creationDate ?? nil,
                        modified: itemTemplate.contentModificationDate ?? nil,
                        onExists: options.contains(.mayAlreadyExist) ? "replace" : nil
                    )

                    progress.completedUnitCount = 100

                    await database.upsertFromServer(node)
                    try await database.save()

                    let item = FileProviderItem(
                        node: node, homeNodeId: homeNodeId, trashNodeId: trashNodeId)
                    completionHandler(item, [], false, nil)
                }
            } catch {
                completionHandler(nil, [], false, mapError(error))
            }
        }

        return progress
    }

    // MARK: - Modify Item

    public func modifyItem(
        _ item: NSFileProviderItem,
        baseVersion version: NSFileProviderItemVersion,
        changedFields: NSFileProviderItemFields,
        contents newContents: URL?,
        options: NSFileProviderModifyItemOptions,
        request: NSFileProviderRequest,
        completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
    ) -> Progress {
        let progress = Progress(totalUnitCount: 100)

        Task {
            do {
                let nodeId = item.itemIdentifier.rawValue
                var currentNodeId = nodeId

                // Handle content change first (creates new node due to immutable blobId)
                if changedFields.contains(.contents), let contentURL = newContents {
                    let contentType = item.contentType?.preferredMIMEType ?? "application/octet-stream"
                    let parentId = resolveNodeId(item.parentItemIdentifier)

                    let blob = try await client.uploadBlob(
                        accountId: accountId,
                        fileURL: contentURL,
                        contentType: contentType
                    )
                    progress.completedUnitCount = 60

                    // Create with onExists:"replace" — destroys old node, creates new one
                    let newNode = try await client.createNode(
                        accountId: accountId,
                        parentId: parentId,
                        name: desanitizeFilename(item.filename),
                        blobId: blob.blobId,
                        type: contentType,
                        created: item.creationDate ?? nil,
                        modified: item.contentModificationDate ?? nil,
                        onExists: "replace"
                    )
                    progress.completedUnitCount = 80

                    // Update database: remove old, add new
                    await database.remove(nodeId: nodeId)
                    await database.upsertFromServer(newNode)
                    currentNodeId = newNode.id
                }

                // Handle rename
                if changedFields.contains(.filename) {
                    try await client.updateNode(
                        accountId: accountId,
                        nodeId: currentNodeId,
                        name: desanitizeFilename(item.filename)
                    )
                }

                // Handle move (parent change)
                if changedFields.contains(.parentItemIdentifier) {
                    let newParentId = resolveNodeId(item.parentItemIdentifier)

                    if newParentId == trashNodeId {
                        // Move to trash
                        try await client.updateNode(
                            accountId: accountId,
                            nodeId: currentNodeId,
                            parentId: newParentId,
                            onExists: "rename"
                        )
                    } else {
                        try await client.updateNode(
                            accountId: accountId,
                            nodeId: currentNodeId,
                            parentId: newParentId
                        )
                    }
                }

                progress.completedUnitCount = 100

                try await database.save()

                // Return the updated item
                guard let entry = await database.entry(for: currentNodeId) else {
                    throw JmapError.notFound(currentNodeId)
                }
                let resultItem = FileProviderItem(
                    nodeId: currentNodeId, entry: entry,
                    homeNodeId: homeNodeId, trashNodeId: trashNodeId)
                completionHandler(resultItem, [], false, nil)
            } catch {
                completionHandler(nil, [], false, mapError(error))
            }
        }

        return progress
    }

    // MARK: - Delete Item

    public func deleteItem(
        identifier: NSFileProviderItemIdentifier,
        baseVersion version: NSFileProviderItemVersion,
        options: NSFileProviderDeleteItemOptions,
        request: NSFileProviderRequest,
        completionHandler: @escaping (Error?) -> Void
    ) -> Progress {
        Task {
            do {
                let nodeId = identifier.rawValue

                if let trashId = trashNodeId {
                    // Move to trash (preferred)
                    do {
                        try await client.updateNode(
                            accountId: accountId,
                            nodeId: nodeId,
                            parentId: trashId,
                            onExists: "rename"
                        )
                    } catch JmapError.notFound {
                        // Already gone — success (idempotent)
                    }
                } else {
                    // Permanent delete
                    try await client.destroyNode(
                        accountId: accountId,
                        nodeId: nodeId,
                        removeChildren: true
                    )
                }

                await database.remove(nodeId: nodeId)
                try await database.save()

                completionHandler(nil)
            } catch {
                completionHandler(mapError(error))
            }
        }

        return Progress()
    }

    // MARK: - Enumeration

    public func enumerator(
        for containerItemIdentifier: NSFileProviderItemIdentifier,
        request: NSFileProviderRequest
    ) throws -> NSFileProviderEnumerator {
        guard database != nil else {
            throw NSFileProviderError(.notAuthenticated)
        }

        return FileProviderEnumerator(
            container: containerItemIdentifier,
            database: database,
            client: client,
            accountId: accountId,
            homeNodeId: homeNodeId ?? "",
            trashNodeId: trashNodeId
        )
    }

    // MARK: - Thumbnails

    public func fetchThumbnails(
        for itemIdentifiers: [NSFileProviderItemIdentifier],
        requestedSize: CGSize,
        perThumbnailCompletionHandler: @escaping (
            NSFileProviderItemIdentifier, Data?, Error?
        ) -> Void,
        completionHandler: @escaping (Error?) -> Void
    ) -> Progress {
        Task {
            for identifier in itemIdentifiers {
                let nodeId = identifier.rawValue
                guard let entry = await database.entry(for: nodeId),
                      let blobId = entry.blobId
                else {
                    perThumbnailCompletionHandler(identifier, nil, nil)
                    continue
                }

                // Check if server supports Blob/convert
                let session = try? await sessionManager.session()
                guard let session = session,
                      session.hasBlobExt(accountId: accountId)
                else {
                    perThumbnailCompletionHandler(identifier, nil, nil)
                    continue
                }

                // TODO: Implement Blob/convert call for server-side thumbnail generation
                // For now, return nil (system shows generic icon)
                perThumbnailCompletionHandler(identifier, nil, nil)
            }
            completionHandler(nil)
        }

        return Progress()
    }

    // MARK: - Private Helpers

    private func initializeFromDatabase() async {
        homeNodeId = await database.homeNodeId
        trashNodeId = await database.trashNodeId

        // If no cached home node, we need a full fetch on first enumeration
        if homeNodeId == nil {
            do {
                let home = try await client.findHomeNode(accountId: accountId)
                homeNodeId = home.id
                await database.setHomeNodeId(home.id)

                let trash = try await client.findTrashNode(accountId: accountId)
                trashNodeId = trash?.id
                await database.setTrashNodeId(trash?.id)

                try await database.save()
            } catch {
                #if canImport(os)
                logger.error("Failed to discover home/trash nodes: \(error.localizedDescription)")
                #endif
            }
        }
    }

    private func startPushWatcher() async {
        // Set up push delegate to signal working set enumerator
        // The PushWatcher needs a delegate — we'll use a bridge object
        await pushWatcher.start()
    }

    /// Resolve a FileProvider item identifier to a JMAP nodeId.
    private func resolveNodeId(_ identifier: NSFileProviderItemIdentifier) -> String {
        switch identifier {
        case .rootContainer: return homeNodeId ?? ""
        case .trashContainer: return trashNodeId ?? homeNodeId ?? ""
        default: return identifier.rawValue
        }
    }

    /// Look up an item by identifier from the database.
    private func lookupItem(_ identifier: NSFileProviderItemIdentifier) async throws -> FileProviderItem {
        let nodeId: String
        switch identifier {
        case .rootContainer:
            nodeId = homeNodeId ?? ""
        case .trashContainer:
            nodeId = trashNodeId ?? homeNodeId ?? ""
        default:
            nodeId = identifier.rawValue
        }

        guard let entry = await database.entry(for: nodeId) else {
            throw JmapError.notFound(nodeId)
        }

        return FileProviderItem(
            nodeId: nodeId, entry: entry,
            homeNodeId: homeNodeId ?? "", trashNodeId: trashNodeId)
    }

    /// Reverse filename sanitization (restore original characters for server).
    private func desanitizeFilename(_ name: String) -> String {
        var result = name
        result = result.replacingOccurrences(of: "\u{2215}", with: "/") // DIVISION SLASH → /
        result = result.replacingOccurrences(of: "\u{A789}", with: ":") // MODIFIER LETTER COLON → :
        return result
    }

    /// Map errors to NSFileProviderError.
    private func mapError(_ error: Error) -> Error {
        if let jmapError = error as? JmapError {
            switch jmapError {
            case .unauthorized, .forbidden:
                return NSFileProviderError(.notAuthenticated)
            case .cannotCalculateChanges:
                return NSFileProviderError(.syncAnchorExpired)
            case .notFound:
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
