import Foundation
import Testing
@testable import JmapClient

private func makeSyncClient() -> JmapClient {
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

private func syncJsonResponse(url: URL, json: String) -> (Data, HTTPURLResponse) {
    let data = json.data(using: .utf8)!
    let response = HTTPURLResponse(
        url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: nil)!
    return (data, response)
}

private let syncTestSession = """
{
    "apiUrl": "https://api.example.com/jmap/",
    "downloadUrl": "https://api.example.com/jmap/download/{accountId}/{blobId}/{name}",
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
            "accountCapabilities": { "https://www.fastmail.com/dev/filenode": {} }
        }
    },
    "primaryAccounts": { "https://www.fastmail.com/dev/filenode": "u123" }
}
"""

private func makeTmpDB() throws -> (NodeDatabase, URL) {
    let tmp = FileManager.default.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
    return (NodeDatabase(containerURL: tmp, accountId: "u123"), tmp)
}

extension NetworkTests {
@Suite struct SyncEngineTests {

    // MARK: - resolveSpecialNodes

    @Test func resolveSpecialNodesReadsFromDatabaseIfKnown() async throws {
        let (db, _) = try makeTmpDB()
        await db.setHomeNodeId("home-cached")
        await db.setTrashNodeId("trash-cached")


        let engine = SyncEngine(client: makeSyncClient(), database: db, accountId: "u123")
        let (homeId, trashId) = try await engine.resolveSpecialNodes()

        #expect(homeId == "home-cached")
        #expect(trashId == "trash-cached")
    }

    @Test func resolveSpecialNodesFetchesFromServerWhenUnknown() async throws {
        let (db, _) = try makeTmpDB()

        // httpBody isn't accessible inside URLProtocol.startLoading — use a call counter instead.
        nonisolated(unsafe) var jmapCallCount = 0
        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return syncJsonResponse(url: url, json: syncTestSession)
            }
            jmapCallCount += 1
            if jmapCallCount == 1 {
                return syncJsonResponse(url: url, json: """
                {"methodResponses":[
                    ["FileNode/query",{"accountId":"u123","queryState":"q1","ids":["M-home"],"total":1},"q0"],
                    ["FileNode/get",{"accountId":"u123","state":"s1","list":[
                        {"id":"M-home","parentId":null,"blobId":null,"name":"Home",
                         "type":null,"size":null,"created":null,"modified":null,
                         "role":"home","myRights":{"mayRead":true,"mayWrite":true,"mayShare":false}}
                    ],"notFound":[]},"g0"]],"sessionState":"ss1"}
                """)
            } else {
                return syncJsonResponse(url: url, json: """
                {"methodResponses":[
                    ["FileNode/query",{"accountId":"u123","queryState":"q1","ids":[],"total":0},"q0"],
                    ["FileNode/get",{"accountId":"u123","state":"s1","list":[],"notFound":[]},"g0"]],"sessionState":"ss1"}
                """)
            }
        }
        defer { MockURLProtocol.handler = nil }

        let engine = SyncEngine(client: makeSyncClient(), database: db, accountId: "u123")
        let (homeId, trashId) = try await engine.resolveSpecialNodes()

        #expect(homeId == "M-home")
        #expect(trashId == nil)
        #expect(await db.homeNodeId == "M-home")
    }

    // MARK: - fetchChanges

    @Test func fetchChangesReturnsEmptyWhenNoStateToken() async throws {
        let (db, _) = try makeTmpDB()

        let engine = SyncEngine(client: makeSyncClient(), database: db, accountId: "u123")
        let (updated, deleted) = try await engine.fetchChanges()

        #expect(updated.isEmpty)
        #expect(deleted.isEmpty)
    }

    @Test func fetchChangesUpdatesDBAndReturnsNodes() async throws {
        let (db, _) = try makeTmpDB()
        await db.setStateToken("state-old")

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return syncJsonResponse(url: url, json: syncTestSession)
            }
            return syncJsonResponse(url: url, json: """
            {"methodResponses":[
                ["FileNode/changes",{"accountId":"u123","oldState":"state-old","newState":"state-new",
                    "hasMoreChanges":false,"created":["N1"],"updated":[],"destroyed":["N-old"]},"c0"],
                ["FileNode/get",{"accountId":"u123","state":"state-new","list":[
                    {"id":"N1","parentId":"M-home","blobId":"B1","name":"new.txt",
                     "type":"text/plain","size":42,"created":null,"modified":null,
                     "role":null,"myRights":{"mayRead":true,"mayWrite":true,"mayShare":false}}
                ],"notFound":[]},"g-created"],
                ["FileNode/get",{"accountId":"u123","state":"state-new","list":[],"notFound":[]},"g-updated"]
            ],"sessionState":"ss1"}
            """)
        }
        defer { MockURLProtocol.handler = nil }

        let engine = SyncEngine(client: makeSyncClient(), database: db, accountId: "u123")
        let (updated, deleted) = try await engine.fetchChanges()

        #expect(updated.count == 1)
        #expect(updated[0].id == "N1")
        #expect(deleted == ["N-old"])
        #expect(await db.stateToken == "state-new")
        #expect(await db.entry(for: "N1")?.name == "new.txt")
    }

    @Test func fetchChangesThrowsCannotCalculateChanges() async throws {
        let (db, _) = try makeTmpDB()
        await db.setStateToken("state-expired")

        MockURLProtocol.handler = { request in
            let url = request.url!
            if url.path.contains("session") {
                return syncJsonResponse(url: url, json: syncTestSession)
            }
            return syncJsonResponse(url: url, json: """
            {"methodResponses":[
                ["error",{"type":"cannotCalculateChanges"},"c0"],
                ["error",{"type":"cannotCalculateChanges"},"g-created"],
                ["error",{"type":"cannotCalculateChanges"},"g-updated"]
            ],"sessionState":"ss1"}
            """)
        }
        defer { MockURLProtocol.handler = nil }

        let engine = SyncEngine(client: makeSyncClient(), database: db, accountId: "u123")
        do {
            _ = try await engine.fetchChanges()
            Issue.record("Expected cannotCalculateChanges to be thrown")
        } catch JmapError.cannotCalculateChanges {
            // expected
        }
    }
}
}
