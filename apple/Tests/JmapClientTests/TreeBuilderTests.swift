import Foundation
import Testing
@testable import JmapClient

private func fn(_ id: String, parent: String?, folder: Bool) -> FileNode {
    FileNode(id: id, parentId: parent, blobId: folder ? nil : "blob-\(id)",
             name: id, type: nil, size: nil, created: nil, modified: nil, accessed: nil,
             role: nil, executable: nil, isSubscribed: nil, myRights: nil, shareWith: nil)
}

@Test func reachableFromHomeBuildsTreeAndExcludesOrphans() {
    let nodes = [
        fn("A", parent: "home", folder: true),
        fn("A1", parent: "A", folder: false),
        fn("B", parent: "home", folder: false),
        fn("X", parent: "ghost", folder: false),   // orphan: parent not reachable
    ]
    let result = FileNode.reachableFromHome(nodes, homeId: "home")
    let ids = result.map { $0.id }

    #expect(Set(ids) == ["A", "A1", "B"])                       // orphan X excluded
    #expect(ids.firstIndex(of: "A")! < ids.firstIndex(of: "A1")!)  // parent before child
}

@Test func reachableFromHomeIsCycleSafe() {
    // A → B → A cycle plus a normal child; must terminate and not duplicate.
    let nodes = [
        fn("A", parent: "home", folder: true),
        fn("B", parent: "A", folder: true),
        fn("loop", parent: "B", folder: true),   // points back via data error below
        fn("C", parent: "A", folder: false),
    ]
    // Force a cycle: make "A" also a child of "loop".
    let cyclic = nodes + [fn("A", parent: "loop", folder: true)]
    let result = FileNode.reachableFromHome(cyclic, homeId: "home")
    // Terminates; each id appears at most once.
    #expect(result.count == Set(result.map { $0.id }).count)
    #expect(Set(result.map { $0.id }) == ["A", "B", "loop", "C"])
}
