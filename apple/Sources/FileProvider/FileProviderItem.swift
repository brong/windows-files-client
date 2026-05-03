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

    public init(nodeId: String, entry: NodeCacheEntry, homeNodeId: String, trashNodeId: String?) {
        self.nodeId = nodeId
        self.entry = entry
        self.homeNodeId = homeNodeId
        self.trashNodeId = trashNodeId
    }

    /// Convenience init from a FileNode.
    public convenience init(node: FileNode, homeNodeId: String, trashNodeId: String?) {
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
        self.init(nodeId: node.id, entry: entry, homeNodeId: homeNodeId, trashNodeId: trashNodeId)
    }

    // MARK: - NSFileProviderItem

    public var itemIdentifier: NSFileProviderItemIdentifier {
        if nodeId == homeNodeId {
            return .rootContainer
        }
        if let trashId = trashNodeId, nodeId == trashId {
            return .trashContainer
        }
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
        sanitizeFilename(entry.name)
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
        let metaString = "\(entry.name):\(entry.parentId ?? "root")"
        let metaData = metaString.data(using: .utf8) ?? Data()
        return NSFileProviderItemVersion(contentVersion: contentData, metadataVersion: metaData)
    }

    public var documentSize: NSNumber? {
        entry.isFolder ? nil : NSNumber(value: entry.size)
    }

    /// Files are eligible for eviction (Finder shows "Remove Download" on downloaded files).
    /// Folders use the same policy so their subtree can be evicted recursively.
    public var contentPolicy: NSFileProviderItemContentPolicy {
        .downloadEligibleForEncryptedStorage
    }

    public var creationDate: Date? {
        // We don't store creation date in the cache currently.
        // Could be added if needed.
        nil
    }

    public var contentModificationDate: Date? {
        entry.modified
    }

    // MARK: - Filename Sanitization

    /// Sanitize filenames for macOS/iOS.
    /// Only `/` and `:` are problematic (`:` shows as `/` in Finder due to HFS+ legacy).
    private func sanitizeFilename(_ name: String) -> String {
        var result = name
        result = result.replacingOccurrences(of: "/", with: "\u{2215}") // DIVISION SLASH
        result = result.replacingOccurrences(of: ":", with: "\u{A789}") // MODIFIER LETTER COLON
        return result
    }
}
