import FileProvider
import UniformTypeIdentifiers
import JmapClient
#if canImport(os)
import os
#endif

/// Resolved home and trash node IDs — available once the extension has contacted the server.
struct SpecialNodes {
    let homeId: String
    let trashId: String?
}

/// The main FileProvider extension class.
///
/// Implements `NSFileProviderReplicatedExtension` — the system calls this
/// to enumerate items, download content, and propagate local changes.
///
/// Lifecycle: the system launches this extension on demand and may terminate
/// it at any time. All state is persisted to the App Group shared container.
public final class FileProviderExtension: NSObject, NSFileProviderReplicatedExtension,
    NSFileProviderEnumerating, NSFileProviderThumbnailing, PushWatcherDelegate
{
    private let domain: NSFileProviderDomain
    private let database: NodeDatabase
    private let client: JmapClient
    private let sessionManager: SessionManager
    private let pushWatcher: PushWatcher
    private let syncEngine: SyncEngine
    private let accountId: String
    private let accountName: String
    private let activityTracker: ActivityTracker
    private let statusWriter: ExtensionStatusWriter
    private let bandwidthPolicy = BandwidthPolicy()
    /// Guards against concurrent duplicate uploads for the same item identifier.
    /// fileproviderd may call createItem/modifyItem concurrently for the same item on retry.
    /// Uses NSLock + Dictionary (not actor) for synchronous access during init.
    private let inFlightLock = NSLock()
    private var inFlightUploads: [String: Task<Void, Never>] = [:]
    /// Monotonically increasing per-key counter. Lets a completing task detect that a
    /// newer task has already superseded it, so it skips deregistration.
    private var inFlightGenerations: [String: Int] = [:]
    /// Resolved once on first use; all sync methods await this before touching the server.
    private let specialNodes: Task<SpecialNodes, Error>
    /// Guards `_isDomainBeingRemoved` against concurrent reads from deleteItem tasks
    /// and writes from invalidate() which can be called on any thread.
    private let domainRemovalLock = NSLock()
    private var _isDomainBeingRemoved = false
    private var isDomainBeingRemoved: Bool {
        get { domainRemovalLock.lock(); defer { domainRemovalLock.unlock() }; return _isDomainBeingRemoved }
        set { domainRemovalLock.lock(); _isDomainBeingRemoved = newValue; domainRemovalLock.unlock() }
    }

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
        // Phase 1: initialize all stored properties before super.init().
        // No `self` access allowed here — use local variables for everything.

        self.domain = domain
        self.accountId = domain.identifier.rawValue
        self.accountName = domain.displayName

        let appGroup = Self.appGroupId
        let acctId = domain.identifier.rawValue

        // App Group container — fall back to temp dir so all `let` properties are always valid.
        let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: appGroup)
        let effectiveContainerURL = containerURL ?? FileManager.default.temporaryDirectory

        // Load config from shared UserDefaults
        let defaults = UserDefaults(suiteName: appGroup)
        let sessionURLString = defaults?.string(forKey: "sessionURL-\(acctId)")
            ?? "https://api.fastmail.com/jmap/session"
        let sessionURL = URL(string: sessionURLString)
            ?? URL(string: "https://api.fastmail.com/jmap/session")!

        // Look up which login owns this account
        let loginId = defaults?.string(forKey: "loginForAccount-\(acctId)")
        let authType = defaults?.string(forKey: "authType-\(acctId)")
        let loginKeychainService = "com.fastmail.files.login"
        let keychainAccount = loginId ?? acctId

        let credData = FileProviderExtension.readKeychainData(
            service: loginKeychainService, account: keychainAccount, accessGroup: appGroup)

        let tokenProvider: TokenProvider
        if authType == "oauth", let credData = credData,
           let credential = {
               let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601
               return try? d.decode(OAuthCredential.self, from: credData)
           }()
        {
            let kcService = loginKeychainService
            let kcAccount = keychainAccount
            // Lock file name: sanitise the login identifier so it's a valid filename.
            // All extension instances for the same login share this lock, ensuring
            // only one process hits the token endpoint at a time.
            let safeName = kcAccount.unicodeScalars.map {
                CharacterSet.alphanumerics.contains($0) ? String($0) : "_"
            }.joined()
            let lockFilePath = effectiveContainerURL
                .appendingPathComponent("token-refresh-\(safeName).lock").path
            tokenProvider = OAuthTokenProvider(credential: credential,
                lockFilePath: lockFilePath,
                onTokenRefreshed: { updated in
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
                    guard let data = FileProviderExtension.readKeychainData(
                        service: kcService, account: kcAccount, accessGroup: appGroup)
                    else { return nil }
                    let d = JSONDecoder(); d.dateDecodingStrategy = .iso8601
                    return try? d.decode(OAuthCredential.self, from: data)
                }
            )
        } else {
            tokenProvider = KeychainTokenProvider(
                service: loginKeychainService, account: keychainAccount, accessGroup: appGroup)
        }

        // Build all infrastructure synchronously (no self needed).
        let sessionCacheURL = effectiveContainerURL.appendingPathComponent("session-\(acctId).json")
        let _sessionManager = SessionManager(sessionURL: sessionURL, tokenProvider: tokenProvider,
                                             diskCacheURL: sessionCacheURL)
        TrafficLog.shared.configure(containerURL: effectiveContainerURL)

        // Use App Group container tmp/ for chunk files so orphans survive a process kill.
        let tmpDir = effectiveContainerURL.appendingPathComponent("tmp", isDirectory: true)
        try? FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)

        let _database = NodeDatabase(containerURL: effectiveContainerURL, accountId: acctId)

        // Sweep stale rows on every startup to prevent unbounded table growth.
        // NodeDatabase is an actor; sweep in a detached Task so init stays synchronous.
        Task { await _database.sweepOldUploadSessions() }
        Task { await _database.sweepSha1Cache() }

        // Background URLSession for chunk uploads — tasks run in nsurlsessiond and survive
        // extension process kills. The session identifier must be stable across launches so
        // reconnection works; use the account ID as the discriminator.
        let bgUploader = BackgroundUploader(
            identifier: "com.fastmail.files.upload.\(acctId)",
            onOrphan: { taskId, data, response in
                // A chunk task that completed after the extension was killed.
                // Parse the blobId and persist it so the next retry can skip re-uploading.
                guard response.statusCode == 200,
                      let blobResponse = try? JSONDecoder().decode(BlobUploadResponse.self, from: data)
                else { return }
                // onOrphan is synchronous; hop to the actor via a Task.
                Task { await _database.completeUploadChunkByTaskId(taskId, blobId: blobResponse.blobId, sha1: "") }
                // sha1 is empty here — it was already incorporated into the overall hash before
                // the task started. The DB row's sha1 field is only used for delta dedup, which
                // can tolerate a missing value (it'll fall back to re-uploading that chunk).
            }
        )

        let chunkStore = UploadChunkStore(
            completedChunks: { uploadId in await _database.completedUploadChunks(uploadId: uploadId) },
            startChunk: { uploadId, index, taskId in await _database.startUploadChunk(uploadId: uploadId, index: index, taskId: taskId) },
            completeChunk: { uploadId, index, blobId, sha1 in await _database.completeUploadChunk(uploadId: uploadId, index: index, blobId: blobId, sha1: sha1) },
            clearUpload: { uploadId in await _database.clearUploadSession(uploadId: uploadId) },
            cachedBlobId: { sha1 in await _database.cachedBlobId(forSha1: sha1) },
            cacheChunkSha1: { sha1, blobId in await _database.cacheChunkSha1(sha1, blobId: blobId) }
        )

        let _client = JmapClient(sessionManager: _sessionManager, tokenProvider: tokenProvider,
                                 temporaryDirectory: tmpDir,
                                 backgroundUploader: bgUploader,
                                 chunkStore: chunkStore)
        let _activityTracker = ActivityTracker(containerURL: effectiveContainerURL)
        let _statusWriter = ExtensionStatusWriter(containerURL: effectiveContainerURL, accountId: acctId)
        let _syncEngine = SyncEngine(client: _client, database: _database, accountId: acctId)
        let _pushWatcher = PushWatcher(
            sessionManager: _sessionManager, tokenProvider: tokenProvider, accountId: acctId)

        // Assign stored properties.
        self.sessionManager = _sessionManager
        self.client = _client
        self.database = _database
        self.activityTracker = _activityTracker
        self.statusWriter = _statusWriter
        self.syncEngine = _syncEngine
        self.pushWatcher = _pushWatcher

        _statusWriter.setState(.initializing)

        // specialNodes is the single async init barrier.
        // All sync methods await this; the result is cached so the network
        // round-trip only happens once regardless of concurrency.
        if containerURL == nil {
            // Misconfigured entitlements — extension cannot function.
            self.specialNodes = Task { throw JmapError.serverError("container", "App Group container unavailable") }
        } else {
            self.specialNodes = Task {
                // Warm start: home node already known from previous session.
                if let homeId = await _database.homeNodeId {
                    return SpecialNodes(homeId: homeId, trashId: await _database.trashNodeId)
                }
                // Cold start: resolve via JMAP session.
                _statusWriter.setSyncing()
                let actId = "init:\(acctId):session"
                await _activityTracker.start(
                    id: actId, accountId: acctId,
                    fileName: "Reading session data", action: .sync)
                do {
                    let (homeId, trashId) = try await _syncEngine.resolveSpecialNodes()
                    await _database.setHomeNodeId(homeId)
                    await _database.setTrashNodeId(trashId)
                    try await _database.save()
                    await _activityTracker.complete(id: actId)
                    _statusWriter.setIdle()
                    return SpecialNodes(homeId: homeId, trashId: trashId)
                } catch let err as JmapError {
                    await _activityTracker.fail(id: actId, error: err.localizedDescription)
                    switch err {
                    case .unauthorized, .forbidden:
                        _statusWriter.setError("Authentication failed")
                    default:
                        _statusWriter.setError(err.localizedDescription ?? "Unknown error")
                    }
                    throw err
                } catch {
                    await _activityTracker.fail(id: actId, error: error.localizedDescription)
                    _statusWriter.setState(.offline)
                    throw error
                }
            }
        }

        super.init()

        // Phase 2: background startup (may reference self).
        #if canImport(os)
        logger.info("Extension init for account: \(acctId, privacy: .public)")
        logger.info("Container URL: \(effectiveContainerURL.path, privacy: .public)")
        #endif
        Task {
            await activityTracker.setStatusWriter(statusWriter)
            // Convert any stale .active entries from a previous killed process to .pending.
            // The system will retry those operations; show them as "queued" not "in progress".
            await activityTracker.recoverStaledActives()
            await bandwidthPolicy.start()
            _ = try? await specialNodes.value  // errors handled inside the task above
            // Pre-warm the session cache so PushWatcher's first sessionManager.session()
            // call is a cache hit rather than a second HTTP round-trip.
            _ = try? await sessionManager.session()
            await startPushWatcher()
            // Tell the system we're ready so it calls enumerateItems immediately,
            // rather than waiting for its own (potentially serialised) schedule.
            NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
        }
    }

    public func invalidate() {
        #if canImport(os)
        logger.info("Extension invalidating — suppressing server deletes")
        #endif
        isDomainBeingRemoved = true
        Task {
            await bandwidthPolicy.stop()
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
                let nodes = try await specialNodes.value
                let item = try await lookupItem(identifier, nodes: nodes)
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
                let nodes = try await specialNodes.value
                let nodeId = itemIdentifier.rawValue
                guard let entry = await database.entry(for: nodeId) else {
                    throw JmapError.notFound(nodeId)
                }

                guard let blobId = entry.blobId else {
                    throw JmapError.serverError("isFolder", "Cannot fetch contents of a folder")
                }

                // Interactive file-open (user explicitly opened the file) proceeds on any
                // non-offline connection. Background hydration (system pre-fetch) only runs
                // on unrestricted (WiFi/ethernet) connections.
                // On macOS < 13 / iOS < 16, isFileViewerRequest is unavailable; treat all
                // fetchContents as interactive so users are never blocked from opening files.
                let isInteractive: Bool
                if #available(macOS 13.0, iOS 16.0, *) {
                    isInteractive = request.isFileViewerRequest
                } else {
                    isInteractive = true
                }
                let allowed = isInteractive
                    ? await bandwidthPolicy.allowsInteractiveDownload
                    : await bandwidthPolicy.allowsBackgroundDownload
                if !allowed {
                    throw JmapError.rateLimited  // retry when connection improves
                }

                // Download to temp directory in the App Group container
                guard let containerURL = FileManager.default.containerURL(
                    forSecurityApplicationGroupIdentifier: Self.appGroupId)
                else {
                    throw JmapError.invalidResponse
                }

                let activityId = "dl:\(accountId):\(nodeId)"
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
                    homeNodeId: nodes.homeId, trashNodeId: nodes.trashId,
                    isPinned: await database.isPinned(id: nodeId))
                completionHandler(tempURL, item, nil)
            } catch {
                let activityId = "dl:\(accountId):\(itemIdentifier.rawValue)"
                await activityTracker.fail(id: activityId, error: error.localizedDescription)
                #if canImport(os)
                logger.error("fetchContents [\(self.accountId, privacy: .public)] \(itemIdentifier.rawValue, privacy: .public) failed: \(error, privacy: .public)")
                #endif
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

        // Generate a stable upload ID from the item identity + file content fingerprint.
        // Stable across retries (same item + same file = same ID), changes when content changes.
        let createUploadId: String = {
            let fileSize = (try? FileManager.default.attributesOfItem(
                atPath: (url ?? URL(fileURLWithPath: "")).path)[.size] as? Int64) ?? 0
            let modMs = (try? FileManager.default.attributesOfItem(
                atPath: (url ?? URL(fileURLWithPath: "")).path)[.modificationDate] as? Date)?
                .timeIntervalSince1970 ?? 0
            return "create:\(accountId):\(itemTemplate.itemIdentifier.rawValue):\(fileSize):\(Int64(modMs * 1000))"
        }()

        let dedupeKey = itemTemplate.itemIdentifier.rawValue
        let inFlightGen = registerInFlight(key: dedupeKey)

        let uploadTask = Task {
            defer { deregisterInFlight(key: dedupeKey, generation: inFlightGen) }
            do {
                let nodes = try await specialNodes.value

                // Creating inside .trashContainer when no trash node exists is impossible.
                if itemTemplate.parentItemIdentifier == .trashContainer && nodes.trashId == nil {
                    completionHandler(nil, [], false, NSFileProviderError(.noSuchItem) as NSError)
                    return
                }

                let parentId = resolveNodeId(itemTemplate.parentItemIdentifier, nodes: nodes)

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
                        homeNodeId: nodes.homeId, trashNodeId: nodes.trashId)
                    completionHandler(item, [], false, nil)
                } else {
                    // Create file — upload blob then create node
                    guard let contentURL = url else {
                        throw JmapError.uploadFailed("No content URL provided")
                    }

                    let fileName = desanitizeFilename(itemTemplate.filename)
                    let fileSize = (try? FileManager.default.attributesOfItem(
                        atPath: contentURL.path)[.size] as? Int) ?? 0

                    // Register as pending immediately — visible to the user even if
                    // the upload is blocked by bandwidth policy or hasn't started yet.
                    let activityId = "ul:\(accountId):\(fileName)"
                    await activityTracker.start(
                        id: activityId, accountId: accountId, fileName: fileName,
                        action: .upload, fileSize: fileSize, status: .pending)

                    guard await bandwidthPolicy.allowsUpload(bytes: fileSize) else {
                        await activityTracker.fail(id: activityId, error: "Upload deferred (bandwidth policy)")
                        throw JmapError.rateLimited
                    }

                    let contentType = itemTemplate.contentType?.preferredMIMEType ?? "application/octet-stream"

                    // Detect retry: if a node with this name already exists in our DB, the previous
                    // createItem may have succeeded but died before calling completionHandler.
                    // If the server still holds that blob with the same content (verified via SHA1),
                    // skip the re-upload entirely — the content hasn't changed.
                    let existingBlobId: String? = await {
                        guard let existingNodeId = await database.nodeId(forName: fileName, parentId: parentId) else { return nil }
                        return await database.entry(for: existingNodeId)?.blobId
                    }()
                    let reusedBlobId: String? = await {
                        guard let candidateBlobId = existingBlobId,
                              let localSha1 = try? sha1OfFile(contentURL) else { return nil }
                        return (try? await client.blobHasDigest(
                            accountId: accountId, blobId: candidateBlobId, sha1: localSha1)) == true
                            ? candidateBlobId : nil
                    }()

                    let blobId: String
                    if let reused = reusedBlobId {
                        blobId = reused
                        progress.completedUnitCount = 80
                    } else {
                        let blob = try await client.uploadBlobChunked(
                            accountId: accountId,
                            fileURL: contentURL,
                            contentType: contentType,
                            uploadId: createUploadId
                        ) { uploaded, total in
                            if total > 0 {
                                let pct = Double(uploaded) / Double(total)
                                progress.completedUnitCount = Int64(pct * 80)
                                Task { await self.activityTracker.updateProgress(id: activityId, progress: pct * 0.8) }
                            }
                        }
                        blobId = blob.blobId
                    }

                    await activityTracker.updateProgress(id: activityId, progress: 0.9)
                    let node = try await client.createNode(
                        accountId: accountId,
                        parentId: parentId,
                        name: fileName,
                        blobId: blobId,
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
                        blobId: node.blobId ?? blobId,
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
                    await database.resetUploadFailure(itemIdentifier: itemTemplate.itemIdentifier.rawValue)
                    statusWriter.setBlockedUploadCount(await database.blockedUploadCount)

                    let item = FileProviderItem(
                        nodeId: node.id, entry: entry,
                        homeNodeId: nodes.homeId, trashNodeId: nodes.trashId)
                    completionHandler(item, [], false, nil)
                }
            } catch {
                let failId = "ul:\(accountId):\(desanitizeFilename(itemTemplate.filename))"
                await activityTracker.fail(id: failId, error: error.localizedDescription)
                let mapped: Error
                if isPermanentError(error) {
                    // Permanent errors (e.g. alreadyExists → filenameCollision) must not be
                    // swapped for cannotSynchronize — the system needs the specific code.
                    mapped = mapError(error)
                } else {
                    let failCount = await database.incrementUploadFailure(
                        itemIdentifier: itemTemplate.itemIdentifier.rawValue,
                        error: error.localizedDescription)
                    statusWriter.setBlockedUploadCount(await database.blockedUploadCount)
                    mapped = failCount >= NodeDatabase.uploadFailureThreshold
                        ? NSFileProviderError(.cannotSynchronize) as NSError
                        : mapError(error)
                }
                completionHandler(nil, [], false, mapped)
            }
        }

        setInFlight(key: dedupeKey, generation: inFlightGen, task: uploadTask)
        progress.cancellationHandler = { uploadTask.cancel() }
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

        let modifyDedupeKey = item.itemIdentifier.rawValue
        let modifyInFlightGen = registerInFlight(key: modifyDedupeKey)

        let modifyTask = Task {
            defer { deregisterInFlight(key: modifyDedupeKey, generation: modifyInFlightGen) }
            do {
                let nodes = try await specialNodes.value
                let nodeId = item.itemIdentifier.rawValue
                var currentNodeId = nodeId

                // Handle content change — v10: update blobId directly (node ID stays the same)
                if changedFields.contains(.contents), let contentURL = newContents {
                    let contentType = item.contentType?.preferredMIMEType ?? "application/octet-stream"
                    let fileName = desanitizeFilename(item.filename)
                    let fileSize = (try? FileManager.default.attributesOfItem(
                        atPath: contentURL.path)[.size] as? Int) ?? 0

                    let activityId = "ul:\(accountId):\(fileName)"
                    await activityTracker.start(
                        id: activityId, accountId: accountId, fileName: fileName,
                        action: .upload, fileSize: fileSize, status: .pending)

                    guard await bandwidthPolicy.allowsUpload(bytes: fileSize) else {
                        await activityTracker.fail(id: activityId, error: "Upload deferred (bandwidth policy)")
                        throw JmapError.rateLimited
                    }

                    let currentBlobId = await database.entry(for: nodeId)?.blobId
                    let modUploadId: String = {
                        let sz = (try? FileManager.default.attributesOfItem(atPath: contentURL.path)[.size] as? Int64) ?? 0
                        let ms = (try? FileManager.default.attributesOfItem(atPath: contentURL.path)[.modificationDate] as? Date)?
                            .timeIntervalSince1970 ?? 0
                        return "modify:\(accountId):\(nodeId):\(sz):\(Int64(ms * 1000))"
                    }()
                    let blob = try await client.uploadBlobDelta(
                        accountId: accountId,
                        fileURL: contentURL,
                        contentType: contentType,
                        oldBlobId: currentBlobId,
                        uploadId: modUploadId
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
                    if let entry = await database.entry(for: nodeId) {
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
                    let newParentIdentifier = item.parentItemIdentifier
                    if newParentIdentifier == .trashContainer && nodes.trashId == nil {
                        // Moving to trash but account has no trash node — permanently delete.
                        // Signal success with no resulting item (same as deleteItem does).
                        try await client.destroyNode(accountId: accountId, nodeId: currentNodeId,
                                                     removeChildren: false)
                        await database.remove(nodeId: currentNodeId)
                        try await database.save()
                        completionHandler(nil, [], false, nil)
                        return
                    }
                    let newParentId = resolveNodeId(newParentIdentifier, nodes: nodes)
                    if newParentId == nodes.trashId {
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

                // Handle pin/unpin — local-only, not sent to server.
                // Pinned items get contentPolicy .downloadEagerlyAndKeepDownloaded so the
                // framework keeps them hydrated. Eager download is gated on bandwidth:
                // it runs only on unrestricted connections so it never competes with an
                // interactive file open on cellular / Low Data Mode.
                do {
                    let wantsPinned = item.contentPolicy == .downloadEagerlyAndKeepDownloaded
                    await database.setPinned(id: currentNodeId, pinned: wantsPinned)
                    let allowsDownload = await bandwidthPolicy.allowsBackgroundDownload
                    if wantsPinned && allowsDownload {
                        // Ask the framework to hydrate now; it will schedule behind any
                        // in-flight interactive downloads automatically.
                        let identifier = NSFileProviderItemIdentifier(currentNodeId)
                        NSFileProviderManager(for: domain)?
                            .requestDownloadForItem(withIdentifier: identifier) { _ in }
                    }
                }

                progress.completedUnitCount = 100

                try await database.save()

                // Return the updated item
                guard let entry = await database.entry(for: currentNodeId) else {
                    throw JmapError.notFound(currentNodeId)
                }
                let isPinned = await database.isPinned(id: currentNodeId)
                let resultItem = FileProviderItem(
                    nodeId: currentNodeId, entry: entry,
                    homeNodeId: nodes.homeId, trashNodeId: nodes.trashId,
                    isPinned: isPinned)
                await database.resetUploadFailure(itemIdentifier: item.itemIdentifier.rawValue)
                statusWriter.setBlockedUploadCount(await database.blockedUploadCount)
                completionHandler(resultItem, [], false, nil)
            } catch {
                let mapped: Error
                if isPermanentError(error) {
                    mapped = mapError(error)
                } else {
                    let failCount = await database.incrementUploadFailure(
                        itemIdentifier: item.itemIdentifier.rawValue,
                        error: error.localizedDescription)
                    statusWriter.setBlockedUploadCount(await database.blockedUploadCount)
                    mapped = failCount >= NodeDatabase.uploadFailureThreshold
                        ? NSFileProviderError(.cannotSynchronize) as NSError
                        : mapError(error)
                }
                completionHandler(nil, [], false, mapped)
            }
        }

        setInFlight(key: modifyDedupeKey, generation: modifyInFlightGen, task: modifyTask)
        progress.cancellationHandler = { modifyTask.cancel() }
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

                let nodes = try await specialNodes.value

                // Re-check after the await: invalidate() runs on an arbitrary thread and
                // may have set isDomainBeingRemoved while we were suspended above.
                if isDomainBeingRemoved {
                    completionHandler(nil)
                    return
                }

                let nodeId = identifier.rawValue

                let fileName = (await database.entry(for: nodeId))?.name ?? nodeId
                let activityId = "del:\(accountId):\(nodeId)"
                await activityTracker.start(
                    id: activityId, accountId: accountId, fileName: fileName,
                    action: .delete)

                // Final check immediately before the destructive server call.
                guard !isDomainBeingRemoved else {
                    completionHandler(nil)
                    return
                }

                do {
                    if let trashId = nodes.trashId {
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
                    await activityTracker.complete(id: activityId)
                    completionHandler(nil)
                } catch {
                    await activityTracker.fail(id: activityId, error: error.localizedDescription)
                    completionHandler(mapError(error))
                }
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
        // specialNodes is awaited inside the enumerator — no need to block here.
        return FileProviderEnumerator(
            container: containerItemIdentifier,
            database: database,
            client: client,
            syncEngine: syncEngine,
            accountId: accountId,
            accountName: accountName,
            specialNodes: specialNodes,
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

    // MARK: - In-flight Upload Guard

    /// Cancel any existing in-flight upload for `key`, bump the generation, and return it.
    /// Call this before creating the Task; store the returned generation for deregistration.
    private func registerInFlight(key: String) -> Int {
        inFlightLock.withLock {
            inFlightUploads[key]?.cancel()
            let gen = (inFlightGenerations[key] ?? 0) &+ 1
            inFlightGenerations[key] = gen
            return gen
        }
    }

    /// Store the task after it has been created, but only if our generation is still current.
    /// If a newer call came in between registerInFlight and here, the task is already superseded.
    private func setInFlight(key: String, generation: Int, task: Task<Void, Never>) {
        inFlightLock.withLock {
            if inFlightGenerations[key] == generation {
                inFlightUploads[key] = task
            }
        }
    }

    /// Remove the registration for `key` only if our generation is still current.
    /// Called from a task's `defer` block so a superseded task doesn't evict a newer one.
    private func deregisterInFlight(key: String, generation: Int) {
        inFlightLock.withLock {
            if inFlightGenerations[key] == generation {
                inFlightUploads.removeValue(forKey: key)
                inFlightGenerations.removeValue(forKey: key)
            }
        }
    }

    private func startPushWatcher() async {
        await pushWatcher.setDelegate(self)
        await pushWatcher.start()
    }

    // MARK: - PushWatcherDelegate

    public func pushWatcherDidReceiveChange(_ watcher: PushWatcher) async {
        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] SSE state change — signalling working set")
        #endif
        NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
    }

    public func pushWatcherDidReconnect(_ watcher: PushWatcher) async {
        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] SSE reconnected — signalling working set to catch up")
        #endif
        NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
    }

    /// Resolve a FileProvider item identifier to a JMAP nodeId.
    private func resolveNodeId(_ identifier: NSFileProviderItemIdentifier, nodes: SpecialNodes) -> String {
        switch identifier {
        case .rootContainer: return nodes.homeId
        case .trashContainer: return nodes.trashId ?? nodes.homeId
        default: return identifier.rawValue
        }
    }

    /// Look up an item by identifier from the database.
    private func lookupItem(_ identifier: NSFileProviderItemIdentifier, nodes: SpecialNodes) async throws -> FileProviderItem {
        let nodeId: String
        switch identifier {
        case .rootContainer:
            nodeId = nodes.homeId
        case .trashContainer:
            guard let trashId = nodes.trashId else {
                // Account has no trash node — return a synthetic read-only empty container so
                // the system gets .trashContainer back (not .rootContainer, which would itemMismatch).
                let entry = NodeCacheEntry(parentId: nodes.homeId, name: "Trash",
                                           isFolder: true,
                                           myRights: FileNodeRights(mayRead: true, mayWrite: false))
                return FileProviderItem(nodeId: "synthetic-trash", entry: entry,
                                        homeNodeId: nodes.homeId, trashNodeId: nil,
                                        isSyntheticTrash: true)
            }
            nodeId = trashId
        default:
            nodeId = identifier.rawValue
        }

        if let entry = await database.entry(for: nodeId) {
            return FileProviderItem(
                nodeId: nodeId, entry: entry,
                homeNodeId: nodes.homeId, trashNodeId: nodes.trashId,
                isPinned: await database.isPinned(id: nodeId))
        }

        // Node not in cache — fetch from server and cache it for future calls.
        let fetched = try await client.getNodes(accountId: accountId, ids: [nodeId])
        guard let node = fetched.first else {
            throw JmapError.notFound(nodeId)
        }
        await database.upsertFromServer(node)
        return FileProviderItem(
            node: node, homeNodeId: nodes.homeId, trashNodeId: nodes.trashId,
            isPinned: await database.isPinned(id: nodeId))
    }

    private func desanitizeFilename(_ name: String) -> String {
        FilenameUtils.desanitize(name)
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

    /// True for errors that are permanent — the system cannot recover by retrying.
    /// These must be returned as-is rather than being swapped for `.cannotSynchronize`
    /// when the upload failure threshold is reached.
    private func isPermanentError(_ error: Error) -> Bool {
        guard let jmapError = error as? JmapError else { return false }
        return !jmapError.isRetriable
    }
}

// MARK: - Traffic Log

/// Simple file-based traffic logger for the FileProvider extension.
/// Writes to the shared App Group container so the app or `tail -f` can read it.
