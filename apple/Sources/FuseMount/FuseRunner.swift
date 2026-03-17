import Foundation
import CFuseT

/// Builds the fuse_operations struct and runs `fuse_main`.
/// This function blocks until the filesystem is unmounted.
public func runFuseMount(fs: FileNodeFuseFS, mountPoint: String,
                         foreground: Bool = true, allowOther: Bool = false) -> Int32 {
    gFilesystem = fs

    var ops = fuse_operations()
    ops.getattr  = fuseGetattr
    ops.readdir  = fuseReaddir
    ops.open     = fuseOpen
    ops.read     = fuseRead
    ops.write    = fuseWrite
    ops.release  = fuseRelease
    ops.create   = fuseCreate
    ops.mkdir    = fuseMkdir
    ops.unlink   = fuseUnlink
    ops.rmdir    = fuseRmdir
    ops.rename   = fuseRename
    ops.truncate = fuseTruncate
    ops.statfs   = fuseStatfs

    // Build argv for fuse_main
    var args = ["fastmail-files", mountPoint]
    if foreground { args.append("-f") }
    if allowOther { args.append("-oallow_other") }

    // Convert to C argv
    let argc = Int32(args.count)
    let argv = args.map { strdup($0) }
    defer { argv.forEach { free($0) } }

    var cArgv = argv.map { UnsafeMutablePointer<CChar>($0) }

    return cArgv.withUnsafeMutableBufferPointer { buf in
        fuse_main_real(argc, buf.baseAddress!, &ops, MemoryLayout.size(ofValue: ops), nil)
    }
}
