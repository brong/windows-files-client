import Foundation

extension FileNode {
    /// Group `nodes` by `parentId` and return those reachable from `homeId`, in
    /// BFS order (each parent before its children). Nodes not reachable from home
    /// — orphans, detached subtrees — are excluded, matching a per-folder BFS that
    /// only descends from home.
    ///
    /// This lets the client build its whole tree from a single bulk fetch
    /// (`queryAllNodeIds` + `getNodes`) instead of one network round trip per
    /// folder level. Cycle-safe via a visited set.
    public static func reachableFromHome(_ nodes: [FileNode], homeId: String) -> [FileNode] {
        var childrenByParent: [String: [FileNode]] = [:]
        for node in nodes {
            guard let parentId = node.parentId else { continue }
            childrenByParent[parentId, default: []].append(node)
        }

        var result: [FileNode] = []
        var queue: [String] = [homeId]
        var visited: Set<String> = [homeId]
        while !queue.isEmpty {
            let parentId = queue.removeFirst()
            for child in childrenByParent[parentId] ?? [] {
                guard visited.insert(child.id).inserted else { continue }  // cycle/dupe guard
                result.append(child)
                if child.isFolder { queue.append(child.id) }
            }
        }
        return result
    }
}
