import Foundation
#if canImport(os)
import os
#endif

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

    // Store a reference to the token provider's getter
    nonisolated private let tokenGetter: @Sendable () async throws -> String

    /// Optional hook called with (URL, bodyData) before each JMAP API request.
    /// Used by debug logging to capture body before URLSession converts it to a stream.
    nonisolated public let requestWillSend: (@Sendable (URL, Data) -> Void)?

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

        let effectiveCapabilities = capabilities ?? buildCapabilities(session: session)
        let body = JmapRequestBody(
            using: effectiveCapabilities,
            methodCalls: methodCalls.map { $0.asArray() }
        )

        var request = URLRequest(url: apiURL)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let bodyData = try encoder.encode(body)
        request.httpBody = bodyData
        if let url = request.url { requestWillSend?(url, bodyData) }

        let (data, httpResponse) = try await authorizedRequest(request, session: interactiveSession)
        try checkHTTPStatus(httpResponse, data: data)

        let response = try decoder.decode(JmapResponse.self, from: data)
        return response.methodResponses
    }

    // MARK: - FileNode Operations

    /// Find the home node (role: "home").
    public func findHomeNode(accountId: String) async throws -> FileNode {
        let responses = try await call([
            JmapMethodCall(
                name: "FileNode/query",
                args: [
                    "accountId": AnyCodable(accountId),
                    "filter": ["hasRole": "home"],
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
                    "filter": ["hasRole": "trash"],
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

    /// Fetch incremental changes since a state token.
    /// Returns changes and the full FileNode objects for created/updated items.
    public func getChanges(
        accountId: String,
        sinceState: String
    ) async throws -> (changes: FileNodeChangesResponse, created: [FileNode], updated: [FileNode]) {
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
        onExists: String? = nil
    ) async throws {
        var updateFields: [String: AnyCodable] = [:]
        if let parentId = parentId { updateFields["parentId"] = AnyCodable(parentId) }
        if let name = name { updateFields["name"] = AnyCodable(name) }

        var setArgs: [String: AnyCodable] = [
            "accountId": AnyCodable(accountId),
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

        let fileSize = try FileManager.default.attributesOfItem(
            atPath: fileURL.path
        )[.size] as? Int64 ?? 0

        var request = URLRequest(url: uploadURL)
        request.httpMethod = "POST"
        request.setValue(contentType, forHTTPHeaderField: "Content-Type")

        // Use background session for uploads to avoid starving downloads
        let (data, httpResponse) = try await authorizedUpload(
            request, fromFile: fileURL, session: backgroundSession)
        try checkHTTPStatus(httpResponse, data: data)

        progress?(fileSize, fileSize)

        return try decoder.decode(BlobUploadResponse.self, from: data)
    }

    // MARK: - Chunked Upload

    private static let minChunkSize = 1_048_576        // 1 MB
    private static let maxChunkSize = 67_108_864        // 64 MB
    private static let defaultChunkSize = 67_108_864    // 64 MB
    private static let chunkThreshold = 5_000_000       // 5 MB — files smaller than this skip chunking

    /// Upload a large file in chunks using Blob/upload (RFC 9404) to combine.
    /// Falls back to single upload if Blob capability is not available or file is small.
    public func uploadBlobChunked(
        accountId: String,
        fileURL: URL,
        contentType: String,
        progress: (@Sendable (Int64, Int64) -> Void)? = nil
    ) async throws -> BlobUploadResponse {
        let session = try await sessionManager.session()

        let fileSize = try FileManager.default.attributesOfItem(
            atPath: fileURL.path)[.size] as? Int64 ?? 0

        // Small files or no Blob capability — use simple upload
        guard fileSize > Self.chunkThreshold,
              session.hasBlob(accountId: accountId) else {
            return try await uploadBlob(
                accountId: accountId, fileURL: fileURL,
                contentType: contentType, progress: progress)
        }

        // Determine chunk size
        let blobCap = session.accounts[accountId]?.accountCapabilities[JmapCapability.blob]
        let maxDataSources = blobCap?.dictValue?["maxDataSources"]?.intValue ?? 100
        var chunkSize = Self.defaultChunkSize

        // Adjust chunk size up if file would exceed maxDataSources chunks
        while Int(fileSize) / chunkSize + 1 > maxDataSources && chunkSize < Int(fileSize) {
            chunkSize *= 2
        }
        chunkSize = max(Self.minChunkSize, min(chunkSize, max(Self.maxChunkSize, Int(fileSize))))

        guard let uploadURL = session.uploadURL(accountId: accountId) else {
            throw JmapError.invalidResponse
        }

        let hasBlobExt = session.hasBlobExt(accountId: accountId)

        // Read file and upload chunks
        let fileHandle = try FileHandle(forReadingFrom: fileURL)
        defer { try? fileHandle.close() }

        var chunkBlobIds: [(blobId: String, sha1: String)] = []
        var totalUploaded: Int64 = 0

        // Incremental SHA1 for overall file digest
        var overallHashContext = SHA1Context()

        while totalUploaded < fileSize {
            let remaining = Int(fileSize - totalUploaded)
            let thisChunkSize = min(chunkSize, remaining)

            guard let chunkData = fileHandle.readData(ofLength: thisChunkSize) as Data?,
                  !chunkData.isEmpty else { break }

            // Hash this chunk
            let chunkSha1 = sha1Digest(chunkData)
            overallHashContext.update(chunkData)

            // Upload chunk
            let tempChunkFile = FileManager.default.temporaryDirectory
                .appendingPathComponent(UUID().uuidString)
            try chunkData.write(to: tempChunkFile)
            defer { try? FileManager.default.removeItem(at: tempChunkFile) }

            var request = URLRequest(url: uploadURL)
            request.httpMethod = "POST"
            request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

            let (data, httpResponse) = try await authorizedUpload(
                request, fromFile: tempChunkFile, session: backgroundSession)
            try checkHTTPStatus(httpResponse, data: data)

            let uploadResponse = try decoder.decode(BlobUploadResponse.self, from: data)
            chunkBlobIds.append((uploadResponse.blobId, chunkSha1))

            totalUploaded += Int64(thisChunkSize)
            progress?(totalUploaded, fileSize)
        }

        // If only one chunk, no need to combine
        if chunkBlobIds.count == 1 {
            return BlobUploadResponse(
                blobId: chunkBlobIds[0].blobId,
                size: Int(fileSize), type: contentType)
        }

        // Combine chunks via Blob/upload
        let overallSha1 = overallHashContext.finalize()

        var dataSourceObjects: [[String: AnyCodable]] = []
        for chunk in chunkBlobIds {
            var obj: [String: AnyCodable] = ["blobId": AnyCodable(chunk.blobId)]
            if hasBlobExt {
                obj["digest:sha"] = AnyCodable(chunk.sha1)
            }
            dataSourceObjects.append(obj)
        }

        var createObj: [String: AnyCodable] = [
            "data": AnyCodable(dataSourceObjects.map { AnyCodable($0) }),
            "type": AnyCodable(contentType),
        ]
        if hasBlobExt {
            createObj["digest:sha"] = AnyCodable(overallSha1)
        }

        var capabilities = [JmapCapability.core, JmapCapability.blob]
        if hasBlobExt {
            capabilities.append(JmapCapability.blobExt)
        }

        let responses = try await call([
            JmapMethodCall(
                name: "Blob/upload",
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
              let blobId = combined["blobId"]?.stringValue
        else {
            throw JmapError.serverError("blobUpload", "Failed to combine chunks")
        }

        let size = combined["size"]?.intValue ?? Int(fileSize)
        return BlobUploadResponse(blobId: blobId, size: size, type: contentType)
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
            throw JmapError.invalidResponse
        }

        var request = URLRequest(url: downloadURL)
        request.setValue("application/octet-stream", forHTTPHeaderField: "Accept")

        // Use interactive session for downloads (user-initiated)
        let (tempURL, httpResponse) = try await authorizedDownload(
            request, session: interactiveSession)
        try checkHTTPStatus(httpResponse, data: nil)

        // Move to destination directory with unique name
        let destURL = destinationDir
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension("download")
        try FileManager.default.moveItem(at: tempURL, to: destURL)

        return destURL
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
            // Try refreshing session and retrying once
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
        session urlSession: URLSession
    ) async throws -> (Data, HTTPURLResponse) {
        var request = request
        let token = try await getToken()
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await urlSession.upload(for: request, fromFile: fileURL)
        guard let httpResponse = response as? HTTPURLResponse else {
            throw JmapError.invalidResponse
        }
        return (data, httpResponse)
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
        return (tempURL, httpResponse)
    }

    /// Get the current auth token.
    private func getToken() async throws -> String {
        return try await tokenGetter()
    }

    /// - Parameter protocolClasses: Custom URL protocol classes for testing (injected into URLSessionConfiguration).
    /// - Parameter requestWillSend: Optional hook called before each request for debug logging.
    public init(sessionManager: SessionManager, tokenProvider: TokenProvider,
                protocolClasses: [AnyClass]? = nil,
                requestWillSend: (@Sendable (URL, Data) -> Void)? = nil) {
        self.sessionManager = sessionManager
        self.tokenGetter = { try await tokenProvider.currentToken() }
        self.requestWillSend = requestWillSend

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
        bgConfig.timeoutIntervalForRequest = 120
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
        "size", "created", "modified", "role", "myRights",
    ].map { AnyCodable($0) }
}
