import Foundation
import Testing
@testable import JmapClient

@Test func testUpsertAndRetrieve() async {
    let db = await makeDatabase()
    let entry = NodeCacheEntry(
        parentId: "M0", name: "test.txt", blobId: "B1",
        size: 100, modified: Date(), isFolder: false, type: "text/plain"
    )
    await db.upsert(nodeId: "M1", entry: entry)
    let retrieved = await db.entry(for: "M1")
    #expect(retrieved != nil)
    #expect(retrieved?.name == "test.txt")
    #expect(retrieved?.blobId == "B1")
    #expect(retrieved?.size == 100)
    #expect(retrieved?.isFolder == false)
}

@Test func testUpsertFromServer() async {
    let db = await makeDatabase()
    let node = FileNode(
        id: "M1", parentId: "M0", blobId: "B1", name: "photo.jpg",
        type: "image/jpeg", size: 2048, created: nil, modified: Date(), accessed: nil,
        role: nil, executable: nil, isSubscribed: nil,
        myRights: FileNodeRights(mayRead: true, mayWrite: false), shareWith: nil
    )
    await db.upsertFromServer(node)
    let entry = await db.entry(for: "M1")
    #expect(entry?.name == "photo.jpg")
    #expect(entry?.type == "image/jpeg")
    #expect(entry?.size == 2048)
    #expect(entry?.isFolder == false)
    #expect(entry?.myRights?.mayWrite == false)
}

@Test func testUpsertFolderFromServer() async {
    let db = await makeDatabase()
    let folder = FileNode(
        id: "M2", parentId: "M0", blobId: nil, name: "Documents",
        type: nil, size: nil, created: nil, modified: nil, accessed: nil,
        role: nil, executable: nil, isSubscribed: nil, myRights: nil, shareWith: nil
    )
    await db.upsertFromServer(folder)
    let entry = await db.entry(for: "M2")
    #expect(entry?.isFolder == true)
    #expect(entry?.blobId == nil)
}

@Test func testChildren() async {
    let db = await makeDatabase()
    await db.upsert(nodeId: "M1", entry: NodeCacheEntry(parentId: "M0", name: "a.txt", size: 10, isFolder: false))
    await db.upsert(nodeId: "M2", entry: NodeCacheEntry(parentId: "M0", name: "b.txt", size: 20, isFolder: false))
    await db.upsert(nodeId: "M3", entry: NodeCacheEntry(parentId: "M1", name: "c.txt", size: 30, isFolder: false))

    let children = await db.children(of: "M0")
    #expect(children.count == 2)
    let names = Set(children.map { $0.entry.name })
    #expect(names == ["a.txt", "b.txt"])

    let childrenOfM1 = await db.children(of: "M1")
    #expect(childrenOfM1.count == 1)
    #expect(childrenOfM1[0].entry.name == "c.txt")
}

@Test func testRelativePath() async {
    let db = await makeDatabase()
    await db.setHomeNodeId("home")
    await db.upsert(nodeId: "home", entry: NodeCacheEntry(parentId: nil, name: "Home", isFolder: true))
    await db.upsert(nodeId: "M1", entry: NodeCacheEntry(parentId: "home", name: "Documents", isFolder: true))
    await db.upsert(nodeId: "M2", entry: NodeCacheEntry(parentId: "M1", name: "Work", isFolder: true))
    await db.upsert(nodeId: "M3", entry: NodeCacheEntry(parentId: "M2", name: "report.pdf", blobId: "B1", size: 500, isFolder: false))

    let path = await db.relativePath(for: "M3")
    #expect(path == "Documents/Work/report.pdf")

    let folderPath = await db.relativePath(for: "M1")
    #expect(folderPath == "Documents")

    let homePath = await db.relativePath(for: "home")
    #expect(homePath == "")
}

@Test func testRelativePathCycleDetection() async {
    let db = await makeDatabase()
    await db.setHomeNodeId("home")
    await db.upsert(nodeId: "home", entry: NodeCacheEntry(parentId: nil, name: "Home", isFolder: true))
    // Create a cycle: M1 -> M2 -> M1
    await db.upsert(nodeId: "M1", entry: NodeCacheEntry(parentId: "M2", name: "A", isFolder: true))
    await db.upsert(nodeId: "M2", entry: NodeCacheEntry(parentId: "M1", name: "B", isFolder: true))

    let path = await db.relativePath(for: "M1")
    #expect(path == nil) // cycle detected
}

@Test func testRelativePathOrphaned() async {
    let db = await makeDatabase()
    await db.setHomeNodeId("home")
    await db.upsert(nodeId: "home", entry: NodeCacheEntry(parentId: nil, name: "Home", isFolder: true))
    // M1's parent doesn't exist in cache
    await db.upsert(nodeId: "M1", entry: NodeCacheEntry(parentId: "MISSING", name: "orphan.txt", isFolder: false))

    let path = await db.relativePath(for: "M1")
    #expect(path == nil) // can't reach home
}

@Test func testStateToken() async {
    let db = await makeDatabase()
    let initial = await db.stateToken
    #expect(initial == nil)

    await db.setStateToken("abc123")
    let token = await db.stateToken
    #expect(token == "abc123")
}

@Test func testHomeAndTrashNodeIds() async {
    let db = await makeDatabase()
    #expect(await db.homeNodeId == nil)
    #expect(await db.trashNodeId == nil)

    await db.setHomeNodeId("H1")
    await db.setTrashNodeId("T1")
    #expect(await db.homeNodeId == "H1")
    #expect(await db.trashNodeId == "T1")

    await db.setTrashNodeId(nil)
    #expect(await db.trashNodeId == nil)
}

@Test func testRemoveAndCount() async {
    let db = await makeDatabase()
    await db.upsert(nodeId: "M1", entry: NodeCacheEntry(parentId: "M0", name: "a.txt", isFolder: false))
    await db.upsert(nodeId: "M2", entry: NodeCacheEntry(parentId: "M0", name: "b.txt", isFolder: false))
    await db.upsert(nodeId: "M3", entry: NodeCacheEntry(parentId: "M0", name: "c.txt", isFolder: false))
    #expect(await db.count == 3)

    await db.remove(nodeId: "M2")
    #expect(await db.count == 2)
    #expect(await db.entry(for: "M2") == nil)

    await db.remove(nodeIds: ["M1", "M3"])
    #expect(await db.count == 0)
}

@Test func testClearEntries() async {
    let db = await makeDatabase()
    await db.setStateToken("abc")
    await db.upsert(nodeId: "M1", entry: NodeCacheEntry(parentId: "M0", name: "a.txt", isFolder: false))
    await db.clearEntries()
    #expect(await db.count == 0)
    #expect(await db.stateToken == nil)
}

@Test func testSaveAndReload() async throws {
    let tempDir = FileManager.default.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: tempDir) }

    // Write
    let db1 = NodeDatabase(containerURL: tempDir, accountId: "test-account")
    await db1.setHomeNodeId("H1")
    await db1.setTrashNodeId("T1")
    await db1.setStateToken("state123")
    await db1.upsert(nodeId: "M1", entry: NodeCacheEntry(
        parentId: "H1", name: "file.txt", blobId: "B1", size: 42,
        isFolder: false, type: "text/plain"
    ))
    try await db1.save()

    // Reload
    let db2 = NodeDatabase(containerURL: tempDir, accountId: "test-account")
    #expect(await db2.homeNodeId == "H1")
    #expect(await db2.trashNodeId == "T1")
    #expect(await db2.stateToken == "state123")
    #expect(await db2.count == 1)
    let entry = await db2.entry(for: "M1")
    #expect(entry?.name == "file.txt")
    #expect(entry?.blobId == "B1")
    #expect(entry?.size == 42)
}

@Test func testVersionMismatchForcesCleanStart() async throws {
    let tempDir = FileManager.default.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: tempDir) }

    // Write a cache with wrong version
    let cacheDir = tempDir
        .appendingPathComponent("NodeCache", isDirectory: true)
        .appendingPathComponent("test-account", isDirectory: true)
    try FileManager.default.createDirectory(at: cacheDir, withIntermediateDirectories: true)
    let badCache = """
    {"version":999,"homeNodeId":"H1","stateToken":"old","entries":{"M1":{"parentId":"H1","name":"old.txt","size":10,"isFolder":false}}}
    """.data(using: .utf8)!
    try badCache.write(to: cacheDir.appendingPathComponent("nodecache.json"))

    // Load — should discard due to version mismatch
    let db = NodeDatabase(containerURL: tempDir, accountId: "test-account")
    #expect(await db.homeNodeId == nil)
    #expect(await db.stateToken == nil)
    #expect(await db.count == 0)
}

@Test func testDelete() async throws {
    let tempDir = FileManager.default.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: tempDir) }

    let db = NodeDatabase(containerURL: tempDir, accountId: "test-account")
    await db.upsert(nodeId: "M1", entry: NodeCacheEntry(parentId: "M0", name: "a.txt", isFolder: false))
    try await db.save()
    try await db.delete()
    #expect(await db.count == 0)

    // File should be gone
    let cacheFile = tempDir
        .appendingPathComponent("NodeCache")
        .appendingPathComponent("test-account")
        .appendingPathComponent("nodecache.json")
    #expect(!FileManager.default.fileExists(atPath: cacheFile.path))
}

// MARK: - Helpers

private func makeDatabase() -> NodeDatabase {
    let tempDir = FileManager.default.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    return NodeDatabase(containerURL: tempDir, accountId: "test-\(UUID().uuidString)")
}
