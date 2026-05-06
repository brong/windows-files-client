import FileProvider
import UniformTypeIdentifiers
import JmapClient

/// Represents a single item (file or folder) in the FileProvider domain.
/// Constructed from a cached `NodeCacheEntry` and its server nodeId.
public final class FileProviderItem: NSObject, NSFileProviderItem {
    private let nodeId: String
    private let entry: NodeCacheEntry
    private let homeNodeId: String
    private let trashNodeId: String?
    private let pinned: Bool
    private let syntheticTrash: Bool
    /// Overrides the displayed filename when two siblings are case-insensitively identical
    /// (e.g., "FOO.txt" displayed as "FOO (2).txt" so both appear in Finder).
    /// The real name stored on the server is always `entry.name`.
    private let filenameOverride: String?

    public init(nodeId: String, entry: NodeCacheEntry,
                homeNodeId: String, trashNodeId: String?,
                isPinned: Bool = false, isSyntheticTrash: Bool = false,
                filenameOverride: String? = nil) {
        self.nodeId = nodeId
        self.entry = entry
        self.homeNodeId = homeNodeId
        self.trashNodeId = trashNodeId
        self.pinned = isPinned
        self.syntheticTrash = isSyntheticTrash
        self.filenameOverride = filenameOverride
    }

    /// Convenience init from a FileNode.
    public convenience init(node: FileNode, homeNodeId: String, trashNodeId: String?,
                            isPinned: Bool = false) {
        let entry = NodeCacheEntry(
            parentId: node.parentId,
            name: node.name ?? "Untitled",
            blobId: node.blobId,
            size: node.size ?? 0,
            modified: node.modified,
            isFolder: node.isFolder,
            type: node.type,
            myRights: node.myRights
        )
        self.init(nodeId: node.id, entry: entry, homeNodeId: homeNodeId,
                  trashNodeId: trashNodeId, isPinned: isPinned)
    }

    // MARK: - NSFileProviderItem

    public var itemIdentifier: NSFileProviderItemIdentifier {
        if syntheticTrash { return .trashContainer }
        if nodeId == homeNodeId { return .rootContainer }
        if let trashId = trashNodeId, nodeId == trashId { return .trashContainer }
        return NSFileProviderItemIdentifier(nodeId)
    }

    public var parentItemIdentifier: NSFileProviderItemIdentifier {
        guard let parentId = entry.parentId else {
            return .rootContainer
        }
        if parentId == homeNodeId {
            return .rootContainer
        }
        if let trashId = trashNodeId, parentId == trashId {
            return .trashContainer
        }
        return NSFileProviderItemIdentifier(parentId)
    }

    public var filename: String {
        sanitizeFilename(filenameOverride ?? entry.name)
    }

    public var contentType: UTType {
        if entry.isFolder {
            return .folder
        }
        if let mimeType = entry.type, let utType = UTType(mimeType: mimeType) {
            return utType
        }
        // Infer from filename extension
        let ext = (entry.name as NSString).pathExtension
        if !ext.isEmpty, let utType = UTType(filenameExtension: ext) {
            return utType
        }
        return .data
    }

    public var capabilities: NSFileProviderItemCapabilities {
        var caps: NSFileProviderItemCapabilities = [.allowsReading, .allowsEvicting]

        let rights = entry.myRights ?? FileNodeRights()
        if rights.mayWrite {
            caps.insert(.allowsWriting)
            caps.insert(.allowsRenaming)
            caps.insert(.allowsReparenting)
            caps.insert(.allowsTrashing)
            caps.insert(.allowsDeleting)
        }

        return caps
    }

    public var itemVersion: NSFileProviderItemVersion {
        // contentVersion: blobId (changes when file content changes)
        // metadataVersion: name + parentId hash (changes on rename/move)
        let contentData = (entry.blobId ?? "folder").data(using: .utf8) ?? Data()
        // Use the displayed filename (not entry.name) so that adding/removing a
        // case-collision suffix registers as a metadata change.
        let metaString = "\(filename):\(entry.parentId ?? "root")"
        let metaData = metaString.data(using: .utf8) ?? Data()
        return NSFileProviderItemVersion(contentVersion: contentData, metadataVersion: metaData)
    }

    public var documentSize: NSNumber? {
        entry.isFolder ? nil : NSNumber(value: entry.size)
    }

    /// Pinned items stay downloaded regardless of storage pressure (Finder shows
    /// "Keep Downloaded" / "Remove from Everywhere" in the context menu).
    /// Unpinned items are eligible for eviction when storage is needed.
    public var contentPolicy: NSFileProviderContentPolicy {
        pinned ? .downloadEagerlyAndKeepDownloaded : .downloadLazily
    }

    public var creationDate: Date? {
        // We don't store creation date in the cache currently.
        // Could be added if needed.
        nil
    }

    public var contentModificationDate: Date? {
        entry.modified
    }

    private func sanitizeFilename(_ name: String) -> String {
        FilenameUtils.sanitize(name)
    }
}
