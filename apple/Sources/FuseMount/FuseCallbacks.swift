import Foundation
import CFuseT
import JmapClient

// MARK: - FUSE C Callbacks
//
// These are plain C function pointers (no closures/captures allowed).
// They access the filesystem via the global `gFilesystem` pointer.

func fuseGetattr(_ path: UnsafePointer<CChar>?, _ stbuf: UnsafeMutablePointer<stat>?) -> Int32 {
    guard let path = path, let stbuf = stbuf, let fs = gFilesystem else { return -ENOENT }
    let pathStr = String(cString: path)

    stbuf.pointee = stat()

    fs.lock.lock()
    defer { fs.lock.unlock() }

    guard let nodeId = fs.resolveNodeId(path: pathStr),
          let node = fs.nodes[nodeId] else {
        return -ENOENT
    }

    fillStat(stbuf: stbuf, node: node, childCount: fs.children[nodeId]?.count ?? 0)
    return 0
}

func fuseReaddir(_ path: UnsafePointer<CChar>?, _ buf: UnsafeMutableRawPointer?,
                 _ filler: fuse_fill_dir_t?, _ offset: off_t,
                 _ fi: UnsafeMutablePointer<fuse_file_info>?) -> Int32 {
    guard let path = path, let buf = buf, let filler = filler, let fs = gFilesystem else {
        return -ENOENT
    }
    let pathStr = String(cString: path)

    fs.lock.lock()
    defer { fs.lock.unlock() }

    guard let nodeId = fs.resolveNodeId(path: pathStr),
          let node = fs.nodes[nodeId],
          node.isFolder else {
        return -ENOENT
    }

    // Standard directory entries
    _ = filler(buf, ".", nil, 0)
    _ = filler(buf, "..", nil, 0)

    // Child entries
    if let childIds = fs.children[nodeId] {
        for childId in childIds {
            guard let child = fs.nodes[childId] else { continue }
            child.name.withCString { nameCStr in
                var st = stat()
                fillStat(stbuf: &st, node: child, childCount: fs.children[childId]?.count ?? 0)
                _ = filler(buf, nameCStr, &st, 0)
            }
        }
    }

    return 0
}

func fuseOpen(_ path: UnsafePointer<CChar>?,
              _ fi: UnsafeMutablePointer<fuse_file_info>?) -> Int32 {
    guard let path = path, let fi = fi, let fs = gFilesystem else { return -ENOENT }
    let pathStr = String(cString: path)

    fs.lock.lock()

    guard let nodeId = fs.resolveNodeId(path: pathStr),
          let node = fs.nodes[nodeId],
          !node.isFolder else {
        fs.lock.unlock()
        return -ENOENT
    }

    let fh = fs.nextFH
    fs.nextFH += 1
    fs.openFiles[fh] = nodeId

    let flags = fi.pointee.flags
    let isWrite = (flags & O_WRONLY) != 0 || (flags & O_RDWR) != 0
    let isTrunc = (flags & O_TRUNC) != 0

    if isWrite {
        if isTrunc {
            // Truncate: start with empty buffer
            fs.writeBuffers[fh] = Data()
        } else if let blobId = node.blobId {
            // Writing to existing file — need current content
            if let cached = fs.blobCache[nodeId] {
                fs.writeBuffers[fh] = cached
            } else {
                let blobIdCopy = blobId
                let name = node.name
                let type = node.type
                fs.lock.unlock()
                if let data = fs.hydrateBlob(nodeId: nodeId, blobId: blobIdCopy, name: name, type: type) {
                    fs.lock.lock()
                    fs.blobCache[nodeId] = data
                    fs.writeBuffers[fh] = data
                    fs.lock.unlock()
                } else {
                    fs.lock.lock()
                    fs.writeBuffers[fh] = Data()
                    fs.lock.unlock()
                }
                fi.pointee.fh = fh
                return 0
            }
        } else {
            fs.writeBuffers[fh] = Data()
        }
    }

    fs.lock.unlock()
    fi.pointee.fh = fh
    return 0
}

func fuseRead(_ path: UnsafePointer<CChar>?, _ buf: UnsafeMutablePointer<CChar>?,
              _ size: Int, _ offset: off_t,
              _ fi: UnsafeMutablePointer<fuse_file_info>?) -> Int32 {
    guard let buf = buf, let fi = fi, let fs = gFilesystem else { return -EIO }
    let fh = fi.pointee.fh

    fs.lock.lock()
    guard let nodeId = fs.openFiles[fh],
          let node = fs.nodes[nodeId] else {
        fs.lock.unlock()
        return -EBADF
    }

    // Check write buffer first (reading back what was written)
    if let writeData = fs.writeBuffers[fh] {
        fs.lock.unlock()
        let start = Int(offset)
        guard start < writeData.count else { return 0 }
        let end = min(start + size, writeData.count)
        let count = end - start
        writeData.withUnsafeBytes { rawBuf in
            let srcPtr = rawBuf.baseAddress!.advanced(by: start)
            buf.update(from: srcPtr.assumingMemoryBound(to: CChar.self), count: count)
        }
        return Int32(count)
    }

    // Check blob cache
    if let cached = fs.blobCache[nodeId] {
        fs.lock.unlock()
        let start = Int(offset)
        guard start < cached.count else { return 0 }
        let end = min(start + size, cached.count)
        let count = end - start
        cached.withUnsafeBytes { rawBuf in
            let srcPtr = rawBuf.baseAddress!.advanced(by: start)
            buf.update(from: srcPtr.assumingMemoryBound(to: CChar.self), count: count)
        }
        return Int32(count)
    }

    // Need to hydrate
    guard let blobId = node.blobId else {
        fs.lock.unlock()
        return 0 // Empty file
    }

    let name = node.name
    let type = node.type
    fs.lock.unlock()

    guard let data = fs.hydrateBlob(nodeId: nodeId, blobId: blobId, name: name, type: type) else {
        return -EIO
    }

    fs.lock.lock()
    fs.blobCache[nodeId] = data
    fs.lock.unlock()

    let start = Int(offset)
    guard start < data.count else { return 0 }
    let end = min(start + size, data.count)
    let count = end - start
    data.withUnsafeBytes { rawBuf in
        let srcPtr = rawBuf.baseAddress!.advanced(by: start)
        buf.update(from: srcPtr.assumingMemoryBound(to: CChar.self), count: count)
    }
    return Int32(count)
}

func fuseWrite(_ path: UnsafePointer<CChar>?, _ buf: UnsafePointer<CChar>?,
               _ size: Int, _ offset: off_t,
               _ fi: UnsafeMutablePointer<fuse_file_info>?) -> Int32 {
    guard let buf = buf, let fi = fi, let fs = gFilesystem else { return -EIO }
    let fh = fi.pointee.fh

    fs.lock.lock()
    defer { fs.lock.unlock() }

    guard fs.openFiles[fh] != nil else { return -EBADF }

    if fs.writeBuffers[fh] == nil {
        fs.writeBuffers[fh] = Data()
    }

    let writeOffset = Int(offset)
    let data = Data(bytes: buf, count: size)

    // Extend buffer if needed
    if writeOffset + size > fs.writeBuffers[fh]!.count {
        let padding = writeOffset + size - fs.writeBuffers[fh]!.count
        fs.writeBuffers[fh]!.append(Data(count: padding))
    }

    // Write data at offset
    fs.writeBuffers[fh]!.replaceSubrange(writeOffset..<(writeOffset + size), with: data)

    return Int32(size)
}

func fuseRelease(_ path: UnsafePointer<CChar>?,
                 _ fi: UnsafeMutablePointer<fuse_file_info>?) -> Int32 {
    guard let fi = fi, let fs = gFilesystem else { return 0 }
    let fh = fi.pointee.fh

    fs.lock.lock()
    guard let nodeId = fs.openFiles.removeValue(forKey: fh) else {
        fs.lock.unlock()
        return 0
    }

    let writeData = fs.writeBuffers.removeValue(forKey: fh)

    guard let data = writeData else {
        // Read-only open, nothing to flush
        fs.lock.unlock()
        return 0
    }

    // Flush write buffer to server
    guard let node = fs.nodes[nodeId] else {
        fs.lock.unlock()
        return -EIO
    }

    let parentId = node.parentId ?? fs.homeNodeId
    let name = node.name
    // _tmp_ IDs are local-only (from fuseCreate), don't try to destroy on server
    let existingServerNodeId = nodeId.hasPrefix("_tmp_") ? nil : nodeId

    fs.lock.unlock()

    if let newNode = fs.uploadAndReplace(parentId: parentId, name: name,
                                          data: data, existingNodeId: existingServerNodeId) {
        fs.lock.lock()
        if newNode.id != nodeId {
            // New node ID (new file creation) — remove old, add new
            if let old = fs.nodes.removeValue(forKey: nodeId),
               let pid = old.parentId {
                fs.children[pid]?.removeAll { $0 == nodeId }
            }
            fs.blobCache.removeValue(forKey: nodeId)
            let effectiveParentId = newNode.parentId ?? parentId
            if !effectiveParentId.isEmpty,
               !(fs.children[effectiveParentId]?.contains(newNode.id) ?? false) {
                fs.children[effectiveParentId, default: []].append(newNode.id)
            }
        } else {
            // Same node ID (blobId update) — just update in place
            fs.blobCache.removeValue(forKey: nodeId)
        }

        let entry = FileNodeFuseFS.NodeEntry(
            nodeId: newNode.id, parentId: newNode.parentId ?? parentId,
            name: newNode.name ?? name, blobId: newNode.blobId,
            size: newNode.size ?? data.count, isFolder: false,
            type: newNode.type, created: newNode.created,
            modified: newNode.modified, mayWrite: newNode.myRights?.mayWrite ?? true
        )
        fs.nodes[newNode.id] = entry
        fs.blobCache[newNode.id] = data
        if let bid = newNode.blobId {
            fs.writeDiskCache(blobId: bid, data: data)
        }
        fs.lock.unlock()
    }
    // If upload fails, data is lost — acceptable for MVP

    return 0
}

func fuseCreate(_ path: UnsafePointer<CChar>?, _ mode: mode_t,
                _ fi: UnsafeMutablePointer<fuse_file_info>?) -> Int32 {
    guard let path = path, let fi = fi, let fs = gFilesystem else { return -EIO }
    let pathStr = String(cString: path)

    let components = pathStr.split(separator: "/", omittingEmptySubsequences: true)
    guard !components.isEmpty else { return -EINVAL }
    let name = String(components.last!)
    let parentPath = "/" + components.dropLast().joined(separator: "/")

    fs.lock.lock()
    guard let parentId = fs.resolveNodeId(path: parentPath) else {
        fs.lock.unlock()
        return -ENOENT
    }

    // Check if a node with this name already exists — if so, reuse it (truncate)
    var existingId: String?
    if let childIds = fs.children[parentId] {
        for childId in childIds {
            if let child = fs.nodes[childId], child.name == name {
                existingId = childId
                break
            }
        }
    }

    let fh = fs.nextFH
    fs.nextFH += 1

    if let nodeId = existingId {
        // Reuse existing node — open with truncated write buffer
        fs.openFiles[fh] = nodeId
        fs.writeBuffers[fh] = Data()
    } else {
        // Create a temporary in-memory node
        let tempId = "_tmp_\(fh)"
        let entry = FileNodeFuseFS.NodeEntry(
            nodeId: tempId, parentId: parentId, name: name, blobId: nil,
            size: 0, isFolder: false, type: nil, created: Date(), modified: Date(),
            mayWrite: true
        )
        fs.nodes[tempId] = entry
        fs.children[parentId, default: []].append(tempId)
        fs.openFiles[fh] = tempId
        fs.writeBuffers[fh] = Data()
    }

    fs.lock.unlock()

    fi.pointee.fh = fh
    return 0
}

func fuseMkdir(_ path: UnsafePointer<CChar>?, _ mode: mode_t) -> Int32 {
    guard let path = path, let fs = gFilesystem else { return -EIO }
    let pathStr = String(cString: path)

    let components = pathStr.split(separator: "/", omittingEmptySubsequences: true)
    guard !components.isEmpty else { return -EINVAL }
    let name = String(components.last!)
    let parentPath = "/" + components.dropLast().joined(separator: "/")

    fs.lock.lock()
    guard let parentId = fs.resolveNodeId(path: parentPath) else {
        fs.lock.unlock()
        return -ENOENT
    }
    fs.lock.unlock()

    guard let newNode = fs.createFolderSync(parentId: parentId, name: name) else {
        return -EIO
    }

    fs.lock.lock()
    let entry = FileNodeFuseFS.NodeEntry(
        nodeId: newNode.id, parentId: newNode.parentId ?? parentId,
        name: newNode.name ?? name,
        blobId: nil, size: 0, isFolder: true, type: nil,
        created: newNode.created, modified: newNode.modified,
        mayWrite: newNode.myRights?.mayWrite ?? true
    )
    fs.nodes[newNode.id] = entry
    let mkdirParentId = newNode.parentId ?? parentId
    if !mkdirParentId.isEmpty {
        fs.children[mkdirParentId, default: []].append(newNode.id)
    }
    fs.lock.unlock()

    return 0
}

func fuseUnlink(_ path: UnsafePointer<CChar>?) -> Int32 {
    guard let path = path, let fs = gFilesystem else { return -EIO }
    let pathStr = String(cString: path)

    fs.lock.lock()
    guard let nodeId = fs.resolveNodeId(path: pathStr),
          let node = fs.nodes[nodeId],
          !node.isFolder else {
        fs.lock.unlock()
        return -ENOENT
    }
    fs.lock.unlock()

    // Only destroy on server if it's a real server node (not a local _tmp_ node)
    if !nodeId.hasPrefix("_tmp_") {
        guard fs.destroySync(nodeId: nodeId) else { return -EIO }
    }

    fs.lock.lock()
    if let pid = node.parentId {
        fs.children[pid]?.removeAll { $0 == nodeId }
    }
    fs.nodes.removeValue(forKey: nodeId)
    fs.blobCache.removeValue(forKey: nodeId)
    fs.lock.unlock()

    return 0
}

func fuseRmdir(_ path: UnsafePointer<CChar>?) -> Int32 {
    guard let path = path, let fs = gFilesystem else { return -EIO }
    let pathStr = String(cString: path)

    fs.lock.lock()
    guard let nodeId = fs.resolveNodeId(path: pathStr),
          let node = fs.nodes[nodeId],
          node.isFolder else {
        fs.lock.unlock()
        return -ENOENT
    }
    // Check if empty
    if let kids = fs.children[nodeId], !kids.isEmpty {
        fs.lock.unlock()
        return -ENOTEMPTY
    }
    fs.lock.unlock()

    if !nodeId.hasPrefix("_tmp_") {
        guard fs.destroySync(nodeId: nodeId) else { return -EIO }
    }

    fs.lock.lock()
    if let pid = node.parentId {
        fs.children[pid]?.removeAll { $0 == nodeId }
    }
    fs.nodes.removeValue(forKey: nodeId)
    fs.children.removeValue(forKey: nodeId)
    fs.lock.unlock()

    return 0
}

func fuseRename(_ from: UnsafePointer<CChar>?, _ to: UnsafePointer<CChar>?) -> Int32 {
    guard let from = from, let to = to, let fs = gFilesystem else { return -EIO }
    let fromPath = String(cString: from)
    let toPath = String(cString: to)

    let toComponents = toPath.split(separator: "/", omittingEmptySubsequences: true)
    guard !toComponents.isEmpty else { return -EINVAL }
    let newName = String(toComponents.last!)
    let newParentPath = "/" + toComponents.dropLast().joined(separator: "/")

    fs.lock.lock()
    guard let nodeId = fs.resolveNodeId(path: fromPath),
          let node = fs.nodes[nodeId],
          let newParentId = fs.resolveNodeId(path: newParentPath) else {
        fs.lock.unlock()
        return -ENOENT
    }

    let oldParentId = node.parentId
    let nameChanged = node.name != newName
    let parentChanged = oldParentId != newParentId
    fs.lock.unlock()

    // Only rename on server for real server nodes
    if !nodeId.hasPrefix("_tmp_") {
        guard fs.renameSync(
            nodeId: nodeId,
            newParentId: parentChanged ? newParentId : nil,
            newName: nameChanged ? newName : nil
        ) else {
            return -EIO
        }
    }

    fs.lock.lock()
    fs.nodes[nodeId]?.name = newName
    fs.nodes[nodeId]?.modified = Date()
    if parentChanged {
        if let oldPid = oldParentId {
            fs.children[oldPid]?.removeAll { $0 == nodeId }
        }
        fs.children[newParentId, default: []].append(nodeId)
        // Update parentId — but NodeEntry has let parentId, so rebuild
        if let entry = fs.nodes[nodeId] {
            let newEntry = FileNodeFuseFS.NodeEntry(
                nodeId: entry.nodeId, parentId: newParentId, name: newName,
                blobId: entry.blobId, size: entry.size, isFolder: entry.isFolder,
                type: entry.type, created: entry.created, modified: Date(),
                mayWrite: entry.mayWrite
            )
            fs.nodes[nodeId] = newEntry
        }
    }
    fs.lock.unlock()

    return 0
}

func fuseTruncate(_ path: UnsafePointer<CChar>?, _ size: off_t) -> Int32 {
    guard let path = path, let fs = gFilesystem else { return -EIO }
    let pathStr = String(cString: path)

    fs.lock.lock()
    guard let nodeId = fs.resolveNodeId(path: pathStr),
          let node = fs.nodes[nodeId],
          !node.isFolder else {
        fs.lock.unlock()
        return -ENOENT
    }
    // Truncate will be handled when the file is opened for write
    // For now, just update the cached size
    fs.nodes[nodeId]?.size = Int(size)
    fs.lock.unlock()
    return 0
}

func fuseStatfs(_ path: UnsafePointer<CChar>?, _ stbuf: UnsafeMutablePointer<statvfs>?) -> Int32 {
    guard let stbuf = stbuf else { return -EIO }
    stbuf.pointee = statvfs()
    stbuf.pointee.f_bsize = 4096
    stbuf.pointee.f_frsize = 4096
    stbuf.pointee.f_blocks = 1024 * 1024 * 10 // ~40 GB
    stbuf.pointee.f_bfree = 1024 * 1024 * 9
    stbuf.pointee.f_bavail = 1024 * 1024 * 9
    stbuf.pointee.f_namemax = 255
    return 0
}

// MARK: - Helpers

private func fillStat(stbuf: UnsafeMutablePointer<stat>,
                      node: FileNodeFuseFS.NodeEntry, childCount: Int) {
    if node.isFolder {
        stbuf.pointee.st_mode = S_IFDIR | 0o755
        stbuf.pointee.st_nlink = UInt16(2 + childCount)
    } else {
        stbuf.pointee.st_mode = S_IFREG | (node.mayWrite ? 0o644 : 0o444)
        stbuf.pointee.st_nlink = 1
        stbuf.pointee.st_size = off_t(node.size)
    }

    if let modified = node.modified {
        let ts = timespec(tv_sec: Int(modified.timeIntervalSince1970), tv_nsec: 0)
        stbuf.pointee.st_mtimespec = ts
        stbuf.pointee.st_atimespec = ts
    }
    if let created = node.created {
        stbuf.pointee.st_ctimespec = timespec(tv_sec: Int(created.timeIntervalSince1970), tv_nsec: 0)
        stbuf.pointee.st_birthtimespec = stbuf.pointee.st_ctimespec
    }
}
