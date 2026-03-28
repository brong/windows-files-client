"""JMAP client for FileNode API, using httpx (works with trio and asyncio)."""

import base64
import hashlib
import logging
import mimetypes
from datetime import datetime, timezone

import httpx

log = logging.getLogger("jmap")

# Capability URIs
CAP_CORE = "urn:ietf:params:jmap:core"
CAP_FILENODE_DEV = "https://www.fastmail.com/dev/filenode"
CAP_FILENODE = "urn:ietf:params:jmap:filenode"
CAP_BLOB = "urn:ietf:params:jmap:blob"


class FileNode:
    """A file or folder from the JMAP FileNode API."""

    __slots__ = (
        "id", "parent_id", "blob_id", "name", "type", "size",
        "created", "modified", "accessed", "role", "my_rights",
        "executable", "is_subscribed", "share_with",
    )

    def __init__(self, data: dict):
        self.id: str = data["id"]
        self.parent_id: str | None = data.get("parentId")
        self.blob_id: str | None = data.get("blobId")
        self.name: str = data.get("name", "")
        self.type: str | None = data.get("type")
        self.size: int = data.get("size") or 0
        self.created: datetime | None = _parse_dt(data.get("created"))
        self.modified: datetime | None = _parse_dt(data.get("modified"))
        self.accessed: datetime | None = _parse_dt(data.get("accessed"))
        self.role: str | None = data.get("role")
        rights = data.get("myRights")
        self.my_rights: dict | None = rights
        self.executable: bool = data.get("executable", False)
        self.is_subscribed: bool = data.get("isSubscribed", True)
        self.share_with: dict | None = data.get("shareWith")

    @property
    def is_folder(self) -> bool:
        return self.blob_id is None


def _parse_dt(val: str | None) -> datetime | None:
    if val is None:
        return None
    try:
        return datetime.fromisoformat(val.replace("Z", "+00:00"))
    except (ValueError, AttributeError):
        return None


class JmapClient:
    """Async JMAP client with session management."""

    def __init__(self, session_url: str, token: str):
        self._session_url = session_url
        self._token = token
        self._http: httpx.AsyncClient | None = None

        # Populated by connect()
        self.api_url: str = ""
        self.upload_url: str = ""
        self.download_url: str = ""
        self.event_source_url: str = ""
        self.account_id: str = ""
        self.filenode_using: str = ""
        self.capabilities: dict = {}
        self.state: str | None = None

    async def connect(self):
        """Fetch JMAP session and discover endpoints."""
        self._http = httpx.AsyncClient(
            headers={"Authorization": f"Bearer {self._token}"},
            timeout=30.0,
            follow_redirects=True,
        )

        resp = await self._http.get(self._session_url)
        resp.raise_for_status()
        session = resp.json()

        self.api_url = session["apiUrl"]
        self.upload_url = session["uploadUrl"]
        self.download_url = session["downloadUrl"]
        self.event_source_url = session.get("eventSourceUrl", "")
        self.capabilities = session.get("capabilities", {})
        self.accounts = session.get("accounts", {})

        # Parse FileNode capability fields
        self.web_write_url_template: str | None = None
        self.web_url_template: str | None = None
        self.web_trash_url: str | None = None

        # Find the FileNode capability and primary account
        primary = session.get("primaryAccounts", {})
        if CAP_FILENODE in primary:
            self.filenode_using = CAP_FILENODE
            self.account_id = primary[CAP_FILENODE]
        elif CAP_FILENODE_DEV in primary:
            self.filenode_using = CAP_FILENODE_DEV
            self.account_id = primary[CAP_FILENODE_DEV]
        else:
            raise RuntimeError("Server does not support FileNode capability")

        # Parse FileNode capability fields from account capabilities
        acct = self.accounts.get(self.account_id, {})
        acct_caps = acct.get("accountCapabilities", {})
        fn_cap = acct_caps.get(self.filenode_using, {})
        self.web_write_url_template = fn_cap.get("webWriteUrlTemplate")
        self.web_url_template = fn_cap.get("webUrlTemplate")
        self.web_trash_url = fn_cap.get("webTrashUrl")

        log.info("Connected: account=%s, capability=%s", self.account_id, self.filenode_using)

    async def close(self):
        if self._http:
            await self._http.aclose()
            self._http = None

    async def _call(self, method_calls: list[list]) -> list:
        """Execute a JMAP request and return methodResponses."""
        body = {"using": [CAP_CORE, self.filenode_using], "methodCalls": method_calls}
        if CAP_BLOB in self.capabilities:
            body["using"].append(CAP_BLOB)

        resp = await self._http.post(self.api_url, json=body)
        resp.raise_for_status()
        return resp.json()["methodResponses"]

    async def find_home_and_trash(self) -> tuple[str, str | None]:
        """Find the home (sync root) and trash node IDs."""
        responses = await self._call([
            ["FileNode/query", {
                "accountId": self.account_id,
                "filter": {"hasRole": "home"},
            }, "home"],
            ["FileNode/query", {
                "accountId": self.account_id,
                "filter": {"hasRole": "trash"},
            }, "trash"],
        ])

        home_id = None
        trash_id = None
        for resp in responses:
            name, result, tag = resp
            if name == "FileNode/query" and tag == "home":
                ids = result.get("ids", [])
                if ids:
                    home_id = ids[0]
            elif name == "FileNode/query" and tag == "trash":
                ids = result.get("ids", [])
                if ids:
                    trash_id = ids[0]

        if not home_id:
            raise RuntimeError("No home node found")
        return home_id, trash_id

    async def get_all_nodes(self) -> tuple[list[FileNode], str]:
        """Fetch all FileNode objects. Returns (nodes, state)."""
        all_ids: list[str] = []
        position = 0
        limit = 4096
        while True:
            responses = await self._call([
                ["FileNode/query", {
                    "accountId": self.account_id,
                    "position": position,
                    "limit": limit,
                }, "q0"],
            ])
            _, result, _ = responses[0]
            ids = result.get("ids", [])
            all_ids.extend(ids)
            total = result.get("total", len(all_ids))
            if len(all_ids) >= total or len(ids) < limit:
                break
            position = len(all_ids)

        log.info("Queried %d node IDs (total=%d)", len(all_ids), total)

        nodes: list[FileNode] = []
        state = ""
        batch_size = 1024
        properties = [
            "id", "parentId", "blobId", "name", "type", "size",
            "created", "modified", "accessed", "role", "myRights",
            "executable", "isSubscribed", "shareWith",
        ]
        for i in range(0, len(all_ids), batch_size):
            batch = all_ids[i:i + batch_size]
            responses = await self._call([
                ["FileNode/get", {
                    "accountId": self.account_id,
                    "ids": batch,
                    "properties": properties,
                }, "g0"],
            ])
            _, result, _ = responses[0]
            state = result.get("state", state)
            for item in result.get("list", []):
                nodes.append(FileNode(item))

        log.info("Fetched %d nodes, state=%s", len(nodes), state)
        self.state = state
        return nodes, state

    async def get_changes(self, since_state: str) -> tuple[list[str], list[str], list[str], str, bool]:
        """Get incremental changes. Returns (created, updated, destroyed, new_state, has_more)."""
        responses = await self._call([
            ["FileNode/changes", {
                "accountId": self.account_id,
                "sinceState": since_state,
            }, "c0"],
        ])
        _, result, _ = responses[0]
        if result.get("type") == "cannotCalculateChanges":
            raise RuntimeError("cannotCalculateChanges")
        return (
            result.get("created", []),
            result.get("updated", []),
            result.get("destroyed", []),
            result.get("newState", since_state),
            result.get("hasMoreChanges", False),
        )

    async def get_nodes_by_id(self, ids: list[str]) -> list[FileNode]:
        """Fetch specific nodes by ID."""
        if not ids:
            return []
        properties = [
            "id", "parentId", "blobId", "name", "type", "size",
            "created", "modified", "accessed", "role", "myRights",
            "executable", "isSubscribed", "shareWith",
        ]
        responses = await self._call([
            ["FileNode/get", {
                "accountId": self.account_id,
                "ids": ids,
                "properties": properties,
            }, "g0"],
        ])
        _, result, _ = responses[0]
        return [FileNode(item) for item in result.get("list", [])]

    async def download_blob(self, blob_id: str) -> bytes:
        """Download a blob by ID. Returns the raw bytes."""
        url = self._blob_url(blob_id)
        resp = await self._http.get(url)
        resp.raise_for_status()
        return resp.content

    async def download_blob_range(self, blob_id: str, offset: int, length: int) -> bytes:
        """Download a byte range of a blob."""
        url = self._blob_url(blob_id)
        headers = {"Range": f"bytes={offset}-{offset + length - 1}"}
        resp = await self._http.get(url, headers=headers)
        resp.raise_for_status()
        return resp.content

    async def upload_blob(self, data: bytes, content_type: str = "application/octet-stream") -> str:
        """Upload a blob. Returns the blobId."""
        url = self.upload_url.replace("{accountId}", self.account_id)
        resp = await self._http.post(url, content=data, headers={"Content-Type": content_type})
        resp.raise_for_status()
        result = resp.json()
        return result["blobId"]

    async def upload_blob_delta(self, data: bytes, content_type: str,
                                old_blob_id: str | None = None) -> str:
        """Upload a blob using delta-aware chunking.

        Queries the server for the old blob's chunk structure, compares
        SHA1 hashes, and only uploads changed chunks. Falls back to full
        upload if no old blob or server doesn't support chunks.
        """
        # No old blob or small file — use simple upload
        if old_blob_id is None or len(data) < 5_000_000:
            return await self.upload_blob(data, content_type)

        # Query server for old blob's chunk structure
        server_chunks = None
        try:
            body = {
                "using": [CAP_CORE, CAP_BLOB],
                "methodCalls": [
                    ["Blob/get", {
                        "accountId": self.account_id,
                        "ids": [old_blob_id],
                        "properties": ["id", "size", "chunks"],
                    }, "b0"],
                ],
            }
            resp = await self._http.post(self.api_url, json=body)
            resp.raise_for_status()
            responses = resp.json()["methodResponses"]
            _, result, _ = responses[0]
            blob_list = result.get("list", [])
            if blob_list and "chunks" in blob_list[0]:
                chunks = blob_list[0]["chunks"]
                if chunks:
                    server_chunks = [
                        (c["blobId"], c["size"], c.get("digest:sha"))
                        for c in chunks
                    ]
        except Exception:
            pass  # Fall back to full upload

        if not server_chunks:
            return await self.upload_blob(data, content_type)

        # Delta upload: compare local chunks against server chunks
        url = self.upload_url.replace("{accountId}", self.account_id)
        chunk_blob_ids: list[tuple[str, str]] = []  # (blobId, sha1_b64)
        overall_hash = hashlib.sha1()
        offset = 0
        reused = 0

        for server_blob_id, server_size, server_digest in server_chunks:
            if offset >= len(data):
                break
            chunk_size = min(server_size, len(data) - offset)
            chunk_data = data[offset:offset + chunk_size]

            local_sha1 = hashlib.sha1(chunk_data).digest()
            local_sha1_b64 = base64.b64encode(local_sha1).decode()
            overall_hash.update(chunk_data)

            if server_digest and local_sha1_b64 == server_digest:
                # Reuse server chunk
                chunk_blob_ids.append((server_blob_id, local_sha1_b64))
                reused += 1
            else:
                # Upload new chunk
                resp = await self._http.post(
                    url, content=chunk_data,
                    headers={"Content-Type": "application/octet-stream"})
                resp.raise_for_status()
                blob_id = resp.json()["blobId"]
                chunk_blob_ids.append((blob_id, local_sha1_b64))

            offset += chunk_size

        # Upload any remaining data beyond server's chunk count
        max_chunk = 64 * 1024 * 1024
        while offset < len(data):
            chunk_size = min(max_chunk, len(data) - offset)
            chunk_data = data[offset:offset + chunk_size]

            local_sha1 = hashlib.sha1(chunk_data).digest()
            local_sha1_b64 = base64.b64encode(local_sha1).decode()
            overall_hash.update(chunk_data)

            resp = await self._http.post(
                url, content=chunk_data,
                headers={"Content-Type": "application/octet-stream"})
            resp.raise_for_status()
            blob_id = resp.json()["blobId"]
            chunk_blob_ids.append((blob_id, local_sha1_b64))
            offset += chunk_size

        log.info("Delta upload: reused %d/%d chunks", reused, len(chunk_blob_ids))

        if len(chunk_blob_ids) == 1:
            return chunk_blob_ids[0][0]

        # Combine chunks via Blob/upload
        overall_sha1_b64 = base64.b64encode(overall_hash.digest()).decode()

        data_sources = []
        for blob_id, sha1_b64 in chunk_blob_ids:
            obj = {"blobId": blob_id, "digest:sha": sha1_b64}
            data_sources.append(obj)

        create_obj = {
            "data": data_sources,
            "type": content_type,
            "digest:sha": overall_sha1_b64,
        }

        body = {
            "using": [CAP_CORE, CAP_BLOB],
            "methodCalls": [
                ["Blob/upload", {
                    "accountId": self.account_id,
                    "create": {"combined": create_obj},
                }, "b0"],
            ],
        }
        resp = await self._http.post(self.api_url, json=body)
        resp.raise_for_status()
        responses = resp.json()["methodResponses"]
        _, result, _ = responses[0]
        created = result.get("created", {}).get("combined")
        if not created:
            err = result.get("notCreated", {}).get("combined", {})
            raise RuntimeError(f"Blob/upload combine failed: {err}")
        return created.get("id") or created["blobId"]

    async def create_file(self, parent_id: str, name: str, data: bytes,
                          content_type: str | None = None) -> FileNode:
        """Upload blob and create a file node. Returns the new FileNode."""
        if content_type is None:
            content_type = mimetypes.guess_type(name)[0] or "application/octet-stream"
        blob_id = await self.upload_blob(data, content_type)
        now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        responses = await self._call([
            ["FileNode/set", {
                "accountId": self.account_id,
                "create": {
                    "c0": {
                        "parentId": parent_id,
                        "name": name,
                        "blobId": blob_id,
                        "type": content_type,
                    }
                },
            }, "s0"],
        ])
        _, result, _ = responses[0]
        created = result.get("created", {}).get("c0")
        if not created:
            err = result.get("notCreated", {}).get("c0", {})
            raise RuntimeError(f"FileNode/set create failed: {err}")
        # Merge the server response with what we sent
        created.setdefault("parentId", parent_id)
        created.setdefault("name", name)
        created.setdefault("blobId", blob_id)
        created.setdefault("type", content_type)
        created.setdefault("size", len(data))
        self.state = result.get("newState", self.state)
        return FileNode(created)

    async def create_folder(self, parent_id: str, name: str) -> FileNode:
        """Create a folder node. Returns the new FileNode."""
        responses = await self._call([
            ["FileNode/set", {
                "accountId": self.account_id,
                "create": {
                    "c0": {
                        "parentId": parent_id,
                        "name": name,
                    }
                },
            }, "s0"],
        ])
        _, result, _ = responses[0]
        created = result.get("created", {}).get("c0")
        if not created:
            err = result.get("notCreated", {}).get("c0", {})
            raise RuntimeError(f"FileNode/set create folder failed: {err}")
        created.setdefault("parentId", parent_id)
        created.setdefault("name", name)
        self.state = result.get("newState", self.state)
        return FileNode(created)

    async def update_node(self, node_id: str, **updates) -> None:
        """Update a FileNode (e.g. name, parentId)."""
        responses = await self._call([
            ["FileNode/set", {
                "accountId": self.account_id,
                "update": {
                    node_id: updates,
                },
            }, "s0"],
        ])
        _, result, _ = responses[0]
        if node_id in result.get("notUpdated", {}):
            err = result["notUpdated"][node_id]
            raise RuntimeError(f"FileNode/set update failed: {err}")
        self.state = result.get("newState", self.state)

    async def destroy_node(self, node_id: str, remove_children: bool = False) -> None:
        """Destroy a FileNode."""
        args = {
            "accountId": self.account_id,
            "destroy": [node_id],
        }
        if remove_children:
            args["onDestroyRemoveChildren"] = True
        responses = await self._call([
            ["FileNode/set", args, "s0"],
        ])
        _, result, _ = responses[0]
        if result.get("notDestroyed", {}).get(node_id):
            err = result["notDestroyed"][node_id]
            raise RuntimeError(f"FileNode/set destroy failed: {err}")
        self.state = result.get("newState", self.state)

    async def replace_file(self, node_id: str, parent_id: str, name: str,
                           data: bytes, content_type: str | None = None,
                           old_blob_id: str | None = None) -> FileNode:
        """Replace a file's content via blobId update (v10: blobId is mutable).

        The node ID stays the same — no destroy+create needed.
        When old_blob_id is provided, uses delta-aware chunked upload to
        skip unchanged chunks and save bandwidth.
        """
        if content_type is None:
            content_type = mimetypes.guess_type(name)[0] or "application/octet-stream"
        blob_id = await self.upload_blob_delta(data, content_type, old_blob_id)
        now = datetime.now(timezone.utc)
        await self.update_file_content(node_id, blob_id, content_type, now)
        # Return a FileNode with known values (node ID unchanged)
        return FileNode({
            "id": node_id,
            "parentId": parent_id,
            "name": name,
            "blobId": blob_id,
            "type": content_type,
            "size": len(data),
            "modified": now.strftime("%Y-%m-%dT%H:%M:%SZ"),
        })

    async def update_file_content(self, node_id: str, blob_id: str,
                                   content_type: str | None = None,
                                   modified: datetime | None = None) -> None:
        """Update a file's content by setting a new blobId (v10: blobId is mutable).

        The node ID stays the same — no destroy+create needed.
        """
        update_fields: dict = {"blobId": blob_id}
        if content_type is not None:
            update_fields["type"] = content_type
        if modified is not None:
            update_fields["modified"] = modified.strftime("%Y-%m-%dT%H:%M:%SZ")
        else:
            # Client-managed timestamps: null tells server to use current time
            update_fields["modified"] = None

        responses = await self._call([
            ["FileNode/set", {
                "accountId": self.account_id,
                "update": {
                    node_id: update_fields,
                },
            }, "s0"],
        ])
        _, result, _ = responses[0]
        if node_id in result.get("notUpdated", {}):
            err = result["notUpdated"][node_id]
            raise RuntimeError(f"FileNode/set update failed: {err}")
        self.state = result.get("newState", self.state)

    async def batch_update_accessed(self, accessed: dict[str, datetime]) -> None:
        """Batch-update accessed timestamps for multiple nodes."""
        if not accessed:
            return
        update = {}
        for node_id, time in accessed.items():
            update[node_id] = {"accessed": time.strftime("%Y-%m-%dT%H:%M:%SZ")}
        responses = await self._call([
            ["FileNode/set", {
                "accountId": self.account_id,
                "update": update,
            }, "s0"],
        ])
        # Best-effort — ignore errors
        _, result, _ = responses[0]
        self.state = result.get("newState", self.state)

    async def direct_write(self, node_id: str, data: bytes,
                           content_type: str = "application/octet-stream") -> dict:
        """Direct HTTP Write: PUT to webWriteUrlTemplate/{id}.

        Returns dict with blobId, size, type. Only for small files (< 16 MB).
        """
        if not self.web_write_url_template:
            raise RuntimeError("Server does not support direct HTTP write")
        url = self.web_write_url_template.replace("{id}", node_id)
        resp = await self._http.put(url, content=data,
                                     headers={"Content-Type": content_type})
        resp.raise_for_status()
        return resp.json()

    def _blob_url(self, blob_id: str) -> str:
        return (self.download_url
                .replace("{accountId}", self.account_id)
                .replace("{blobId}", blob_id)
                .replace("{type}", "application/octet-stream")
                .replace("{name}", "data"))
