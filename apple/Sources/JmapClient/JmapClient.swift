import Foundation
#if canImport(os)
import os
#endif

// MARK: - UploadChunkStore

/// Persistence callbacks for resumable chunked uploads.
/// Wire to NodeDatabase in production; pass nil to disable chunk-level resume.
public struct UploadChunkStore: Sendable {
    /// Returns (chunkIndex, blobId, sha1) for all already-completed chunks of this upload.
    public let completedChunks: @Sendable (_ uploadId: String) async -> [(index: Int, blobId: String, sha1: String)]
    /// Called (via taskIdSink) just before a background upload task starts.
    public let startChunk: @Sendable (_ uploadId: String, _ chunkIndex: Int, _ taskId: Int) async -> Void
    /// Called after a chunk upload returns a blobId from the server.
    public let completeChunk: @Sendable (_ uploadId: String, _ chunkIndex: Int, _ blobId: String, _ sha1: String) async -> Void
    /// Called after Blob/set combine succeeds — cleans up the session's rows.
    public let clearUpload: @Sendable (_ uploadId: String) async -> Void
    /// Look up a cached blobId for a chunk with this SHA1 (content-addressed dedup).
    /// Returns nil on miss — caller must upload and then call cacheChunkSha1.
    /// Safe to use even if the server is not content-addressed: a stale blobId causes
    /// a Blob/set failure which the upload path recovers from by re-uploading.
    public let cachedBlobId: @Sendable (_ sha1: String) async -> String?
    /// Persist a SHA1→blobId mapping so future uploads can skip identical content.
    /// On servers where the same bytes can yield different blobIds, only the most
    /// recently uploaded id is stored; older entries are silently replaced.
    public let cacheChunkSha1: @Sendable (_ sha1: String, _ blobId: String) async -> Void

    public init(
        completedChunks: @escaping @Sendable (_ uploadId: String) async -> [(index: Int, blobId: String, sha1: String)],
        startChunk: @escaping @Sendable (_ uploadId: String, _ chunkIndex: Int, _ taskId: Int) async -> Void,
        completeChunk: @escaping @Sendable (_ uploadId: String, _ chunkIndex: Int, _ blobId: String, _ sha1: String) async -> Void,
        clearUpload: @escaping @Sendable (_ uploadId: String) async -> Void,
        cachedBlobId: @escaping @Sendable (_ sha1: String) async -> String? = { _ in nil },
        cacheChunkSha1: @escaping @Sendable (_ sha1: String, _ blobId: String) async -> Void = { _, _ in }
    ) {
        self.completedChunks = completedChunks
        self.startChunk = startChunk
        self.completeChunk = completeChunk
        self.clearUpload = clearUpload
        self.cachedBlobId = cachedBlobId
        self.cacheChunkSha1 = cacheChunkSha1
    }
}

// MARK: - JmapClient

/// JMAP API client for FileNode operations.
///
/// All JMAP method calls go through this client. It handles request
/// construction, batching, response parsing, and error mapping.
public actor JmapClient {
    private let sessionManager: SessionManager
    private let decoder: JSONDecoder
    private let encoder: JSONEncoder

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "JmapClient")
    #endif

    /// Separate URLSessions for interactive (downloads) vs background (uploads) traffic.
    /// This prevents upload traffic from starving downloads via HTTP/2 multiplexing.
    /// See DESIGN.md pitfall #22.
    private let interactiveSession: URLSession
    private let backgroundSession: URLSession

    // Store a reference to the token provider's getter and invalidator
    nonisolated private let tokenGetter: @Sendable () async throws -> String
    nonisolated private let tokenInvalidator: (@Sendable () async -> Void)?

    /// Optional hook called with (URL, bodyData) before each JMAP API request.
    /// Used by debug logging to capture body before URLSession converts it to a stream.
    nonisolated public let requestWillSend: (@Sendable (URL, Data) -> Void)?

    /// Optional hook called with (URL, statusCode, responseData) after each JMAP API response.
    nonisolated public let responseDidReceive: (@Sendable (URL, Int, Data) -> Void)?

    /// Directory used for temporary chunk files during uploads.
    /// Defaults to the system temp dir; override in the extension to use the App Group container
    /// so that orphaned chunks survive a process kill and can be swept on next startup.
    private let temporaryDirectory: URL

    /// Background URLSession wrapper for chunk uploads. When set, chunk HTTP POSTs run in
    /// nsurlsessiond and survive extension process kills. When nil, the regular session is used.
    private let backgroundUploader: BackgroundUploader?

    /// Persistence hooks for resumable chunked uploads. When set, completed chunk blobIds are
    /// written to the database so a restarted extension can skip already-uploaded chunks.
    public let chunkStore: UploadChunkStore?

    /// Pending accessed timestamps to piggyback on the next JMAP call.
    private var pendingAccessed: [String: Date] = [:]
    /// Fallback timer: flushes pending accessed timestamps if no JMAP call happens within 5 minutes.
    private var accessedFlushTask: Task<Void, Never>?
    private let accessedFlushInterval: TimeInterval = 300 // 5 minutes
    /// The accountId to use for piggybacked accessed updates.
    /// Set by callers via recordAccess.
    private var accessedAccountId: String?

    // MARK: - JMAP Method Calls

    /// Execute a batch of JMAP method calls.
    public func call(
        _ methodCalls: [JmapMethodCall],
        using capabilities: [String]? = nil
    ) async throws -> [[AnyCodable]] {
        let session = try await sessionManager.session()
        guard let apiURL = URL(string: session.apiUrl) else {
            throw JmapError.invalidResponse
        }

        var effectiveCapabilities = capabilities ?? buildCapabilities(session: session)
        var allMethodCalls = methodCalls

        // Piggyback pending accessed timestamps
        let accessedBatch = drainPendingAccessed()
        if !accessedBatch.isEmpty, let accountId = accessedAccountId {
            let formatter = ISO8601DateFormatter()
            var updateFields: [String: AnyCodable] = [:]
            for (nodeId, date) in accessedBatch {
                updateFields[nodeId] = AnyCodable(["accessed": AnyCodable(formatter.string(from: date))])
            }
            allMethodCalls.append(JmapMethodCall(
                name: "FileNode/set",
                args: [
                    "accountId": AnyCodable(accountId),
                    "update": AnyCodable(updateFields),
                ],
                callId: "_accessed"
            ))
            // Ensure FileNode capability is included
            if let fileNodeURI = session.fileNodeCapabilityURI,
               !effectiveCapabilities.contains(fileNodeURI) {
                effectiveCapabilities.append(fileNodeURI)
            }
        }

        let body = JmapRequestBody(
            using: effectiveCapabilities,
            methodCalls: allMethodCalls.map { $0.asArray() }
        )

        var request = URLRequest(url: apiURL)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let bodyData = try encoder.encode(body)
        request.httpBody = bodyData
        if let url = request.url {
            let cb = requestWillSend ?? { u, d in TrafficLog.shared.log("→ POST \(u.absoluteString)\n\(TrafficLog.formatBody(d))") }
            cb(url, bodyData)
        }

        let (data, httpResponse) = try await authorizedRequest(request, session: interactiveSession)
        if let url = request.url {
            let cb = responseDidReceive ?? { u, s, d in TrafficLog.shared.log("← \(s) \(u.absoluteString)\n\(TrafficLog.formatBody(d))") }
            cb(url, httpResponse.statusCode, data)
        }
        try checkHTTPStatus(httpResponse, data: data)

        let response = try decoder.decode(JmapResponse.self, from: data)

        // If the server's sessionState differs from our cached session, the session
        // document has changed (new capabilities, URLs, etc.) — invalidate so the
        // next call re-fetches it.
        if let responseState = response.sessionState,
           let cachedState = await sessionManager.cachedState,
           responseState != cachedState {
            await sessionManager.invalidate()
        }

        // Filter out the piggybacked response — callers don't need it
        if accessedBatch.isEmpty {
            return response.methodResponses
        } else {
            return response.methodResponses.filter { entry in
                guard entry.count >= 3 else { return true }
                return entry[2].stringValue != "_accessed"
            }
        }
    }

    // MARK: - FileNode Operations

    /// Find the home node (role: "home").
    public func findHomeNode(accountId: String) async throws -> FileNode {
        let responses = try await call([
            JmapMethodCall(
                name: "FileNode/query",
                args: [
                    "accountId": AnyCodable(accountId),
                    "filter": ["role": "home"],
                ],
                callId: "q0"
            ),
            JmapMethodCall(
                name: "FileNode/get",
                args: [
                    "accountId": AnyCodable(accountId),
                    "#ids": [
                        "resultOf": "q0",
                        "name": "FileNode/query",
                        "path": "/ids",
                    ],
                    "properties": AnyCodable(FileNode.standardProperties),
                ],
                callId: "g0"
            ),
        ])

        let getResponse = try extractResponse(FileNodeGetResponse.self, from: responses, callId: "g0")
        guard let home = getResponse.list.first else {
            throw JmapError.notFound("home node")
        }
        return home
    }

    /// Find the trash node (role: "trash"). Returns nil if no trash exists.
    public func findTrashNode(accountId: String) async throws -> FileNode? {
        let responses = try await call([
            JmapMethodCall(
                name: "FileNode/query",
                args: [
                    "accountId": AnyCodable(accountId),
                    "filter": ["role": "trash"],
                ],
                callId: "q0"
            ),
            JmapMethodCall(
                name: "FileNode/get",
                args: [
                    "accountId": AnyCodable(accountId),
                    "#ids": [
                        "resultOf": "q0",
                        "name": "FileNode/query",
                        "path": "/ids",
                    ],
                    "properties": AnyCodable(FileNode.standardProperties),
                ],
                callId: "g0"
            ),
        ])

        let getResponse = try extractResponse(FileNodeGetResponse.self, from: responses, callId: "g0")
        return getResponse.list.first
    }

    /// Query all FileNode IDs, paginated.
    public func queryAllNodeIds(accountId: String, pageSize: Int = 4096) async throws -> [String] {
        var allIds: [String] = []
        var position = 0

        while true {
            let responses = try await call([
                JmapMethodCall(
                    name: "FileNode/query",
                    args: [
                        "accountId": AnyCodable(accountId),
                        "position": AnyCodable(position),
                        "limit": AnyCodable(pageSize),
                    ],
                    callId: "q0"
                ),
            ])

            let queryResponse = try extractResponse(FileNodeQueryResponse.self, from: responses, callId: "q0")
            allIds.append(contentsOf: queryResponse.ids)

            let total = queryResponse.total ?? allIds.count
            if allIds.count >= total {
                break
            }
            position = allIds.count
        }

        return allIds
    }

    /// Fetch FileNodes by IDs, paginated (max 1024 per call).
    public func getNodes(accountId: String, ids: [String], pageSize: Int = 1024) async throws -> [FileNode] {
        var allNodes: [FileNode] = []

        for startIndex in stride(from: 0, to: ids.count, by: pageSize) {
            let endIndex = min(startIndex + pageSize, ids.count)
            let pageIds = Array(ids[startIndex..<endIndex])

            let responses = try await call([
                JmapMethodCall(
                    name: "FileNode/get",
                    args: [
                        "accountId": AnyCodable(accountId),
                        "ids": AnyCodable(pageIds.map { AnyCodable($0) }),
                        "properties": AnyCodable(FileNode.standardProperties),
                    ],
                    callId: "g0"
                ),
            ])

            let getResponse = try extractResponse(FileNodeGetResponse.self, from: responses, callId: "g0")
            allNodes.append(contentsOf: getResponse.list)
        }

        return allNodes
    }

    /// Fetch children of multiple parent folders in a single HTTP request.
    /// Sends one FileNode/query+get pair per parentId (up to 16 at a time = 32 method calls).
    /// Returns children grouped by parentId, and the state from the last get response.
    public func getChildrenBatched(
        accountId: String,
        parentIds: [String]
    ) async throws -> (childrenByParent: [String: [FileNode]], state: String) {
        guard !parentIds.isEmpty else { return ([:], "") }

        #if canImport(os)
        logger.info("[\(accountId, privacy: .public)] getChildrenBatched: \(parentIds.count) parents")
        #endif
        var methodCalls: [JmapMethodCall] = []
        for (i, parentId) in parentIds.enumerated() {
            let qId = "q\(i)"
            let gId = "g\(i)"
            methodCalls.append(JmapMethodCall(
                name: "FileNode/query",
                args: [
                    "accountId": AnyCodable(accountId),
                    "filter": ["parentId": AnyCodable(parentId)],
                ],
                callId: qId
            ))
            methodCalls.append(JmapMethodCall(
                name: "FileNode/get",
                args: [
                    "accountId": AnyCodable(accountId),
                    "#ids": [
                        "resultOf": AnyCodable(qId),
                        "name": "FileNode/query",
                        "path": "/ids",
                    ],
                    "properties": AnyCodable(FileNode.standardProperties),
                ],
                callId: gId
            ))
        }

        let responses = try await call(methodCalls)

        var childrenByParent: [String: [FileNode]] = [:]
        var state: String = ""
        for (i, parentId) in parentIds.enumerated() {
            let getResponse = try extractResponse(FileNodeGetResponse.self, from: responses, callId: "g\(i)")
            childrenByParent[parentId] = getResponse.list
            state = getResponse.state
        }
        #if canImport(os)
        let totalChildren = childrenByParent.values.reduce(0) { $0 + $1.count }
        logger.info("[\(accountId, privacy: .public)] getChildrenBatched: \(totalChildren) nodes, state=\(state, privacy: .public)")
        #endif
        return (childrenByParent, state)
    }

    /// Fetch all FileNodes and the current state token.
    /// Queries all IDs first, then fetches nodes in batches of 1024 with explicit IDs —
    /// matching the Windows approach (GetFileNodesByIdsPagedAsync). State is extracted
    /// from the last get response so no separate state fetch is needed.
    public func queryAndGetAllNodes(accountId: String, batchSize: Int = 1024) async throws -> (nodes: [FileNode], state: String) {
        let allIds = try await queryAllNodeIds(accountId: accountId)
        guard !allIds.isEmpty else { return ([], "") }

        var allNodes: [FileNode] = []
        var state: String = ""

        for startIndex in stride(from: 0, to: allIds.count, by: batchSize) {
            let endIndex = min(startIndex + batchSize, allIds.count)
            let batchIds = Array(allIds[startIndex..<endIndex])

            let responses = try await call([
                JmapMethodCall(
                    name: "FileNode/get",
                    args: [
                        "accountId": AnyCodable(accountId),
                        "ids": AnyCodable(batchIds.map { AnyCodable($0) }),
                        "properties": AnyCodable(FileNode.standardProperties),
                    ],
                    callId: "g0"
                ),
            ])

            let getResponse = try extractResponse(FileNodeGetResponse.self, from: responses, callId: "g0")
            allNodes.append(contentsOf: getResponse.list)
            state = getResponse.state
        }

        return (allNodes, state)
    }

    /// Fetch incremental changes since a state token.
    /// Returns changes and the full FileNode objects for created/updated items.
    public func getChanges(
        accountId: String,
        sinceState: String
    ) async throws -> (changes: FileNodeChangesResponse, created: [FileNode], updated: [FileNode]) {
        #if canImport(os)
        logger.info("[\(accountId, privacy: .public)] FileNode/changes sinceState=\(sinceState, privacy: .public)")
        #endif
        let responses = try await call([
            JmapMethodCall(
                name: "FileNode/changes",
                args: [
                    "accountId": AnyCodable(accountId),
                    "sinceState": AnyCodable(sinceState),
                ],
                callId: "c0"
            ),
            JmapMethodCall(
                name: "FileNode/get",
                args: [
                    "accountId": AnyCodable(accountId),
                    "#ids": [
                        "resultOf": "c0",
                        "name": "FileNode/changes",
                        "path": "/created",
                    ],
                    "properties": AnyCodable(FileNode.standardProperties),
                ],
                callId: "g-created"
            ),
            JmapMethodCall(
                name: "FileNode/get",
                args: [
                    "accountId": AnyCodable(accountId),
                    "#ids": [
                        "resultOf": "c0",
                        "name": "FileNode/changes",
                        "path": "/updated",
                    ],
                    "properties": AnyCodable(FileNode.standardProperties),
                ],
                callId: "g-updated"
            ),
        ])

        let changesResponse = try extractChangesResponse(from: responses, callId: "c0")
        let createdNodes = try extractResponse(FileNodeGetResponse.self, from: responses, callId: "g-created")
        let updatedNodes = try extractResponse(FileNodeGetResponse.self, from: responses, callId: "g-updated")

        #if canImport(os)
        logger.info("[\(accountId, privacy: .public)] FileNode/changes result: \(createdNodes.list.count) created, \(updatedNodes.list.count) updated, \(changesResponse.destroyed.count) destroyed, newState=\(changesResponse.newState, privacy: .public)")
        #endif
        return (changesResponse, createdNodes.list, updatedNodes.list)
    }

    /// Create a FileNode (file or folder).
    public func createNode(
        accountId: String,
        parentId: String,
        name: String,
        blobId: String? = nil,
        type: String? = nil,
        created: Date? = nil,
        modified: Date? = nil,
        onExists: String? = "replace"
    ) async throws -> FileNode {
        var createArgs: [String: AnyCodable] = [
            "parentId": AnyCodable(parentId),
            "name": AnyCodable(name),
        ]
        if let blobId = blobId { createArgs["blobId"] = AnyCodable(blobId) }
        if let type = type { createArgs["type"] = AnyCodable(type) }
        if let created = created {
            createArgs["created"] = AnyCodable(ISO8601DateFormatter().string(from: created))
        }
        if let modified = modified {
            createArgs["modified"] = AnyCodable(ISO8601DateFormatter().string(from: modified))
        }

        var setArgs: [String: AnyCodable] = [
            "accountId": AnyCodable(accountId),
            "compareCaseInsensitively": AnyCodable(true),
            "create": ["c0": AnyCodable(createArgs)],
        ]
        if let onExists = onExists {
            setArgs["onExists"] = AnyCodable(onExists)
        }

        let responses = try await call([
            JmapMethodCall(name: "FileNode/set", args: setArgs, callId: "s0"),
        ])

        let setResponse = try extractResponse(FileNodeSetResponse.self, from: responses, callId: "s0")

        if let error = setResponse.notCreated?["c0"] {
            if error.type == "forbidden" {
                throw JmapError.forbidden(error.description)
            }
            if error.type == "alreadyExists" {
                throw JmapError.alreadyExists
            }
            throw JmapError.serverError(error.type, error.description)
        }

        guard let node = setResponse.created?["c0"] else {
            throw JmapError.serverError("unknownError", "No created node in response")
        }
        return node
    }

    /// Update a FileNode (rename, move, or both).
    public func updateNode(
        accountId: String,
        nodeId: String,
        parentId: String? = nil,
        name: String? = nil,
        modified: Date? = nil,
        onExists: String? = nil
    ) async throws {
        var updateFields: [String: AnyCodable] = [:]
        if let parentId = parentId { updateFields["parentId"] = AnyCodable(parentId) }
        if let name = name { updateFields["name"] = AnyCodable(name) }
        if let modified = modified {
            updateFields["modified"] = AnyCodable(ISO8601DateFormatter().string(from: modified))
        } else if parentId != nil || name != nil {
            // Metadata change — set modified to null so server uses current time
            updateFields["modified"] = AnyCodable(NSNull())
        }

        var setArgs: [String: AnyCodable] = [
            "accountId": AnyCodable(accountId),
            "compareCaseInsensitively": AnyCodable(true),
            "update": [nodeId: AnyCodable(updateFields)],
        ]
        if let onExists = onExists {
            setArgs["onExists"] = AnyCodable(onExists)
        }

        let responses = try await call([
            JmapMethodCall(name: "FileNode/set", args: setArgs, callId: "s0"),
        ])

        let setResponse = try extractResponse(FileNodeSetResponse.self, from: responses, callId: "s0")

        if let error = setResponse.notUpdated?[nodeId] {
            if error.type == "notFound" {
                throw JmapError.notFound(nodeId)
            }
            if error.type == "forbidden" {
                throw JmapError.forbidden(error.description)
            }
            if error.type == "alreadyExists" {
                throw JmapError.alreadyExists
            }
            throw JmapError.serverError(error.type, error.description)
        }
    }

    /// Update a file's content by setting a new blobId (v10: blobId is now mutable).
    /// The node ID stays the same — no destroy+create needed.
    public func updateNodeContent(
        accountId: String,
        nodeId: String,
        blobId: String,
        type: String? = nil,
        modified: Date? = nil
    ) async throws {
        var updateFields: [String: AnyCodable] = [
            "blobId": AnyCodable(blobId),
        ]
        if let type = type { updateFields["type"] = AnyCodable(type) }
        if let modified = modified {
            updateFields["modified"] = AnyCodable(ISO8601DateFormatter().string(from: modified))
        } else {
            // Client-managed timestamps: set to null so server uses current time
            updateFields["modified"] = AnyCodable(NSNull())
        }

        let responses = try await call([
            JmapMethodCall(
                name: "FileNode/set",
                args: [
                    "accountId": AnyCodable(accountId),
                    "update": [nodeId: AnyCodable(updateFields)],
                ],
                callId: "s0"
            ),
        ])

        let setResponse = try extractResponse(FileNodeSetResponse.self, from: responses, callId: "s0")

        if let error = setResponse.notUpdated?[nodeId] {
            if error.type == "notFound" {
                throw JmapError.notFound(nodeId)
            }
            if error.type == "forbidden" {
                throw JmapError.forbidden(error.description)
            }
            throw JmapError.serverError(error.type, error.description)
        }
    }

    /// Destroy a FileNode.
    public func destroyNode(
        accountId: String,
        nodeId: String,
        removeChildren: Bool = true
    ) async throws {
        let responses = try await call([
            JmapMethodCall(
                name: "FileNode/set",
                args: [
                    "accountId": AnyCodable(accountId),
                    "destroy": AnyCodable([AnyCodable(nodeId)]),
                    "onDestroyRemoveChildren": AnyCodable(removeChildren),
                ],
                callId: "s0"
            ),
        ])

        let setResponse = try extractResponse(FileNodeSetResponse.self, from: responses, callId: "s0")

        if let error = setResponse.notDestroyed?[nodeId] {
            // notFound is success (idempotent delete)
            if error.type == "notFound" { return }
            if error.type == "forbidden" {
                throw JmapError.forbidden(error.description)
            }
            throw JmapError.serverError(error.type, error.description)
        }
    }

    // MARK: - Blob Operations

    /// Upload a blob from a file URL. Returns the blobId.
    public func uploadBlob(
        accountId: String,
        fileURL: URL,
        contentType: String,
        progress: (@Sendable (Int64, Int64) -> Void)? = nil
    ) async throws -> BlobUploadResponse {
        let session = try await sessionManager.session()
        guard let uploadURL = session.uploadURL(accountId: accountId) else {
            throw JmapError.invalidResponse
        }

        var request = URLRequest(url: uploadURL)
        request.httpMethod = "POST"
        request.setValue(contentType, forHTTPHeaderField: "Content-Type")

        // Use background session for uploads to avoid starving downloads
        let (data, httpResponse) = try await authorizedUpload(
            request, fromFile: fileURL, session: backgroundSession,
            onBytesSent: progress.map { cb in { @Sendable sent, total in cb(sent, total) } })
        try checkHTTPStatus(httpResponse, data: data)

        return try decoder.decode(BlobUploadResponse.self, from: data)
    }

    // MARK: - Chunked Upload

    private static let minChunkSize = 1_048_576        // 1 MB
    private static let maxChunkSize = 67_108_864        // 64 MB

    /// Read `length` bytes from `source` in 1 MB buffers, computing SHA1 hashes incrementally.
    ///
    /// - Parameter destURL: Write output here. Pass `nil` for hash-only (skips file write),
    ///   used when resuming: completed chunks still need to contribute to the overall SHA1.
    /// - Returns: Per-chunk SHA1 (base64), or nil if source was already at EOF.
    private static func processChunk(
        from source: FileHandle,
        to destURL: URL?,
        length: Int,
        updating overallHash: inout SHA1Context
    ) throws -> String? {
        let bufferSize = 1_048_576  // 1 MB
        var dest: FileHandle? = nil
        if let destURL {
            FileManager.default.createFile(atPath: destURL.path, contents: nil)
            dest = try FileHandle(forWritingTo: destURL)
        }
        var chunkHash = SHA1Context()
        var bytesRead = 0
        var remaining = length
        while remaining > 0 {
            let n = min(bufferSize, remaining)
            let buf = source.readData(ofLength: n)
            guard !buf.isEmpty else { break }
            chunkHash.update(buf)
            overallHash.update(buf)
            dest?.write(buf)
            bytesRead += buf.count
            remaining -= buf.count
        }
        try? dest?.close()
        guard bytesRead > 0 else {
            if let destURL { try? FileManager.default.removeItem(at: destURL) }
            return nil
        }
        return chunkHash.finalize()
    }

    private static let defaultChunkSize = 67_108_864    // 64 MB
    private static let chunkThreshold = 5_000_000       // 5 MB — files smaller than this skip chunking

    /// Upload a large file in chunks using Blob/set (blob2) to combine.
    /// Falls back to single upload if blob2 capability is not available or file is small.
    public func uploadBlobChunked(
        accountId: String,
        fileURL: URL,
        contentType: String,
        uploadId: String? = nil,
        progress: (@Sendable (Int64, Int64) -> Void)? = nil
    ) async throws -> BlobUploadResponse {
        let session = try await sessionManager.session()

        let fileSize = try FileManager.default.attributesOfItem(
            atPath: fileURL.path)[.size] as? Int64 ?? 0

        // Small files or no blob2 capability — use simple upload
        guard fileSize > Self.chunkThreshold,
              session.hasBlob2(accountId: accountId) else {
            return try await uploadBlob(
                accountId: accountId, fileURL: fileURL,
                contentType: contentType, progress: progress)
        }

        // Reject immediately if file exceeds the server's Blob/set size limit.
        if let maxSize = session.maxSizeBlobSet(accountId: accountId), Int(fileSize) > maxSize {
            throw JmapError.serverError("uploadBlobChunked",
                "File size \(fileSize) exceeds server maxSizeBlobSet \(maxSize)")
        }

        // Determine chunk size. Start from the server-suggested blob2 chunkSize
        // hint (or 64 MB default). maxDataSources comes from the legacy blob
        // capability metadata — readable without sending that URI.
        let blobCap = session.accounts[accountId]?.accountCapabilities[JmapCapability.blob]
        let maxDataSources = blobCap?.dictValue?["maxDataSources"]?.intValue ?? 100
        var chunkSize = session.blob2ChunkSize(accountId: accountId) ?? Self.defaultChunkSize

        // Adjust chunk size up if file would exceed maxDataSources chunks
        while Int(fileSize) / chunkSize + 1 > maxDataSources && chunkSize < Int(fileSize) {
            chunkSize *= 2
        }
        chunkSize = min(max(Self.minChunkSize, chunkSize), Self.maxChunkSize)  // BUG-014 fix

        guard let uploadURL = session.uploadURL(accountId: accountId) else {
            throw JmapError.invalidResponse
        }

        // Load already-completed chunks from a prior attempt that was killed mid-upload.
        var resumedChunks: [Int: (blobId: String, sha1: String)] = [:]
        if let uploadId, let store = chunkStore {
            for chunk in await store.completedChunks(uploadId) {
                resumedChunks[chunk.index] = (chunk.blobId, chunk.sha1)
            }
        }

        let fileHandle = try FileHandle(forReadingFrom: fileURL)
        defer { try? fileHandle.close() }

        var chunkBlobIds: [(blobId: String, sha1: String)] = []
        var totalUploaded: Int64 = 0
        var chunkIndex = 0
        var overallHashContext = SHA1Context()

        while totalUploaded < fileSize {
            let remaining = Int(fileSize - totalUploaded)
            let thisChunkSize = min(chunkSize, remaining)

            if let resumed = resumedChunks[chunkIndex] {
                // This chunk already completed in a previous attempt. Hash the bytes (needed
                // for the overall SHA1) but skip the upload — no bandwidth consumed.
                guard (try Self.processChunk(
                    from: fileHandle, to: nil,
                    length: thisChunkSize, updating: &overallHashContext)) != nil else { break }
                chunkBlobIds.append(resumed)
                totalUploaded += Int64(thisChunkSize)
                chunkIndex += 1
                progress?(totalUploaded, fileSize)
                continue
            }

            // Report progress at chunk start (smooths out the jumps)
            progress?(totalUploaded, fileSize)

            // Process chunk: hash + write in 1 MB buffers to stay within ~2 MB peak RAM.
            let tempChunkFile = temporaryDirectory.appendingPathComponent(UUID().uuidString)
            guard let chunkSha1 = try Self.processChunk(
                from: fileHandle, to: tempChunkFile,
                length: thisChunkSize, updating: &overallHashContext) else { break }
            defer { try? FileManager.default.removeItem(at: tempChunkFile) }

            // SHA1 cache hit: this exact chunk content is already on the server.
            // Reuse the blobId directly without any network traffic.
            if let store = chunkStore, let cachedId = await store.cachedBlobId(chunkSha1) {
                chunkBlobIds.append((cachedId, chunkSha1))
                totalUploaded += Int64(thisChunkSize)
                chunkIndex += 1
                progress?(totalUploaded, fileSize)
                continue  // defer above removes tempChunkFile
            }

            let totalChunks = Int(fileSize) / chunkSize + 1
            requestWillSend?(uploadURL, "chunk \(chunkIndex + 1)/\(totalChunks): \(thisChunkSize) bytes".data(using: .utf8)!)

            let chunkBaseOffset = totalUploaded
            let (data, httpResponse): (Data, HTTPURLResponse)
            if let bgUploader = backgroundUploader {
                // Background session path: task survives extension process kill.
                // Add auth token now — background sessions can't retry with a new token.
                let token = try await getToken()
                var authedRequest = URLRequest(url: uploadURL)
                authedRequest.httpMethod = "POST"
                authedRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                authedRequest.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

                let capturedUploadId = uploadId
                let capturedChunkIndex = chunkIndex
                let capturedStore = chunkStore
                (data, httpResponse) = try await bgUploader.upload(
                    request: authedRequest,
                    fromFile: tempChunkFile
                ) { @Sendable taskId in
                    if let uid = capturedUploadId, let store = capturedStore {
                        Task { await store.startChunk(uid, capturedChunkIndex, taskId) }
                    }
                }
            } else {
                var req = URLRequest(url: uploadURL)
                req.httpMethod = "POST"
                req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                (data, httpResponse) = try await authorizedUpload(
                    req, fromFile: tempChunkFile, session: backgroundSession,
                    onBytesSent: { bytesSent, _ in
                        progress?(chunkBaseOffset + bytesSent, fileSize)
                    })
            }

            responseDidReceive?(uploadURL, httpResponse.statusCode, data)
            try checkHTTPStatus(httpResponse, data: data)

            let uploadResponse = try decoder.decode(BlobUploadResponse.self, from: data)

            // Persist completion so a future restart can skip this chunk.
            if let uploadId, let store = chunkStore {
                await store.completeChunk(uploadId, chunkIndex, uploadResponse.blobId, chunkSha1)
            }
            // Cache SHA1→blobId for cross-upload deduplication.
            if let store = chunkStore {
                await store.cacheChunkSha1(chunkSha1, uploadResponse.blobId)
            }

            chunkBlobIds.append((uploadResponse.blobId, chunkSha1))
            totalUploaded += Int64(thisChunkSize)
            chunkIndex += 1
        }

        // If only one chunk, no need to combine
        if chunkBlobIds.count == 1 {
            return BlobUploadResponse(
                blobId: chunkBlobIds[0].blobId,
                size: Int(fileSize), type: contentType)
        }

        // Combine chunks via Blob/set (blob2)
        let overallSha1 = overallHashContext.finalize()

        let dataSourceObjects: [[String: AnyCodable]] = chunkBlobIds.map { chunk in
            [
                "blobId": AnyCodable(chunk.blobId),
                "digest:sha": AnyCodable(chunk.sha1),
            ]
        }

        let createObj: [String: AnyCodable] = [
            "data": AnyCodable(dataSourceObjects.map { AnyCodable($0) }),
            "type": AnyCodable(contentType),
            "digest:sha": AnyCodable(overallSha1),
        ]

        let capabilities = [JmapCapability.core, JmapCapability.blob2]

        let responses = try await call([
            JmapMethodCall(
                name: "Blob/set",
                args: [
                    "accountId": AnyCodable(accountId),
                    "create": ["combined": AnyCodable(createObj)],
                ],
                callId: "b0"
            ),
        ], using: capabilities)

        // Extract the combined blobId from response
        guard let response = responses.first,
              response.count >= 3,
              let argsDict = response[1].dictValue,
              let created = argsDict["created"]?.dictValue,
              let combined = created["combined"]?.dictValue,
              let blobId = combined["id"]?.stringValue ?? combined["blobId"]?.stringValue
        else {
            throw JmapError.serverError("blobUpload", "Failed to combine chunks")
        }

        // Blob/set succeeded — clear the chunk rows so they don't accumulate.
        if let uploadId, let store = chunkStore {
            await store.clearUpload(uploadId)
        }

        let size = combined["size"]?.intValue ?? Int(fileSize)
        return BlobUploadResponse(blobId: blobId, size: size, type: contentType)
    }

    /// Upload a file using delta-aware chunking: queries the server for the old blob's
    /// chunk structure, compares SHA1 hashes, and only uploads changed chunks.
    /// Falls back to full chunked upload if no old blob or server doesn't support chunks.
    public func uploadBlobDelta(
        accountId: String,
        fileURL: URL,
        contentType: String,
        oldBlobId: String?,
        uploadId: String? = nil,
        progress: (@Sendable (Int64, Int64) -> Void)? = nil
    ) async throws -> BlobUploadResponse {
        let session = try await sessionManager.session()

        // No old blob or no blob2 → fall back to full chunked upload
        guard let oldBlobId = oldBlobId,
              session.hasBlob2(accountId: accountId) else {
            return try await uploadBlobChunked(
                accountId: accountId, fileURL: fileURL,
                contentType: contentType, uploadId: uploadId, progress: progress)
        }

        let fileSize = try FileManager.default.attributesOfItem(
            atPath: fileURL.path)[.size] as? Int64 ?? 0

        // Small files — no benefit from delta
        guard fileSize > Int64(Self.chunkThreshold) else {
            return try await uploadBlob(
                accountId: accountId, fileURL: fileURL,
                contentType: contentType, progress: progress)
        }

        // Query server for old blob's chunk structure
        var serverChunks: [(blobId: String, size: Int, digestSha: String?)]?
        do {
            let capabilities = [JmapCapability.core, JmapCapability.blob2]
            let responses = try await call([
                JmapMethodCall(
                    name: "Blob/get",
                    args: [
                        "accountId": AnyCodable(accountId),
                        "ids": AnyCodable([AnyCodable(oldBlobId)]),
                        "properties": AnyCodable(["id", "size", "chunks"].map { AnyCodable($0) }),
                    ],
                    callId: "b0"
                ),
            ], using: capabilities)

            if let response = responses.first,
               response.count >= 3,
               let argsDict = response[1].dictValue,
               let list = argsDict["list"]?.arrayValue,
               let blob = list.first?.dictValue,
               let chunks = blob["chunks"]?.arrayValue {
                serverChunks = chunks.compactMap { chunk -> (String, Int, String?)? in
                    guard let dict = chunk.dictValue,
                          let blobId = dict["blobId"]?.stringValue,
                          let size = dict["size"]?.intValue else { return nil }
                    let digestSha = dict["digest:sha"]?.stringValue
                    return (blobId, size, digestSha)
                }
            }
        } catch {
            // Fall back to full upload
        }

        // Populate SHA1 cache from server chunks — these are live blobs referenced by an
        // existing FileNode, so their blobIds are valid. Future uploads with overlapping
        // content (other files, re-uploads of this file) can reuse them without network I/O.
        if let serverChunks, let store = chunkStore {
            for chunk in serverChunks {
                if let digest = chunk.digestSha {
                    await store.cacheChunkSha1(digest, chunk.blobId)
                }
            }
        }

        guard let serverChunks = serverChunks, !serverChunks.isEmpty else {
            return try await uploadBlobChunked(
                accountId: accountId, fileURL: fileURL,
                contentType: contentType, progress: progress)
        }

        // Delta upload
        guard let uploadURL = session.uploadURL(accountId: accountId) else {
            throw JmapError.invalidResponse
        }

        let fileHandle = try FileHandle(forReadingFrom: fileURL)
        defer { try? fileHandle.close() }

        var chunkBlobIds: [(blobId: String, sha1: String)] = []
        var totalUploaded: Int64 = 0
        var overallHashContext = SHA1Context()
        var reusedChunks = 0

        for serverChunk in serverChunks {
            guard totalUploaded < fileSize else { break }
            let thisChunkSize = min(serverChunk.size, Int(fileSize - totalUploaded))

            // Stream the chunk to a temp file in 1 MB buffers for both the reuse check and upload.
            let tempChunkFile = temporaryDirectory.appendingPathComponent(UUID().uuidString)
            guard let chunkSha1 = try Self.processChunk(
                from: fileHandle, to: tempChunkFile,
                length: thisChunkSize, updating: &overallHashContext) else { break }
            defer { try? FileManager.default.removeItem(at: tempChunkFile) }

            if let serverDigest = serverChunk.digestSha, chunkSha1 == serverDigest {
                // Reuse server chunk — temp file is cleaned up by defer above
                chunkBlobIds.append((serverChunk.blobId, chunkSha1))
                reusedChunks += 1
            } else if let store = chunkStore, let cachedId = await store.cachedBlobId(chunkSha1) {
                // SHA1 cache hit — another file already uploaded this exact content
                chunkBlobIds.append((cachedId, chunkSha1))
                reusedChunks += 1
            } else {
                // Upload new chunk
                var request = URLRequest(url: uploadURL)
                request.httpMethod = "POST"
                request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

                let (data, httpResponse) = try await authorizedUpload(
                    request, fromFile: tempChunkFile, session: backgroundSession)
                try checkHTTPStatus(httpResponse, data: data)

                let uploadResponse = try decoder.decode(BlobUploadResponse.self, from: data)
                if let store = chunkStore {
                    await store.cacheChunkSha1(chunkSha1, uploadResponse.blobId)
                }
                chunkBlobIds.append((uploadResponse.blobId, chunkSha1))
            }

            totalUploaded += Int64(thisChunkSize)
            progress?(totalUploaded, fileSize)
        }

        // Upload any remaining data beyond server's chunk count
        let defaultChunk = Self.defaultChunkSize
        while totalUploaded < fileSize {
            let remaining = Int(fileSize - totalUploaded)
            let thisChunkSize = min(defaultChunk, remaining)

            let tempChunkFile = temporaryDirectory.appendingPathComponent(UUID().uuidString)
            guard let chunkSha1 = try Self.processChunk(
                from: fileHandle, to: tempChunkFile,
                length: thisChunkSize, updating: &overallHashContext) else { break }
            defer { try? FileManager.default.removeItem(at: tempChunkFile) }

            if let store = chunkStore, let cachedId = await store.cachedBlobId(chunkSha1) {
                chunkBlobIds.append((cachedId, chunkSha1))
            } else {
                var request = URLRequest(url: uploadURL)
                request.httpMethod = "POST"
                request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

                let (data, httpResponse) = try await authorizedUpload(
                    request, fromFile: tempChunkFile, session: backgroundSession)
                try checkHTTPStatus(httpResponse, data: data)

                let uploadResponse = try decoder.decode(BlobUploadResponse.self, from: data)
                if let store = chunkStore {
                    await store.cacheChunkSha1(chunkSha1, uploadResponse.blobId)
                }
                chunkBlobIds.append((uploadResponse.blobId, chunkSha1))
            }

            totalUploaded += Int64(thisChunkSize)
            progress?(totalUploaded, fileSize)
        }

        #if canImport(os)
        logger.info("Delta upload: reused \(reusedChunks)/\(chunkBlobIds.count) chunks")
        #endif

        if chunkBlobIds.count == 1 {
            return BlobUploadResponse(
                blobId: chunkBlobIds[0].blobId,
                size: Int(fileSize), type: contentType)
        }

        // Combine chunks via Blob/set (blob2)
        let overallSha1 = overallHashContext.finalize()

        let dataSourceObjects: [[String: AnyCodable]] = chunkBlobIds.map { chunk in
            [
                "blobId": AnyCodable(chunk.blobId),
                "digest:sha": AnyCodable(chunk.sha1),
            ]
        }

        let createObj: [String: AnyCodable] = [
            "data": AnyCodable(dataSourceObjects.map { AnyCodable($0) }),
            "type": AnyCodable(contentType),
            "digest:sha": AnyCodable(overallSha1),
        ]

        let capabilities = [JmapCapability.core, JmapCapability.blob2]

        let combineResponses = try await call([
            JmapMethodCall(
                name: "Blob/set",
                args: [
                    "accountId": AnyCodable(accountId),
                    "create": ["combined": AnyCodable(createObj)],
                ],
                callId: "b0"
            ),
        ], using: capabilities)

        guard let response = combineResponses.first,
              response.count >= 3,
              let argsDict = response[1].dictValue,
              let created = argsDict["created"]?.dictValue,
              let combined = created["combined"]?.dictValue,
              let blobId = combined["id"]?.stringValue ?? combined["blobId"]?.stringValue
        else {
            throw JmapError.serverError("blobUpload", "Failed to combine delta chunks")
        }

        let size = combined["size"]?.intValue ?? Int(fileSize)
        return BlobUploadResponse(blobId: blobId, size: size, type: contentType)
    }

    /// Check whether a blob already exists on the server with the given SHA1 digest.
    ///
    /// Used by `createItem` to detect a successful upload from a prior extension process
    /// that was killed before its `completionHandler` could fire. If the blob is still there
    /// with matching content, we skip the re-upload entirely.
    ///
    /// Returns false (not true) if blob2 capability is absent, or if the network call fails —
    /// the caller falls through to a normal upload in both cases.
    public func blobHasDigest(accountId: String, blobId: String, sha1: String) async throws -> Bool {
        let session = try await sessionManager.session()
        guard session.hasBlob2(accountId: accountId) else { return false }

        let responses = try await call([
            JmapMethodCall(
                name: "Blob/get",
                args: [
                    "accountId": AnyCodable(accountId),
                    "ids": AnyCodable([AnyCodable(blobId)]),
                    "properties": AnyCodable(["id", "digest:sha"].map { AnyCodable($0) }),
                ],
                callId: "b0"
            ),
        ], using: [JmapCapability.core, JmapCapability.blob2])

        guard let response = responses.first,
              response.count >= 3,
              response[2].stringValue == "b0",
              let argsDict = response[1].dictValue,
              let list = argsDict["list"]?.arrayValue,
              let blob = list.first?.dictValue
        else { return false }

        return blob["digest:sha"]?.stringValue == sha1
    }

    /// Download a blob to a temporary file. Returns the temp file URL.
    public func downloadBlob(
        accountId: String,
        blobId: String,
        name: String,
        type: String?,
        destinationDir: URL,
        progress: (@Sendable (Int64, Int64) -> Void)? = nil
    ) async throws -> URL {
        let session = try await sessionManager.session()
        guard let downloadURL = session.downloadURL(
            accountId: accountId, blobId: blobId, name: name, type: type)
        else {
            TrafficLog.shared.log("← ERR download [\(accountId)] could not construct download URL for blobId=\(blobId) name=\(name)")
            throw JmapError.invalidResponse
        }

        var request = URLRequest(url: downloadURL)
        request.setValue("*/*", forHTTPHeaderField: "Accept")

        TrafficLog.shared.log("→ GET download [\(accountId)] \(downloadURL.absoluteString)")

        // Use interactive session for downloads (user-initiated)
        let (tempURL, httpResponse) = try await authorizedDownload(
            request, session: interactiveSession)

        TrafficLog.shared.log("← \(httpResponse.statusCode) download [\(accountId)] Content-Type: \(httpResponse.value(forHTTPHeaderField: "Content-Type") ?? "?") Content-Length: \(httpResponse.value(forHTTPHeaderField: "Content-Length") ?? "chunked")")

        guard httpResponse.statusCode >= 200 && httpResponse.statusCode < 300 else {
            // Read the error body from the downloaded temp file for diagnostics, then clean up.
            let errorBody = (try? Data(contentsOf: tempURL)).flatMap { String(data: $0, encoding: .utf8) }
            try? FileManager.default.removeItem(at: tempURL)
            TrafficLog.shared.log("← \(httpResponse.statusCode) download [\(accountId)] body: \(errorBody?.prefix(500) ?? "(empty)")")
            try checkHTTPStatus(httpResponse, data: errorBody.flatMap { $0.data(using: .utf8) })
            return tempURL // unreachable — checkHTTPStatus always throws for non-2xx
        }

        // Move to destination directory with unique name
        let destURL = destinationDir
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension("download")
        do {
            try FileManager.default.moveItem(at: tempURL, to: destURL)
        } catch {
            try? FileManager.default.removeItem(at: tempURL)
            throw error
        }

        return destURL
    }

    /// Returns true if the server supports Direct HTTP Write for this account.
    /// Requires a live session (may trigger session fetch if not yet cached).
    public func hasDirectWrite(accountId: String) async throws -> Bool {
        let session = try await sessionManager.session()
        return session.hasDirectWrite(accountId: accountId)
    }

    /// Direct HTTP Write: PUT to webWriteUrlTemplate/{id} to replace file content.
    ///
    /// The PUT updates blobId/size/type on the server in one round-trip. Because the
    /// spec does not define a way to carry the modified timestamp in the PUT, this
    /// method follows up with a FileNode/set update to set modified — keeping the
    /// two-step protocol entirely within JmapClient so the caller sees one operation.
    ///
    /// Suitable for files < 16 MB. Returns the new (blobId, size, type).
    public func directWrite(
        accountId: String,
        nodeId: String,
        fileURL: URL,
        contentType: String,
        modified: Date? = nil
    ) async throws -> BlobUploadResponse {
        let session = try await sessionManager.session()
        guard let template = session.webWriteUrlTemplate(accountId: accountId) else {
            throw JmapError.serverError("notSupported", "Server does not support direct HTTP write")
        }

        let urlString = template.replacingOccurrences(of: "{id}", with: nodeId)
        guard let url = URL(string: urlString) else {
            throw JmapError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.setValue(contentType, forHTTPHeaderField: "Content-Type")

        let (data, httpResponse) = try await authorizedUpload(
            request, fromFile: fileURL, session: backgroundSession)
        try checkHTTPStatus(httpResponse, data: data)

        let blob = try decoder.decode(BlobUploadResponse.self, from: data)

        // Set modified separately — the PUT does not carry a timestamp.
        // Send null if no client timestamp so the server stamps it with current time.
        let modifiedField: AnyCodable = modified.map {
            AnyCodable(ISO8601DateFormatter().string(from: $0))
        } ?? AnyCodable(NSNull())
        let tsResponses = try await call([
            JmapMethodCall(name: "FileNode/set", args: [
                "accountId": AnyCodable(accountId),
                "update": [nodeId: AnyCodable(["modified": modifiedField])],
            ], callId: "ts0"),
        ])
        let tsResponse = try extractResponse(FileNodeSetResponse.self, from: tsResponses, callId: "ts0")
        if let error = tsResponse.notUpdated?[nodeId] {
            throw JmapError.serverError(error.type, error.description)
        }

        return blob
    }

    /// Convert a blob using Blob/convert with ImageConvertRecipe.
    /// Returns the converted blob ID.
    public func convertImage(
        accountId: String,
        blobId: String,
        width: Int,
        height: Int,
        mimeType: String = "image/jpeg"
    ) async throws -> String {
        let capabilities = [JmapCapability.core, JmapCapability.blob2]
        let responses = try await call([
            JmapMethodCall(
                name: "Blob/convert",
                args: [
                    "accountId": AnyCodable(accountId),
                    "create": [
                        "t0": [
                            "imageConvert": [
                                "blobId": AnyCodable(blobId),
                                "width": AnyCodable(width),
                                "height": AnyCodable(height),
                                "type": AnyCodable(mimeType),
                                "autoOrient": AnyCodable(true),
                            ],
                        ],
                    ],
                ],
                callId: "b0"
            ),
        ], using: capabilities)

        guard let response = responses.first,
              response.count >= 3,
              let argsDict = response[1].dictValue,
              let created = argsDict["created"]?.dictValue,
              let item = created["t0"]?.dictValue,
              let convertedBlobId = item["id"]?.stringValue ?? item["blobId"]?.stringValue
        else {
            // Check for error
            if let response = responses.first,
               response.count >= 3,
               let argsDict = response[1].dictValue,
               let notCreated = argsDict["notCreated"]?.dictValue,
               let error = notCreated["t0"]?.dictValue {
                let errorType = error["type"]?.stringValue ?? "unknown"
                throw JmapError.serverError(errorType, error["description"]?.stringValue)
            }
            throw JmapError.serverError("blobConvert", "Failed to convert image")
        }

        return convertedBlobId
    }

    /// Record that a file was accessed. The timestamp will be piggybacked
    /// onto the next outgoing JMAP call, or flushed after 5 minutes.
    // MARK: - Quota

    /// Fetches storage quota for the account.
    /// Returns nil if the server does not advertise the quota capability.
    public func fetchQuota(accountId: String) async throws -> QuotaInfo? {
        let session = try await sessionManager.session()
        guard session.capabilities[JmapCapability.quota] != nil else { return nil }

        let responses = try await call([
            JmapMethodCall(
                name: "Quota/get",
                args: ["accountId": AnyCodable(accountId), "ids": AnyCodable(NSNull())],
                callId: "q0"
            ),
        ], using: [JmapCapability.core, JmapCapability.quota])

        let result = try extractResponse(QuotaGetResponse.self, from: responses, callId: "q0")

        // Sum all octets-type quotas to get a single used/limit pair
        let octets = result.list.filter { $0.resourceType == "octets" }
        let totalUsed = octets.reduce(Int64(0)) { $0 + $1.used }
        let totalLimit = octets.compactMap { $0.hardLimit ?? $0.softLimit }.min()
        return QuotaInfo(used: totalUsed, limit: totalLimit)
    }

    public func recordAccess(accountId: String, nodeId: String, at date: Date = Date()) {
        pendingAccessed[nodeId] = date
        accessedAccountId = accountId
        resetAccessedFlushTimer()
    }

    /// Reset the 5-minute fallback timer.
    private func resetAccessedFlushTimer() {
        accessedFlushTask?.cancel()
        accessedFlushTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(300 * 1_000_000_000))
            guard let self = self, !Task.isCancelled else { return }
            await self.flushPendingAccessed()
        }
    }

    /// Flush pending accessed timestamps as a standalone call.
    private func flushPendingAccessed() async {
        guard !pendingAccessed.isEmpty, let accountId = accessedAccountId else { return }
        let batch = pendingAccessed
        pendingAccessed.removeAll()
        do {
            try await batchUpdateAccessed(accountId: accountId, accessed: batch)
        } catch {
            // Re-add on failure so they'll be retried
            for (nodeId, date) in batch {
                if pendingAccessed[nodeId] == nil {
                    pendingAccessed[nodeId] = date
                }
            }
        }
    }

    /// Drain pending accessed timestamps for piggybacking.
    private func drainPendingAccessed() -> [String: Date] {
        guard !pendingAccessed.isEmpty else { return [:] }
        let batch = pendingAccessed
        pendingAccessed.removeAll()
        accessedFlushTask?.cancel()
        return batch
    }

    /// Batch-update accessed timestamps for multiple nodes.
    public func batchUpdateAccessed(
        accountId: String,
        accessed: [String: Date]
    ) async throws {
        if accessed.isEmpty { return }

        let formatter = ISO8601DateFormatter()
        var updateFields: [String: AnyCodable] = [:]
        for (nodeId, time) in accessed {
            updateFields[nodeId] = AnyCodable(["accessed": AnyCodable(formatter.string(from: time))])
        }

        let responses = try await call([
            JmapMethodCall(
                name: "FileNode/set",
                args: [
                    "accountId": AnyCodable(accountId),
                    "update": AnyCodable(updateFields),
                ],
                callId: "s0"
            ),
        ])

        // Best-effort — ignore errors for individual nodes
        _ = try? extractResponse(FileNodeSetResponse.self, from: responses, callId: "s0")
    }

    // MARK: - Private Helpers

    private func authorizedRequest(
        _ request: URLRequest,
        session urlSession: URLSession
    ) async throws -> (Data, HTTPURLResponse) {
        var request = request
        let token = try await getToken()
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await urlSession.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse else {
            throw JmapError.invalidResponse
        }

        if httpResponse.statusCode == 401 {
            // Invalidate token (forces OAuth refresh on next getToken call)
            await tokenInvalidator?()
            await sessionManager.invalidate()
            let newToken = try await getToken()
            request.setValue("Bearer \(newToken)", forHTTPHeaderField: "Authorization")
            let (retryData, retryResponse) = try await urlSession.data(for: request)
            guard let retryHTTP = retryResponse as? HTTPURLResponse else {
                throw JmapError.invalidResponse
            }
            return (retryData, retryHTTP)
        }

        return (data, httpResponse)
    }

    private func authorizedUpload(
        _ request: URLRequest,
        fromFile fileURL: URL,
        session urlSession: URLSession,
        onBytesSent: (@Sendable (Int64, Int64) -> Void)? = nil
    ) async throws -> (Data, HTTPURLResponse) {
        var request = request
        let token = try await getToken()
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        // Always use the delegate path so stall detection fires on every upload,
        // regardless of whether the caller needs progress callbacks.
        return try await ProgressUploader.upload(
            request: request, fromFile: fileURL, session: urlSession,
            onBytesSent: onBytesSent ?? { _, _ in })
    }

    private func authorizedDownload(
        _ request: URLRequest,
        session urlSession: URLSession
    ) async throws -> (URL, HTTPURLResponse) {
        var request = request
        let token = try await getToken()
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await urlSession.download(for: request)
        guard let httpResponse = response as? HTTPURLResponse else {
            throw JmapError.invalidResponse
        }

        if httpResponse.statusCode == 401 {
            try? FileManager.default.removeItem(at: tempURL)
            await tokenInvalidator?()
            await sessionManager.invalidate()
            let newToken = try await getToken()
            request.setValue("Bearer \(newToken)", forHTTPHeaderField: "Authorization")
            let (retryURL, retryResponse) = try await urlSession.download(for: request)
            guard let retryHTTP = retryResponse as? HTTPURLResponse else {
                throw JmapError.invalidResponse
            }
            return (retryURL, retryHTTP)
        }

        return (tempURL, httpResponse)
    }

    /// Get the current auth token.
    private func getToken() async throws -> String {
        return try await tokenGetter()
    }

    /// - Parameter protocolClasses: Custom URL protocol classes for testing (injected into URLSessionConfiguration).
    /// - Parameter requestWillSend: Optional hook called before each request for debug logging.
    /// - Parameter responseDidReceive: Optional hook called after each JMAP API response.
    public init(sessionManager: SessionManager, tokenProvider: TokenProvider,
                protocolClasses: [AnyClass]? = nil,
                temporaryDirectory: URL = FileManager.default.temporaryDirectory,
                backgroundUploader: BackgroundUploader? = nil,
                chunkStore: UploadChunkStore? = nil,
                requestWillSend: (@Sendable (URL, Data) -> Void)? = nil,
                responseDidReceive: (@Sendable (URL, Int, Data) -> Void)? = nil) {
        self.temporaryDirectory = temporaryDirectory
        self.backgroundUploader = backgroundUploader
        self.chunkStore = chunkStore
        self.sessionManager = sessionManager
        self.tokenGetter = { try await tokenProvider.currentToken() }
        if let oauthProvider = tokenProvider as? OAuthTokenProvider {
            self.tokenInvalidator = { await oauthProvider.invalidateAccessToken() }
        } else {
            self.tokenInvalidator = nil
        }
        self.requestWillSend = requestWillSend
        self.responseDidReceive = responseDidReceive

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let dateString = try container.decode(String.self)
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let date = formatter.date(from: dateString) { return date }
            formatter.formatOptions = [.withInternetDateTime]
            if let date = formatter.date(from: dateString) { return date }
            throw DecodingError.dataCorruptedError(
                in: container, debugDescription: "Cannot decode date: \(dateString)")
        }
        self.decoder = decoder

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        self.encoder = encoder

        let interactiveConfig = URLSessionConfiguration.default
        interactiveConfig.httpMaximumConnectionsPerHost = 4
        interactiveConfig.timeoutIntervalForRequest = 30
        if let protocols = protocolClasses {
            interactiveConfig.protocolClasses = protocols
        }
        self.interactiveSession = URLSession(configuration: interactiveConfig)

        let bgConfig = URLSessionConfiguration.default
        bgConfig.httpMaximumConnectionsPerHost = 4
        // No timeoutIntervalForRequest — ProgressUploader's stall detector handles this.
        if let protocols = protocolClasses {
            bgConfig.protocolClasses = protocols
        }
        self.backgroundSession = URLSession(configuration: bgConfig)
    }

    private func checkHTTPStatus(_ response: HTTPURLResponse, data: Data?) throws {
        switch response.statusCode {
        case 200...299:
            return
        case 401:
            throw JmapError.unauthorized
        case 403:
            throw JmapError.forbidden(nil)
        case 413:
            throw JmapError.payloadTooLarge
        case 429:
            throw JmapError.rateLimited
        case 400...499:
            let body = data.flatMap { String(data: $0, encoding: .utf8) }
            throw JmapError.httpError(response.statusCode, body)
        case 500...599:
            let body = data.flatMap { String(data: $0, encoding: .utf8) }
            throw JmapError.httpError(response.statusCode, body)
        default:
            throw JmapError.httpError(response.statusCode, nil)
        }
    }

    /// Extract a typed response from the method responses array by matching callId.
    private func extractResponse<T: Decodable>(
        _ type: T.Type,
        from responses: [[AnyCodable]],
        callId: String
    ) throws -> T {
        for response in responses {
            guard response.count >= 3,
                  response[2].stringValue == callId
            else { continue }

            // Check for error response
            if let methodName = response[0].stringValue, methodName == "error" {
                if let errorDict = response[1].dictValue {
                    let errorType = errorDict["type"]?.stringValue ?? "unknown"
                    let description = errorDict["description"]?.stringValue
                    if errorType == "cannotCalculateChanges" {
                        throw JmapError.cannotCalculateChanges
                    }
                    throw JmapError.serverError(errorType, description)
                }
            }

            // Re-encode the args dict and decode as the target type
            let argsData = try encoder.encode(response[1])
            return try decoder.decode(T.self, from: argsData)
        }
        throw JmapError.serverError("missingResponse", "No response for callId: \(callId)")
    }

    /// Special extraction for FileNode/changes which may return cannotCalculateChanges.
    private func extractChangesResponse(
        from responses: [[AnyCodable]],
        callId: String
    ) throws -> FileNodeChangesResponse {
        return try extractResponse(FileNodeChangesResponse.self, from: responses, callId: callId)
    }

    private func buildCapabilities(session: JmapSession) -> [String] {
        var caps = [JmapCapability.core]
        if let fileNodeURI = session.fileNodeCapabilityURI {
            caps.append(fileNodeURI)
        }
        return caps
    }
}

// MARK: - Method Call Builder

/// Represents a single JMAP method call in a batch.
public struct JmapMethodCall: Sendable {
    public let name: String
    public let args: [String: AnyCodable]
    public let callId: String

    public init(name: String, args: [String: AnyCodable], callId: String) {
        self.name = name
        self.args = args
        self.callId = callId
    }

    func asArray() -> [AnyCodable] {
        [AnyCodable(name), AnyCodable(args), AnyCodable(callId)]
    }
}

/// JMAP request body.
struct JmapRequestBody: Encodable {
    let using: [String]
    let methodCalls: [[AnyCodable]]
}

// MARK: - FileNode Extensions

extension FileNode {
    /// The standard properties we request from FileNode/get.
    public static let standardProperties: [AnyCodable] = [
        "id", "parentId", "blobId", "name", "type",
        "size", "created", "modified", "accessed", "role",
        "executable", "isSubscribed", "myRights", "shareWith",
    ].map { AnyCodable($0) }
}
