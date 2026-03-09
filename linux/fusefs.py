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

        # Write buffers: inode → bytearray (accumulated writes before flush)
        self._write_buffers: dict[int, bytearray] = {}

        # Track which inodes are open for writing (inode → node_id or None for new files)
        self._open_writes: dict[int, str | None] = {}

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

    def _add_node(self, node: FileNode):
        """Add a node to the tree indexes."""
        self._nodes[node.id] = node
        self._get_or_assign_inode(node.id)
        if node.parent_id:
            children = self._children[node.parent_id]
            if node.id not in children:
                children.append(node.id)

    def _remove_node(self, node_id: str):
        """Remove a node from the tree indexes."""
        node = self._nodes.pop(node_id, None)
        if node and node.parent_id:
            children = self._children.get(node.parent_id, [])
            if node_id in children:
                children.remove(node_id)
        # Clean up inode mapping
        ino = self._node_id_to_inode.pop(node_id, None)
        if ino is not None:
            self._inode_to_node_id.pop(ino, None)
        # Clean up caches
        self._blob_cache.pop(node_id, None)

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

    # -- FUSE read operations --

    async def getattr(self, inode, ctx=None):
        # For inodes with pending writes, report the buffer size
        if inode in self._write_buffers:
            node = self._get_node(inode)
            if node is None:
                raise pyfuse3.FUSEError(errno.ENOENT)
            entry = self._make_entry(node)
            entry.st_size = len(self._write_buffers[inode])
            return entry
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

        accmode = flags & os.O_ACCMODE
        if accmode == os.O_RDONLY:
            return pyfuse3.FileInfo(fh=inode)

        # Writing to existing file — load current content into write buffer
        if node.blob_id and node.id not in self._blob_cache:
            try:
                data = await self._jmap.download_blob(node.blob_id)
                self._blob_cache[node.id] = data
            except Exception as e:
                log.error("Download failed for %s: %s", node.name, e)
                raise pyfuse3.FUSEError(errno.EIO)

        if flags & os.O_TRUNC:
            self._write_buffers[inode] = bytearray()
        else:
            existing = self._blob_cache.get(node.id, b"")
            self._write_buffers[inode] = bytearray(existing)

        self._open_writes[inode] = node.id
        return pyfuse3.FileInfo(fh=inode)

    async def read(self, fh, offset, length):
        # If there's a write buffer, read from it
        if fh in self._write_buffers:
            data = self._write_buffers[fh]
            return bytes(data[offset:offset + length])

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

    # -- FUSE write operations --

    async def write(self, fh, offset, buf):
        if fh not in self._write_buffers:
            raise pyfuse3.FUSEError(errno.EBADF)
        wbuf = self._write_buffers[fh]
        end = offset + len(buf)
        if end > len(wbuf):
            wbuf.extend(b"\x00" * (end - len(wbuf)))
        wbuf[offset:end] = buf
        return len(buf)

    async def create(self, parent_inode, name, mode, flags, ctx):
        """Create a new file."""
        name_str = name.decode("utf-8") if isinstance(name, bytes) else name
        parent_node_id = self._inode_to_node_id.get(parent_inode)
        if parent_node_id is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        # Check for duplicate name
        for child_id in self._children.get(parent_node_id, []):
            child = self._nodes.get(child_id)
            if child and child.name == name_str:
                raise pyfuse3.FUSEError(errno.EEXIST)

        # Create a placeholder node locally; we'll upload on release
        # Use a temporary ID until the server assigns a real one
        temp_id = f"_tmp_{self._next_inode}"
        placeholder = FileNode({
            "id": temp_id,
            "parentId": parent_node_id,
            "name": name_str,
            "blobId": "",  # non-None so is_folder returns False
            "size": 0,
        })
        self._add_node(placeholder)
        ino = self._get_or_assign_inode(temp_id)

        self._write_buffers[ino] = bytearray()
        self._open_writes[ino] = None  # None = new file, not yet on server

        entry = self._make_entry(placeholder)
        return pyfuse3.FileInfo(fh=ino), entry

    async def release(self, fh):
        """Flush writes to the server on file close."""
        if fh not in self._write_buffers:
            return

        wbuf = self._write_buffers.pop(fh)
        existing_node_id = self._open_writes.pop(fh, None)
        node = self._get_node(fh)

        if node is None:
            log.error("release: no node for inode %d", fh)
            return

        data = bytes(wbuf)
        parent_id = node.parent_id
        name = node.name

        try:
            if existing_node_id is None:
                # New file — create on server
                log.info("Uploading new file: %s (%d bytes)", name, len(data))
                new_node = await self._jmap.create_file(parent_id, name, data)
                # Replace the temp node with the real one
                self._remove_node(node.id)
                self._add_node(new_node)
                # Re-map the inode to the new real node_id
                self._inode_to_node_id[fh] = new_node.id
                self._node_id_to_inode[new_node.id] = fh
                self._blob_cache[new_node.id] = data
            else:
                # Existing file — replace on server
                log.info("Replacing file: %s (%d bytes)", name, len(data))
                new_node = await self._jmap.replace_file(
                    existing_node_id, parent_id, name, data)
                self._remove_node(existing_node_id)
                self._add_node(new_node)
                self._inode_to_node_id[fh] = new_node.id
                self._node_id_to_inode[new_node.id] = fh
                self._blob_cache[new_node.id] = data
        except Exception as e:
            log.error("Failed to upload %s: %s", name, e)
            # Keep the local state so the user doesn't lose data
            # but the file won't be on the server

    async def mkdir(self, parent_inode, name, mode, ctx):
        """Create a new directory."""
        name_str = name.decode("utf-8") if isinstance(name, bytes) else name
        parent_node_id = self._inode_to_node_id.get(parent_inode)
        if parent_node_id is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        # Check for duplicate
        for child_id in self._children.get(parent_node_id, []):
            child = self._nodes.get(child_id)
            if child and child.name == name_str:
                raise pyfuse3.FUSEError(errno.EEXIST)

        try:
            new_node = await self._jmap.create_folder(parent_node_id, name_str)
            log.info("Created folder: %s (id=%s)", name_str, new_node.id)
        except Exception as e:
            log.error("Failed to create folder %s: %s", name_str, e)
            raise pyfuse3.FUSEError(errno.EIO)

        self._add_node(new_node)
        return self._make_entry(new_node)

    async def unlink(self, parent_inode, name, ctx):
        """Delete a file."""
        name_str = name.decode("utf-8") if isinstance(name, bytes) else name
        parent_node_id = self._inode_to_node_id.get(parent_inode)
        if parent_node_id is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        target = None
        for child_id in self._children.get(parent_node_id, []):
            child = self._nodes.get(child_id)
            if child and child.name == name_str:
                target = child
                break
        if target is None:
            raise pyfuse3.FUSEError(errno.ENOENT)
        if target.is_folder:
            raise pyfuse3.FUSEError(errno.EISDIR)

        try:
            await self._jmap.destroy_node(target.id)
            log.info("Deleted file: %s (id=%s)", name_str, target.id)
        except Exception as e:
            log.error("Failed to delete %s: %s", name_str, e)
            raise pyfuse3.FUSEError(errno.EIO)

        self._remove_node(target.id)

    async def rmdir(self, parent_inode, name, ctx):
        """Delete a directory."""
        name_str = name.decode("utf-8") if isinstance(name, bytes) else name
        parent_node_id = self._inode_to_node_id.get(parent_inode)
        if parent_node_id is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        target = None
        for child_id in self._children.get(parent_node_id, []):
            child = self._nodes.get(child_id)
            if child and child.name == name_str:
                target = child
                break
        if target is None:
            raise pyfuse3.FUSEError(errno.ENOENT)
        if not target.is_folder:
            raise pyfuse3.FUSEError(errno.ENOTDIR)

        # Check if empty (POSIX rmdir requires empty)
        if self._children.get(target.id):
            raise pyfuse3.FUSEError(errno.ENOTEMPTY)

        try:
            await self._jmap.destroy_node(target.id)
            log.info("Deleted folder: %s (id=%s)", name_str, target.id)
        except Exception as e:
            log.error("Failed to delete folder %s: %s", name_str, e)
            raise pyfuse3.FUSEError(errno.EIO)

        self._remove_node(target.id)

    async def rename(self, old_parent_inode, old_name, new_parent_inode, new_name, flags, ctx):
        """Rename/move a file or directory."""
        old_name_str = old_name.decode("utf-8") if isinstance(old_name, bytes) else old_name
        new_name_str = new_name.decode("utf-8") if isinstance(new_name, bytes) else new_name

        old_parent_id = self._inode_to_node_id.get(old_parent_inode)
        new_parent_id = self._inode_to_node_id.get(new_parent_inode)
        if old_parent_id is None or new_parent_id is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        # Find source
        source = None
        for child_id in self._children.get(old_parent_id, []):
            child = self._nodes.get(child_id)
            if child and child.name == old_name_str:
                source = child
                break
        if source is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        # Check if target exists — if so, remove it (POSIX rename replaces)
        for child_id in self._children.get(new_parent_id, []):
            child = self._nodes.get(child_id)
            if child and child.name == new_name_str and child.id != source.id:
                try:
                    await self._jmap.destroy_node(child.id, remove_children=child.is_folder)
                except Exception as e:
                    log.error("Failed to remove target %s: %s", new_name_str, e)
                    raise pyfuse3.FUSEError(errno.EIO)
                self._remove_node(child.id)
                break

        # Build update
        updates = {}
        if new_name_str != old_name_str:
            updates["name"] = new_name_str
        if new_parent_id != old_parent_id:
            updates["parentId"] = new_parent_id

        if updates:
            try:
                await self._jmap.update_node(source.id, **updates)
                log.info("Renamed: %s → %s (id=%s)", old_name_str, new_name_str, source.id)
            except Exception as e:
                log.error("Failed to rename %s: %s", old_name_str, e)
                raise pyfuse3.FUSEError(errno.EIO)

            # Update local tree
            if old_parent_id != new_parent_id:
                children = self._children.get(old_parent_id, [])
                if source.id in children:
                    children.remove(source.id)
                self._children[new_parent_id].append(source.id)
                source.parent_id = new_parent_id
            if new_name_str != old_name_str:
                source.name = new_name_str

    async def setattr(self, inode, attr, fields, fh, ctx):
        """Handle truncate and other attribute changes."""
        node = self._get_node(inode)
        if node is None:
            raise pyfuse3.FUSEError(errno.ENOENT)

        # Handle truncate (e.g. open with O_TRUNC or ftruncate)
        if fields.update_size:
            new_size = attr.st_size
            if inode in self._write_buffers:
                wbuf = self._write_buffers[inode]
                if new_size < len(wbuf):
                    del wbuf[new_size:]
                else:
                    wbuf.extend(b"\x00" * (new_size - len(wbuf)))
            elif new_size == 0 and not node.is_folder:
                # Truncate to zero — start a write buffer
                self._write_buffers[inode] = bytearray()
                self._open_writes[inode] = node.id

        return self._make_entry(node)

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
