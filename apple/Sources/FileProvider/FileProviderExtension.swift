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
    private var accountName: String = ""
    private var homeNodeId: String!
    private var trashNodeId: String?
    private var activityTracker: ActivityTracker!
    private var statusWriter: ExtensionStatusWriter!
    /// Set to true when the domain is being removed — prevents server-side deletes.
    private var isDomainBeingRemoved = false

    // App Group identifier — must match entitlements
    #if os(macOS)
    // macOS: BJL34Q426G.com.fastmail.files
    private static let appGroupId = "BJL34Q426G.com.fastmail.files"
    #else
    // iOS: group.com.fastmail.files
    private static let appGroupId = "group.com.fastmail.files"
    #endif

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "Extension")
    #endif

    // MARK: - Keychain Helper

    /// Read raw data from keychain synchronously (for use in init).
    private static func readKeychainData(service: String = "com.fastmail.files",
                                          account: String, accessGroup: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecAttrAccessGroup as String: accessGroup,
            kSecUseDataProtectionKeychain as String: true,
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { return nil }
        return data
    }

    // MARK: - Lifecycle

    public required init(domain: NSFileProviderDomain) {
        self.domain = domain
        super.init()

        // The domain identifier is the JMAP accountId
        self.accountId = domain.identifier.rawValue
        self.accountName = domain.displayName
        logger.info("Extension init for account: \(domain.identifier.rawValue, privacy: .public)")

        // Initialize from shared container
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: Self.appGroupId)
        else {
            logger.error("Failed to access App Group container")
            return
        }
        logger.info("Container URL: \(containerURL.path, privacy: .public)")

        // Load config from shared UserDefaults
        let defaults = UserDefaults(suiteName: Self.appGroupId)
        let sessionURLString = defaults?.string(forKey: "sessionURL-\(accountId!)")
            ?? "https://api.fastmail.com/jmap/session"
        guard let sessionURL = URL(string: sessionURLString) else { return }

        // Look up which login owns this account
        let loginId = defaults?.string(forKey: "loginForAccount-\(accountId!)")
        let authType = defaults?.string(forKey: "authType-\(accountId!)")
        let loginKeychainService = "com.fastmail.files.login"
        let keychainAccount = loginId ?? accountId!  // fall back to accountId for legacy

        logger.info("Auth type for \(self.accountId!, privacy: .public): \(authType ?? "nil", privacy: .public)")
        logger.info("Login ID: \(loginId ?? "nil", privacy: .public)")
        logger.info("Session URL: \(sessionURLString, privacy: .public)")

        let tokenProvider: TokenProvider
        let appGroup = Self.appGroupId

        // Read login-level credential from keychain
        let credData = Self.readKeychainData(
            service: loginKeychainService, account: keychainAccount, accessGroup: appGroup)
        logger.info("Keychain data for login \(keychainAccount, privacy: .public): \(credData != nil ? "\(credData!.count) bytes" : "nil", privacy: .public)")

        if authType == "oauth", let credData = credData {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            if let credential = try? decoder.decode(OAuthCredential.self, from: credData) {
                let kcService = loginKeychainService
                let kcAccount = keychainAccount
                tokenProvider = OAuthTokenProvider(credential: credential,
                    onTokenRefreshed: { updated in
                        // Persist refreshed tokens back to login-level keychain
                        let encoder = JSONEncoder()
                        encoder.dateEncodingStrategy = .iso8601
                        if let data = try? encoder.encode(updated),
                           let str = String(data: data, encoding: .utf8) {
                            try? KeychainTokenProvider.storeToken(
                                str, service: kcService,
                                account: kcAccount, accessGroup: appGroup)
                        }
                    },
                    reloadCredential: {
                        // Reload from keychain — another process or app may have refreshed
                        guard let data = FileProviderExtension.readKeychainData(
                            service: kcService, account: kcAccount, accessGroup: appGroup)
                        else { return nil }
                        let decoder = JSONDecoder()
                        decoder.dateDecodingStrategy = .iso8601
                        return try? decoder.decode(OAuthCredential.self, from: data)
                    }
                )
            } else {
                logger.error("Failed to decode OAuth credential for \(keychainAccount, privacy: .public)")
                tokenProvider = KeychainTokenProvider(
                    service: loginKeychainService, account: keychainAccount, accessGroup: appGroup)
            }
        } else {
            tokenProvider = KeychainTokenProvider(
                service: loginKeychainService, account: keychainAccount, accessGroup: appGroup)
        }

        // Initialize components — with traffic logging to shared container
        let logFile = containerURL.appendingPathComponent("jmap-traffic.log")
        self.sessionManager = SessionManager(sessionURL: sessionURL, tokenProvider: tokenProvider)
        self.client = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider,
                                  requestWillSend: { url, body in
            TrafficLog.shared.log("→ POST \(url.absoluteString)\n\(TrafficLog.formatBody(body))")
        }, responseDidReceive: { url, status, body in
            TrafficLog.shared.log("← \(status) \(url.absoluteString)\n\(TrafficLog.formatBody(body))")
        })
        self.database = NodeDatabase(containerURL: containerURL, accountId: accountId)
        self.activityTracker = ActivityTracker(containerURL: containerURL)
        self.statusWriter = ExtensionStatusWriter(containerURL: containerURL, accountId: accountId)

        // Set up push watcher
        self.pushWatcher = PushWatcher(sessionManager: sessionManager, tokenProvider: tokenProvider, accountId: accountId)

        statusWriter.setState(.initializing)
        Task {
            await initializeFromDatabase()
            statusWriter.setIdle()
            await startPushWatcher()
        }
    }

    public func invalidate() {
        #if canImport(os)
        logger.info("Extension invalidating — suppressing server deletes")
        #endif
        isDomainBeingRemoved = true
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

                let activityId = "dl:\(accountId!):\(nodeId)"
                await activityTracker.start(
                    id: activityId, accountId: accountId, fileName: entry.name,
                    action: .download, fileSize: entry.size)

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
                        let pct = Double(downloaded) / Double(total)
                        progress.completedUnitCount = Int64(pct * 100)
                        Task { await self.activityTracker.updateProgress(id: activityId, progress: pct) }
                    }
                }

                progress.completedUnitCount = 100
                await activityTracker.complete(id: activityId)

                let item = FileProviderItem(
                    nodeId: nodeId, entry: entry,
                    homeNodeId: homeNodeId, trashNodeId: trashNodeId)
                completionHandler(tempURL, item, nil)
            } catch {
                let activityId = "dl:\(accountId!):\(itemIdentifier.rawValue)"
                await activityTracker.fail(id: activityId, error: error.localizedDescription)
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
                    let folderName = desanitizeFilename(itemTemplate.filename)
                    let node = try await client.createNode(
                        accountId: accountId,
                        parentId: parentId,
                        name: folderName
                    )
                    let entry = NodeCacheEntry(
                        parentId: node.parentId ?? parentId,
                        name: node.name ?? folderName,
                        blobId: nil,
                        size: 0,
                        modified: node.modified,
                        isFolder: true,
                        type: nil,
                        myRights: node.myRights
                    )
                    await database.upsert(nodeId: node.id, entry: entry)
                    try await database.save()

                    let item = FileProviderItem(
                        nodeId: node.id, entry: entry,
                        homeNodeId: homeNodeId, trashNodeId: trashNodeId)
                    completionHandler(item, [], false, nil)
                } else {
                    // Create file — upload blob then create node
                    guard let contentURL = url else {
                        throw JmapError.uploadFailed("No content URL provided")
                    }

                    let fileName = desanitizeFilename(itemTemplate.filename)
                    let fileSize = (try? FileManager.default.attributesOfItem(
                        atPath: contentURL.path)[.size] as? Int) ?? 0
                    let activityId = "ul:\(accountId!):\(fileName)"
                    await activityTracker.start(
                        id: activityId, accountId: accountId, fileName: fileName,
                        action: .upload, fileSize: fileSize)

                    let contentType = itemTemplate.contentType?.preferredMIMEType ?? "application/octet-stream"
                    let blob = try await client.uploadBlobChunked(
                        accountId: accountId,
                        fileURL: contentURL,
                        contentType: contentType
                    ) { uploaded, total in
                        if total > 0 {
                            let pct = Double(uploaded) / Double(total)
                            progress.completedUnitCount = Int64(pct * 80)
                            Task { await self.activityTracker.updateProgress(id: activityId, progress: pct * 0.8) }
                        }
                    }

                    await activityTracker.updateProgress(id: activityId, progress: 0.9)
                    let node = try await client.createNode(
                        accountId: accountId,
                        parentId: parentId,
                        name: fileName,
                        blobId: blob.blobId,
                        type: contentType,
                        created: itemTemplate.creationDate ?? nil,
                        modified: itemTemplate.contentModificationDate ?? nil,
                        onExists: "replace"
                    )

                    progress.completedUnitCount = 100

                    // Build item from our known values + server's new ID
                    // (server response is partial — missing name, parentId, blobId)
                    let entry = NodeCacheEntry(
                        parentId: node.parentId ?? parentId,
                        name: node.name ?? fileName,
                        blobId: node.blobId ?? blob.blobId,
                        size: node.size ?? ((try? FileManager.default.attributesOfItem(
                            atPath: contentURL.path)[.size] as? Int) ?? 0),
                        modified: node.modified ?? (itemTemplate.contentModificationDate ?? nil),
                        isFolder: false,
                        type: node.type ?? contentType,
                        myRights: node.myRights
                    )
                    await database.upsert(nodeId: node.id, entry: entry)
                    try await database.save()

                    await activityTracker.complete(id: activityId)

                    let item = FileProviderItem(
                        nodeId: node.id, entry: entry,
                        homeNodeId: homeNodeId, trashNodeId: trashNodeId)
                    completionHandler(item, [], false, nil)
                }
            } catch {
                // Mark any in-flight upload as failed
                let failId = "ul:\(accountId!):\(desanitizeFilename(itemTemplate.filename))"
                await activityTracker.fail(id: failId, error: error.localizedDescription)
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

                // Handle content change — v10: update blobId directly (node ID stays the same)
                if changedFields.contains(.contents), let contentURL = newContents {
                    let contentType = item.contentType?.preferredMIMEType ?? "application/octet-stream"
                    let parentId = resolveNodeId(item.parentItemIdentifier)
                    let fileName = desanitizeFilename(item.filename)
                    let fileSize = (try? FileManager.default.attributesOfItem(
                        atPath: contentURL.path)[.size] as? Int) ?? 0
                    let activityId = "ul:\(accountId!):\(fileName)"
                    await activityTracker.start(
                        id: activityId, accountId: accountId, fileName: fileName,
                        action: .upload, fileSize: fileSize)

                    let blob = try await client.uploadBlobChunked(
                        accountId: accountId,
                        fileURL: contentURL,
                        contentType: contentType
                    ) { uploaded, total in
                        if total > 0 {
                            let pct = Double(uploaded) / Double(total)
                            Task { await self.activityTracker.updateProgress(id: activityId, progress: pct * 0.8) }
                        }
                    }
                    progress.completedUnitCount = 60

                    // v10: blobId is mutable — update directly, node ID stays the same
                    try await client.updateNodeContent(
                        accountId: accountId,
                        nodeId: nodeId,
                        blobId: blob.blobId,
                        type: contentType,
                        modified: item.contentModificationDate ?? nil
                    )
                    progress.completedUnitCount = 80

                    // Update database with new blob info (node ID unchanged)
                    if var entry = await database.entry(for: nodeId) {
                        let updatedEntry = NodeCacheEntry(
                            parentId: entry.parentId,
                            name: entry.name,
                            blobId: blob.blobId,
                            size: blob.size,
                            modified: item.contentModificationDate ?? nil,
                            isFolder: false,
                            type: contentType,
                            myRights: entry.myRights
                        )
                        await database.upsert(nodeId: nodeId, entry: updatedEntry)
                    }
                    // currentNodeId stays as nodeId — no ID change
                    await activityTracker.complete(id: activityId)
                }

                // Handle rename
                if changedFields.contains(.filename) {
                    try await client.updateNode(
                        accountId: accountId,
                        nodeId: currentNodeId,
                        name: desanitizeFilename(item.filename),
                        modified: Date()
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
                            modified: Date(),
                            onExists: "rename"
                        )
                    } else {
                        try await client.updateNode(
                            accountId: accountId,
                            nodeId: currentNodeId,
                            parentId: newParentId,
                            modified: Date()
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
                // CRITICAL: When the system is removing the domain (e.g. user removed
                // account), it sends delete requests for every item. We must NOT forward
                // those to the server or we'll destroy all the user's files.
                if isDomainBeingRemoved {
                    completionHandler(nil)
                    return
                }

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
            accountName: accountName,
            homeNodeId: homeNodeId ?? "",
            trashNodeId: trashNodeId,
            activityTracker: activityTracker,
            statusWriter: statusWriter
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
                      let blobId = entry.blobId,
                      let type = entry.type,
                      type.hasPrefix("image/")
                else {
                    perThumbnailCompletionHandler(identifier, nil, nil)
                    continue
                }

                // Check if server supports Blob/convert (blob2)
                let session = try? await sessionManager.session()
                guard let session = session,
                      session.hasBlob2(accountId: accountId)
                else {
                    perThumbnailCompletionHandler(identifier, nil, nil)
                    continue
                }

                do {
                    let thumbnailBlobId = try await client.convertImage(
                        accountId: accountId,
                        blobId: blobId,
                        width: Int(requestedSize.width),
                        height: Int(requestedSize.height),
                        mimeType: "image/jpeg"
                    )

                    // Download the thumbnail blob
                    guard let containerURL = FileManager.default.containerURL(
                        forSecurityApplicationGroupIdentifier: Self.appGroupId)
                    else {
                        perThumbnailCompletionHandler(identifier, nil, nil)
                        continue
                    }
                    let tempDir = containerURL.appendingPathComponent("tmp", isDirectory: true)
                    try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)

                    let tempURL = try await client.downloadBlob(
                        accountId: accountId,
                        blobId: thumbnailBlobId,
                        name: "thumbnail.jpg",
                        type: "image/jpeg",
                        destinationDir: tempDir
                    )
                    let thumbnailData = try Data(contentsOf: tempURL)
                    try? FileManager.default.removeItem(at: tempURL)

                    perThumbnailCompletionHandler(identifier, thumbnailData, nil)
                } catch {
                    perThumbnailCompletionHandler(identifier, nil, nil)
                }
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
            statusWriter.setSyncing()
            let sessionActivityId = "init:\(accountId!):session"
            await activityTracker.start(
                id: sessionActivityId, accountId: accountId,
                fileName: "Reading session data", action: .sync)
            do {
                let home = try await client.findHomeNode(accountId: accountId)
                homeNodeId = home.id
                await database.setHomeNodeId(home.id)

                let trash = try await client.findTrashNode(accountId: accountId)
                trashNodeId = trash?.id
                await database.setTrashNodeId(trash?.id)

                try await database.save()
                await activityTracker.complete(id: sessionActivityId)
            } catch let error as JmapError {
                await activityTracker.fail(id: sessionActivityId, error: error.localizedDescription)
                switch error {
                case .unauthorized, .forbidden:
                    statusWriter.setError("Authentication failed")
                default:
                    statusWriter.setError(error.localizedDescription ?? "Unknown error")
                }
                #if canImport(os)
                logger.error("Failed to discover home/trash nodes: \(error.localizedDescription)")
                #endif
            } catch {
                await activityTracker.fail(id: sessionActivityId, error: error.localizedDescription)
                statusWriter.setState(.offline)
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
            return jmapError.fileProviderError
        }
        if (error as NSError).domain == NSURLErrorDomain {
            return NSFileProviderError(.serverUnreachable)
        }
        return error
    }
}

// MARK: - Traffic Log

/// Simple file-based traffic logger for the FileProvider extension.
/// Writes to the shared App Group container so the app or `tail -f` can read it.
final class TrafficLog: Sendable {
    static let shared = TrafficLog()

    private let logger = Logger(subsystem: "com.fastmail.files", category: "JMAP")

    private init() {}

    func log(_ message: String) {
        logger.info("\(message, privacy: .public)")
    }

    /// Format a JMAP body for logging, extracting method calls/responses.
    static func formatBody(_ data: Data) -> String {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return String(data: data, encoding: .utf8) ?? "(\(data.count) bytes)"
        }

        var opts: JSONSerialization.WritingOptions = [.prettyPrinted, .sortedKeys]
        if #available(macOS 13, *) {
            opts.insert(.withoutEscapingSlashes)
        }

        // Extract method calls or responses
        let key = obj["methodCalls"] != nil ? "methodCalls" : (obj["methodResponses"] != nil ? "methodResponses" : nil)
        if let key = key, let methods = obj[key] as? [[Any]] {
            var lines: [String] = []
            for method in methods {
                guard method.count >= 3,
                      let name = method[0] as? String,
                      let callId = method[2] as? String else { continue }
                if let argsData = try? JSONSerialization.data(withJSONObject: method[1], options: opts),
                   var argsStr = String(data: argsData, encoding: .utf8) {
                    argsStr = argsStr.replacingOccurrences(of: "\\[\\s*\\]", with: "[]", options: .regularExpression)
                    argsStr = argsStr.replacingOccurrences(of: "\\{\\s*\\}", with: "{}", options: .regularExpression)
                    lines.append("  \(name) #\(callId)\n    \(argsStr.replacingOccurrences(of: "\n", with: "\n    "))")
                } else {
                    lines.append("  \(name) #\(callId)")
                }
            }
            return lines.joined(separator: "\n")
        }

        // Non-JMAP — compact
        if let data = try? JSONSerialization.data(withJSONObject: obj, options: opts),
           let str = String(data: data, encoding: .utf8) {
            let maxLen = 1000
            return str.count > maxLen ? String(str.prefix(maxLen)) + "..." : str
        }
        return "(\(data.count) bytes)"
    }
}
