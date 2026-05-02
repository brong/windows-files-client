import Foundation
import Testing
@testable import JmapClient

// MARK: - Mock URL Protocol

/// A URLProtocol subclass that returns canned responses for testing.
final class MockURLProtocol: URLProtocol, @unchecked Sendable {
    // Thread-safe handler storage
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (Data, HTTPURLResponse))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let handler = Self.handler else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }
        do {
            let (data, response) = try handler(request)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}
}

// MARK: - Test Helpers

private let testSessionJSON = """
{
    "apiUrl": "https://api.example.com/jmap/",
    "downloadUrl": "https://api.example.com/jmap/download/{accountId}/{blobId}/{name}?type={type}",
    "uploadUrl": "https://api.example.com/jmap/upload/{accountId}/",
    "eventSourceUrl": "https://api.example.com/jmap/eventsource/",
    "capabilities": {
        "urn:ietf:params:jmap:core": {},
        "https://www.fastmail.com/dev/filenode": {}
    },
    "accounts": {
        "u123": {
            "name": "test@example.com",
            "isPersonal": true,
            "accountCapabilities": {
                "https://www.fastmail.com/dev/filenode": {}
            }
        }
    },
    "primaryAccounts": {
        "https://www.fastmail.com/dev/filenode": "u123"
    }
}
"""

/// Read the full POST body from a URLRequest, handling both httpBody and httpBodyStream.
private func readRequestBody(_ request: URLRequest) -> String {
    if let body = request.httpBody {
        return String(data: body, encoding: .utf8) ?? ""
    }
    guard let stream = request.httpBodyStream else { return "" }
    stream.open()
    defer { stream.close() }
    var data = Data()
    var buffer = [UInt8](repeating: 0, count: 4096)
    while stream.hasBytesAvailable {
        let n = stream.read(&buffer, maxLength: buffer.count)
        if n > 0 { data.append(contentsOf: buffer[..<n]) }
    }
    return String(data: data, encoding: .utf8) ?? ""
}

private let testSessionWithQuota = """
{
    "apiUrl": "https://api.example.com/jmap/",
    "downloadUrl": "https://api.example.com/jmap/download/{accountId}/{blobId}/{name}",
    "uploadUrl": "https://api.example.com/jmap/upload/{accountId}/",
    "eventSourceUrl": "https://api.example.com/jmap/eventsource/",
    "capabilities": {
        "urn:ietf:params:jmap:core": {},
        "https://www.fastmail.com/dev/filenode": {},
        "urn:ietf:params:jmap:quota": {}
    },
    "accounts": {
        "u123": {
            "name": "test@example.com",
            "isPersonal": true,
            "accountCapabilities": {
                "https://www.fastmail.com/dev/filenode": {},
                "urn:ietf:params:jmap:quota": {}
            }
        }
    },
    "primaryAccounts": {
        "https://www.fastmail.com/dev/filenode": "u123"
    }
}
"""

private func makeTestClient() -> JmapClient {
    let sessionURL = URL(string: "https://api.example.com/jmap/session")!
    let tokenProvider = StaticTokenProvider(token: "test-token")
    let sessionManager = SessionManager(
        sessionURL: sessionURL, tokenProvider: tokenProvider,
        protocolClasses: [MockURLProtocol.self])
    return JmapClient(
        sessionManager: sessionManager,
        tokenProvider: tokenProvider,
        protocolClasses: [MockURLProtocol.self]
    )
}

private func jsonResponse(url: URL, json: String, statusCode: Int = 200) -> (Data, HTTPURLResponse) {
    let data = json.data(using: .utf8)!
    let response = HTTPURLResponse(url: url, statusCode: statusCode, httpVersion: "HTTP/1.1", headerFields: nil)!
    return (data, response)
}

// MARK: - Tests

extension NetworkTests {
@Suite struct JmapClientAPITests {

    @Test func findHomeNode() async throws {
        let client = makeTestClient()

        var requestCount = 0
        MockURLProtocol.handler = { request in
            requestCount += 1
            let url = request.url!

            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }

            // Verify auth header is set
            let auth = request.value(forHTTPHeaderField: "Authorization")
            #expect(auth == "Bearer test-token")

            let responseJSON = """
            {
                "methodResponses": [
                    ["FileNode/query", {"accountId":"u123","queryState":"q1","ids":["M-home"],"total":1}, "q0"],
                    ["FileNode/get", {"accountId":"u123","state":"s1","list":[{"id":"M-home","parentId":null,"blobId":null,"name":"Home","type":null,"size":null,"created":null,"modified":null,"role":"home","myRights":{"mayRead":true,"mayModifyContent":true,"mayShare":false}}],"notFound":[]}, "g0"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        let home = try await client.findHomeNode(accountId: "u123")
        #expect(home.id == "M-home")
        #expect(home.name == "Home")
        #expect(home.isHome == true)
        #expect(home.isFolder == true)
        #expect(requestCount == 2)
    }

    @Test func findTrashNodeReturnsNil() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }
            let responseJSON = """
            {
                "methodResponses": [
                    ["FileNode/query", {"accountId":"u123","queryState":"q1","ids":[],"total":0}, "q0"],
                    ["FileNode/get", {"accountId":"u123","state":"s1","list":[],"notFound":[]}, "g0"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        let trash = try await client.findTrashNode(accountId: "u123")
        #expect(trash == nil)
    }

    @Test func createNode() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }

            let responseJSON = """
            {
                "methodResponses": [
                    ["FileNode/set", {
                        "accountId": "u123",
                        "oldState": "s1",
                        "newState": "s2",
                        "created": {
                            "c0": {"id":"M-new","parentId":"M-home","blobId":null,"name":"NewFolder","type":null,"size":null,"created":null,"modified":null,"role":null,"myRights":{"mayRead":true,"mayModifyContent":true,"mayShare":false}}
                        }
                    }, "s0"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        let node = try await client.createNode(
            accountId: "u123", parentId: "M-home", name: "NewFolder")
        #expect(node.id == "M-new")
        #expect(node.name == "NewFolder")
        #expect(node.isFolder == true)
    }

    @Test func createNodeForbidden() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }
            let responseJSON = """
            {
                "methodResponses": [
                    ["FileNode/set", {
                        "accountId": "u123",
                        "oldState": "s1",
                        "newState": "s1",
                        "notCreated": {
                            "c0": {"type":"forbidden","description":"Read-only share"}
                        }
                    }, "s0"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        do {
            _ = try await client.createNode(
                accountId: "u123", parentId: "M-readonly", name: "test")
            #expect(Bool(false), "Should have thrown")
        } catch let error as JmapError {
            if case .forbidden = error {
                // expected
            } else {
                #expect(Bool(false), "Expected forbidden, got \(error)")
            }
        }
    }

    @Test func getChanges() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }
            let responseJSON = """
            {
                "methodResponses": [
                    ["FileNode/changes", {
                        "accountId": "u123",
                        "oldState": "s1",
                        "newState": "s2",
                        "created": ["M-new1"],
                        "updated": ["M-existing"],
                        "destroyed": ["M-gone"],
                        "hasMoreChanges": false
                    }, "c0"],
                    ["FileNode/get", {
                        "accountId": "u123",
                        "state": "s2",
                        "list": [{"id":"M-new1","parentId":"M-home","blobId":"B1","name":"new.txt","type":"text/plain","size":100,"created":null,"modified":null,"role":null,"myRights":null}],
                        "notFound": []
                    }, "g-created"],
                    ["FileNode/get", {
                        "accountId": "u123",
                        "state": "s2",
                        "list": [{"id":"M-existing","parentId":"M-home","blobId":"B2","name":"updated.txt","type":"text/plain","size":200,"created":null,"modified":null,"role":null,"myRights":null}],
                        "notFound": []
                    }, "g-updated"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        let result = try await client.getChanges(accountId: "u123", sinceState: "s1")
        #expect(result.changes.oldState == "s1")
        #expect(result.changes.newState == "s2")
        #expect(result.changes.destroyed == ["M-gone"])
        #expect(result.created.count == 1)
        #expect(result.created[0].name == "new.txt")
        #expect(result.updated.count == 1)
        #expect(result.updated[0].name == "updated.txt")
    }

    @Test func cannotCalculateChanges() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }
            let responseJSON = """
            {
                "methodResponses": [
                    ["error", {"type":"cannotCalculateChanges"}, "c0"],
                    ["error", {"type":"cannotCalculateChanges"}, "g-created"],
                    ["error", {"type":"cannotCalculateChanges"}, "g-updated"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        do {
            _ = try await client.getChanges(accountId: "u123", sinceState: "old-state")
            #expect(Bool(false), "Should have thrown")
        } catch let error as JmapError {
            if case .cannotCalculateChanges = error {
                // expected
            } else {
                #expect(Bool(false), "Expected cannotCalculateChanges, got \(error)")
            }
        }
    }

    @Test func destroyNodeIdempotent() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }
            let responseJSON = """
            {
                "methodResponses": [
                    ["FileNode/set", {
                        "accountId": "u123",
                        "oldState": "s1",
                        "newState": "s1",
                        "notDestroyed": {
                            "M-gone": {"type":"notFound","description":"Node not found"}
                        }
                    }, "s0"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        // Should not throw — notFound on destroy is idempotent
        try await client.destroyNode(accountId: "u123", nodeId: "M-gone")
    }

    // MARK: - updateNodeContent (v10 blobId mutation)

    @Test func updateNodeContentSendsBlobIdUpdate() async throws {
        let client = makeTestClient()

        nonisolated(unsafe) var capturedBody = ""
        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }
            capturedBody = readRequestBody(request)
            return jsonResponse(url: url, json: """
            {"methodResponses":[
                ["FileNode/set",{"accountId":"u123","oldState":"s1","newState":"s2",
                 "updated":{"N1":null}},"s0"]
            ],"sessionState":"ss1"}
            """)
        }

        try await client.updateNodeContent(accountId: "u123", nodeId: "N1", blobId: "B-new")

        // v10 fix: must be an update, not a destroy+create
        #expect(capturedBody.contains("\"blobId\""))
        #expect(!capturedBody.contains("\"destroy\""))
        #expect(capturedBody.contains("\"update\""))
    }

    @Test func updateNodeContentThrowsNotFound() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") { return jsonResponse(url: url, json: testSessionJSON) }
            return jsonResponse(url: url, json: """
            {"methodResponses":[
                ["FileNode/set",{"accountId":"u123","oldState":"s1","newState":"s1",
                 "notUpdated":{"N1":{"type":"notFound","description":"not found"}}},"s0"]
            ],"sessionState":"ss1"}
            """)
        }

        do {
            try await client.updateNodeContent(accountId: "u123", nodeId: "N1", blobId: "B-new")
            Issue.record("Expected notFound error")
        } catch JmapError.notFound { /* expected */ }
    }

    // MARK: - updateNode (rename / move)

    @Test func updateNodeRename() async throws {
        let client = makeTestClient()

        nonisolated(unsafe) var capturedBody = ""
        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") { return jsonResponse(url: url, json: testSessionJSON) }
            capturedBody = readRequestBody(request)
            return jsonResponse(url: url, json: """
            {"methodResponses":[
                ["FileNode/set",{"accountId":"u123","oldState":"s1","newState":"s2",
                 "updated":{"N1":null}},"s0"]
            ],"sessionState":"ss1"}
            """)
        }

        try await client.updateNode(accountId: "u123", nodeId: "N1", name: "renamed.txt")

        #expect(capturedBody.contains("\"name\""))
        #expect(capturedBody.contains("renamed.txt"))
    }

    @Test func updateNodeMove() async throws {
        let client = makeTestClient()

        nonisolated(unsafe) var capturedBody = ""
        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") { return jsonResponse(url: url, json: testSessionJSON) }
            capturedBody = readRequestBody(request)
            return jsonResponse(url: url, json: """
            {"methodResponses":[
                ["FileNode/set",{"accountId":"u123","oldState":"s1","newState":"s2",
                 "updated":{"N1":null}},"s0"]
            ],"sessionState":"ss1"}
            """)
        }

        try await client.updateNode(accountId: "u123", nodeId: "N1", parentId: "P-new")

        #expect(capturedBody.contains("\"parentId\""))
        #expect(capturedBody.contains("P-new"))
    }

    // MARK: - destroyNode (success path)

    @Test func destroyNodeSuccess() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") { return jsonResponse(url: url, json: testSessionJSON) }
            return jsonResponse(url: url, json: """
            {"methodResponses":[
                ["FileNode/set",{"accountId":"u123","oldState":"s1","newState":"s2",
                 "destroyed":["N1"]},"s0"]
            ],"sessionState":"ss1"}
            """)
        }

        // Should complete without throwing
        try await client.destroyNode(accountId: "u123", nodeId: "N1")
    }

    // MARK: - fetchQuota

    @Test func fetchQuotaReturnsNilWhenCapabilityAbsent() async throws {
        let client = makeTestClient()
        // testSessionJSON has no quota capability — fetchQuota should short-circuit
        MockURLProtocol.handler = { request in
            jsonResponse(url: request.url!, json: testSessionJSON)
        }
        defer { MockURLProtocol.handler = nil }

        let result = try await client.fetchQuota(accountId: "u123")
        #expect(result == nil)
    }

    @Test func fetchQuotaSumsOctetsQuota() async throws {
        let client = makeTestClient()

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") { return jsonResponse(url: url, json: testSessionWithQuota) }
            return jsonResponse(url: url, json: """
            {"methodResponses":[
                ["Quota/get",{"accountId":"u123","state":"q1","list":[
                    {"id":"Q1","name":"Storage","resourceType":"octets","used":1000,"hardLimit":5000},
                    {"id":"Q2","name":"Messages","resourceType":"count","used":50,"hardLimit":100}
                ],"notFound":[]},"q0"]
            ],"sessionState":"ss1"}
            """)
        }

        let quota = try await client.fetchQuota(accountId: "u123")
        #expect(quota != nil)
        #expect(quota?.used == 1000)       // only octets type summed
        #expect(quota?.limit == 5000)
    }

    @Test func http401TriggersRetry() async throws {
        let client = makeTestClient()

        var callCount = 0
        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return jsonResponse(url: url, json: testSessionJSON)
            }

            callCount += 1
            if callCount == 1 {
                return jsonResponse(url: url, json: "{}", statusCode: 401)
            }
            let responseJSON = """
            {
                "methodResponses": [
                    ["FileNode/query", {"accountId":"u123","queryState":"q1","ids":["M-home"],"total":1}, "q0"],
                    ["FileNode/get", {"accountId":"u123","state":"s1","list":[{"id":"M-home","parentId":null,"blobId":null,"name":"Home","type":null,"size":null,"created":null,"modified":null,"role":"home","myRights":null}],"notFound":[]}, "g0"]
                ],
                "sessionState": "ss1"
            }
            """
            return jsonResponse(url: url, json: responseJSON)
        }

        let home = try await client.findHomeNode(accountId: "u123")
        #expect(home.id == "M-home")
        #expect(callCount == 2)
    }
}
}
