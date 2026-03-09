"""FUSE3 filesystem for JMAP FileNode."""

import errno
import logging
import os
import stat
import time
from collections import defaultdict
from datetime import datetime, timezone

import pyfuse3

from jmap_client import FileNode

log = logging.getLogger("fuse")

# pyfuse3 uses integer inode numbers. We map between inodes and FileNode IDs.
# inode 1 = FUSE root (always), which maps to the home node.
ROOT_INODE = pyfuse3.ROOT_INODE  # 1


class FileNodeFS(pyfuse3.Operations):
    """FUSE filesystem backed by JMAP FileNode."""

    def __init__(self, jmap_client):
        super().__init__()
        self._jmap = jmap_client

        # Bidirectional inode ↔ node_id mapping
        self._inode_to_node_id: dict[int, str] = {}
        self._node_id_to_inode: dict[str, int] = {}
        self._next_inode = 2  # 1 is reserved for root

        # Node data: node_id → FileNode
        self._nodes: dict[str, FileNode] = {}

        # Children: parent_id → [node_id, ...]
        self._children: dict[str, list[str]] = defaultdict(list)

        # Blob cache: node_id → bytes (in-memory, simple LRU would be better)
        self._blob_cache: dict[str, bytes] = {}

        # Home/trash node IDs
        self.home_id: str = ""
        self.trash_id: str | None = None

    def populate(self, nodes: list[FileNode], home_id: str, trash_id: str | None):
        """Build the in-memory tree from a list of FileNode objects."""
        self.home_id = home_id
        self.trash_id = trash_id

        # Index all nodes
        for node in nodes:
            self._nodes[node.id] = node

        # Build children index
        self._children.clear()
        for node in nodes:
            if node.parent_id:
                self._children[node.parent_id].append(node.id)

        # Map root inode to home node
        self._inode_to_node_id[ROOT_INODE] = home_id
        self._node_id_to_inode[home_id] = ROOT_INODE

        # Pre-assign inodes to all nodes (avoids races)
        for node in nodes:
            if node.id != home_id:
                self._get_or_assign_inode(node.id)

        log.info("Populated: %d nodes, %d folders",
                 len(self._nodes),
                 sum(1 for n in self._nodes.values() if n.is_folder))

    def _get_or_assign_inode(self, node_id: str) -> int:
        ino = self._node_id_to_inode.get(node_id)
        if ino is not None:
            return ino
        ino = self._next_inode
        self._next_inode += 1
        self._inode_to_node_id[ino] = node_id
        self._node_id_to_inode[node_id] = ino
        return ino

    def _get_node(self, inode: int) -> FileNode | None:
        node_id = self._inode_to_node_id.get(inode)
        if node_id is None:
            return None
        return self._nodes.get(node_id)

    def _make_entry(self, node: FileNode) -> pyfuse3.EntryAttributes:
        """Build an EntryAttributes struct from a FileNode."""
        ino = self._get_or_assign_inode(node.id)
        entry = pyfuse3.EntryAttributes()
        entry.st_ino = ino
        entry.generation = 0
        entry.entry_timeout = 300
        entry.attr_timeout = 300

        now_ns = int(time.time() * 1e9)
        mtime_ns = _dt_to_ns(node.modified) or now_ns
        ctime_ns = _dt_to_ns(node.created) or mtime_ns

        if node.is_folder:
            entry.st_mode = stat.S_IFDIR | 0o755
            entry.st_size = 0
            entry.st_nlink = 2 + len(self._children.get(node.id, []))
        else:
            entry.st_mode = stat.S_IFREG | 0o644
            entry.st_size = node.size
            entry.st_nlink = 1

        entry.st_uid = os.getuid()
        entry.st_gid = os.getgid()
        entry.st_atime_ns = mtime_ns
        entry.st_mtime_ns = mtime_ns
        entry.st_ctime_ns = ctime_ns
        entry.st_blksize = 4096
        entry.st_blocks = (node.size + 511) // 512 if not node.is_folder else 0

        return entry

    # -- FUSE operations --

    async def getattr(self, inode, ctx=None):
        node = self._get_node(inode)
        if node is None:
            raise pyfuse3.FUSEError(errno.ENOENT)
        return self._make_entry(node)

    async def lookup(self, parent_inode, name, ctx=None):
        name_str = name.decode("utf-8") if isinstance(name, bytes) else name
        parent_node_id = self._inode_to_node_id.get(parent_inode)
        if parent_node_id is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        for child_id in self._children.get(parent_node_id, []):
            child = self._nodes.get(child_id)
            if child and child.name == name_str:
                return self._make_entry(child)

        raise pyfuse3.FUSEError(errno.ENOENT)

    async def opendir(self, inode, ctx):
        node = self._get_node(inode)
        if node is None or not node.is_folder:
            raise pyfuse3.FUSEError(errno.ENOTDIR)
        return inode  # use inode as file handle

    async def readdir(self, fh, start_id, token):
        node_id = self._inode_to_node_id.get(fh)
        if node_id is None:
            return

        children = self._children.get(node_id, [])
        for idx in range(start_id, len(children)):
            child_id = children[idx]
            child = self._nodes.get(child_id)
            if child is None:
                continue
            child_inode = self._get_or_assign_inode(child_id)
            name = child.name.encode("utf-8")
            if not pyfuse3.readdir_reply(token, name, self._make_entry(child), idx + 1):
                break

    async def open(self, inode, flags, ctx):
        node = self._get_node(inode)
        if node is None:
            raise pyfuse3.FUSEError(errno.ENOENT)
        if node.is_folder:
            raise pyfuse3.FUSEError(errno.EISDIR)

        # Read-only for now
        if (flags & os.O_ACCMODE) != os.O_RDONLY:
            raise pyfuse3.FUSEError(errno.EROFS)

        return pyfuse3.FileInfo(fh=inode)

    async def read(self, fh, offset, length):
        node = self._get_node(fh)
        if node is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        if not node.blob_id:
            return b""

        # Hydrate on demand
        if node.id not in self._blob_cache:
            log.info("Hydrating: %s (%s, %d bytes)", node.name, node.id, node.size)
            try:
                data = await self._jmap.download_blob(node.blob_id)
                self._blob_cache[node.id] = data
            except Exception as e:
                log.error("Download failed for %s: %s", node.name, e)
                raise pyfuse3.FUSEError(errno.EIO)

        data = self._blob_cache[node.id]
        return data[offset:offset + length]

    async def release(self, fh):
        pass

    async def releasedir(self, fh):
        pass

    async def statfs(self, ctx):
        sfs = pyfuse3.StatvfsData()
        sfs.f_bsize = 4096
        sfs.f_frsize = 4096
        # Report used space plus generous free space so df looks reasonable
        used_bytes = sum(n.size for n in self._nodes.values() if not n.is_folder)
        used_blocks = max(used_bytes // 4096, 1)
        total_blocks = used_blocks * 10  # pretend 10% used
        sfs.f_blocks = total_blocks
        sfs.f_bfree = total_blocks - used_blocks
        sfs.f_bavail = total_blocks - used_blocks
        sfs.f_files = len(self._nodes) * 10
        sfs.f_ffree = len(self._nodes) * 9
        sfs.f_namemax = 255
        return sfs


def _dt_to_ns(dt: datetime | None) -> int | None:
    """Convert datetime to nanoseconds since epoch."""
    if dt is None:
        return None
    return int(dt.timestamp() * 1e9)
