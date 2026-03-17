import Foundation
import Testing
@testable import JmapClient
@testable import FuseMount

// MARK: - Path resolution tests (no FUSE or network needed)

@Suite struct PathResolutionTests {

    func makeFS() -> FileNodeFuseFS {
        let sessionURL = URL(string: "https://example.com/jmap/session")!
        let tokenProvider = StaticTokenProvider(token: "test")
        let sessionManager = SessionManager(sessionURL: sessionURL, tokenProvider: tokenProvider)
        let jmapClient = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider)
        let tempBase = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuse-test-\(UUID().uuidString)")
        let fs = FileNodeFuseFS(
            jmapClient: jmapClient, sessionManager: sessionManager,
            accountId: "u1", stateDir: tempBase.appendingPathComponent("state"),
            blobCacheDir: tempBase.appendingPathComponent("blobs"))

        // Build a tree:
        //   home/
        //     Documents/
        //       report.txt
        //     photo.jpg
        fs.homeNodeId = "M-home"
        fs.nodes["M-home"] = .init(
            nodeId: "M-home", parentId: nil, name: "Home", blobId: nil,
            size: 0, isFolder: true, type: nil, created: nil, modified: nil, mayWrite: true)
        fs.nodes["M-docs"] = .init(
            nodeId: "M-docs", parentId: "M-home", name: "Documents", blobId: nil,
            size: 0, isFolder: true, type: nil, created: nil, modified: nil, mayWrite: true)
        fs.nodes["M-report"] = .init(
            nodeId: "M-report", parentId: "M-docs", name: "report.txt", blobId: "B1",
            size: 1024, isFolder: false, type: "text/plain", created: nil, modified: nil, mayWrite: true)
        fs.nodes["M-photo"] = .init(
            nodeId: "M-photo", parentId: "M-home", name: "photo.jpg", blobId: "B2",
            size: 50000, isFolder: false, type: "image/jpeg", created: nil, modified: nil, mayWrite: false)

        fs.children["M-home"] = ["M-docs", "M-photo"]
        fs.children["M-docs"] = ["M-report"]

        return fs
    }

    @Test func resolveRoot() {
        let fs = makeFS()
        #expect(fs.resolveNodeId(path: "/") == "M-home")
    }

    @Test func resolveFile() {
        let fs = makeFS()
        #expect(fs.resolveNodeId(path: "/photo.jpg") == "M-photo")
    }

    @Test func resolveNested() {
        let fs = makeFS()
        #expect(fs.resolveNodeId(path: "/Documents/report.txt") == "M-report")
    }

    @Test func resolveFolder() {
        let fs = makeFS()
        #expect(fs.resolveNodeId(path: "/Documents") == "M-docs")
    }

    @Test func resolveNonexistent() {
        let fs = makeFS()
        #expect(fs.resolveNodeId(path: "/nonexistent") == nil)
    }

    @Test func resolveDeepNonexistent() {
        let fs = makeFS()
        #expect(fs.resolveNodeId(path: "/Documents/nope.txt") == nil)
    }

    @Test func fileHandleAllocation() {
        let fs = makeFS()
        fs.lock.lock()
        let fh1 = fs.nextFH
        fs.nextFH += 1
        fs.openFiles[fh1] = "M-photo"
        let fh2 = fs.nextFH
        fs.nextFH += 1
        fs.openFiles[fh2] = "M-report"
        fs.lock.unlock()

        #expect(fh1 != fh2)
        #expect(fs.openFiles[fh1] == "M-photo")
        #expect(fs.openFiles[fh2] == "M-report")
    }

    @Test func writeBufferAccumulation() {
        let fs = makeFS()
        let fh: UInt64 = 1
        fs.writeBuffers[fh] = Data()

        // Simulate writes at different offsets
        let hello = Data("hello".utf8)
        let writeOffset = 0
        if writeOffset + hello.count > fs.writeBuffers[fh]!.count {
            let padding = writeOffset + hello.count - fs.writeBuffers[fh]!.count
            fs.writeBuffers[fh]!.append(Data(count: padding))
        }
        fs.writeBuffers[fh]!.replaceSubrange(writeOffset..<(writeOffset + hello.count), with: hello)

        #expect(fs.writeBuffers[fh]!.count == 5)
        #expect(String(data: fs.writeBuffers[fh]!, encoding: .utf8) == "hello")
    }

    @Test func diskCachePathSanitization() {
        let fs = makeFS()
        let path = fs.diskCachePath(blobId: "abc/def:ghi")
        #expect(path.lastPathComponent == "abc_def_ghi")
    }

    private func makeFSWithSharedDirs() -> (FileNodeFuseFS, URL) {
        let sessionURL = URL(string: "https://example.com/jmap/session")!
        let tokenProvider = StaticTokenProvider(token: "test")
        let sessionManager = SessionManager(sessionURL: sessionURL, tokenProvider: tokenProvider)
        let jmapClient = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider)
        let tempBase = FileManager.default.temporaryDirectory
            .appendingPathComponent("fuse-persist-\(UUID().uuidString)")
        let fs = FileNodeFuseFS(
            jmapClient: jmapClient, sessionManager: sessionManager,
            accountId: "u1", stateDir: tempBase.appendingPathComponent("state"),
            blobCacheDir: tempBase.appendingPathComponent("blobs"))
        return (fs, tempBase)
    }

    private func makeFSAt(tempBase: URL) -> FileNodeFuseFS {
        let sessionURL = URL(string: "https://example.com/jmap/session")!
        let tokenProvider = StaticTokenProvider(token: "test")
        let sessionManager = SessionManager(sessionURL: sessionURL, tokenProvider: tokenProvider)
        let jmapClient = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider)
        return FileNodeFuseFS(
            jmapClient: jmapClient, sessionManager: sessionManager,
            accountId: "u1", stateDir: tempBase.appendingPathComponent("state"),
            blobCacheDir: tempBase.appendingPathComponent("blobs"))
    }

    @Test func saveAndLoadNodeCache() {
        let (fs, tempBase) = makeFSWithSharedDirs()
        // Populate with same tree as makeFS()
        fs.homeNodeId = "M-home"
        fs.nodes["M-home"] = .init(
            nodeId: "M-home", parentId: nil, name: "Home", blobId: nil,
            size: 0, isFolder: true, type: nil, created: nil, modified: nil, mayWrite: true)
        fs.nodes["M-docs"] = .init(
            nodeId: "M-docs", parentId: "M-home", name: "Documents", blobId: nil,
            size: 0, isFolder: true, type: nil, created: nil, modified: nil, mayWrite: true)
        fs.nodes["M-report"] = .init(
            nodeId: "M-report", parentId: "M-docs", name: "report.txt", blobId: "B1",
            size: 1024, isFolder: false, type: "text/plain", created: nil, modified: nil, mayWrite: true)
        fs.children["M-home"] = ["M-docs"]
        fs.children["M-docs"] = ["M-report"]
        fs.stateToken = "state-42"

        fs.saveNodeCache()

        // Load into fresh instance with same dirs
        let fs2 = makeFSAt(tempBase: tempBase)
        let loaded = fs2.loadNodeCache()
        #expect(loaded == true)

        #expect(fs2.homeNodeId == "M-home")
        #expect(fs2.stateToken == "state-42")
        #expect(fs2.resolveNodeId(path: "/") == "M-home")
        #expect(fs2.resolveNodeId(path: "/Documents/report.txt") == "M-report")
        #expect(fs2.nodeCount == 3)
    }

    @Test func loadNodeCacheNonexistent() {
        let (fs, _) = makeFSWithSharedDirs()
        let loaded = fs.loadNodeCache()
        #expect(loaded == false)
    }

    @Test func loadNodeCacheEmptyStateToken() {
        let (fs, tempBase) = makeFSWithSharedDirs()
        // stateToken is "" by default — cache should not be usable
        fs.saveNodeCache()

        let fs2 = makeFSAt(tempBase: tempBase)
        let loaded = fs2.loadNodeCache()
        #expect(loaded == false)
    }
}
