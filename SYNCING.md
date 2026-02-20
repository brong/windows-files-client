# Sync Engine Internals

This document describes the data flows, JMAP methods, persistent data formats,
and race condition handling in the files-client sync engine.

## Architecture overview

```
                ┌─────────────────────────────────────────────────────────┐
                │                     Server                              │
                │   JMAP API (FileNode/*, Blob/get, upload, download)     │
                │   SSE push  (EventSource: type=FileNode)                │
                └────────────┬─────────────────────┬──────────────────────┘
                             │                     │
                    HTTPS    │              SSE     │
                             │                     │
┌────────────────────────────┼─────────────────────┼──────────────────────┐
│  Service process           │                     │                      │
│                            ▼                     ▼                      │
│  ┌──────────────┐   ┌───────────┐   ┌──────────────────┐               │
│  │  JmapClient  │   │ JmapQueue │   │ AccountSupervisor│               │
│  │  (HTTP)      │◄──┤ (4+4 sem) │   │  (state channel) │               │
│  └──────────────┘   └───────────┘   └────────┬─────────┘               │
│                                               │                        │
│                                    ┌──────────▼──────────┐             │
│                                    │     SyncEngine       │             │
│                                    │  (mappings, state)   │             │
│                                    └──┬───┬───┬───┬───┬──┘             │
│                                       │   │   │   │   │                │
│  ┌────────────────┐  ┌─────────┐  ┌──┘   │   │   │   └──┐             │
│  │FileChangeWatcher│  │SyncRoot │  │      │   │   │      │             │
│  │(FileSystemWatcher)│(cfapi)  │  ▼      ▼   ▼   ▼      ▼             │
│  └───────┬────────┘  └─────────┘ Placeholder  Sync   Outbox           │
│          │                       Manager     Callbacks Processor       │
│          │                                                             │
│          ▼                                                             │
│  ┌──────────────┐   ┌───────────────┐                                  │
│  │  SyncOutbox   │──▶│OutboxProcessor│                                  │
│  │  (persist)    │   │ (4 workers)   │                                  │
│  └──────────────┘   └───────────────┘                                  │
│                                                                        │
│  Persistent storage:                                                   │
│    %LocalAppData%\FastmailFiles\<user>\<accountId>\                    │
│      nodecache.json   — node mappings + JMAP state token               │
│      outbox.json      — pending local changes                          │
└────────────────────────────────────────────────────────────────────────┘
                             │
                    cfapi (cldflt.sys minifilter)
                             │
                    ┌────────▼────────┐
                    │  Windows Shell   │
                    │  (Explorer, etc) │
                    └─────────────────┘
```

## Startup sequence

### 1. Session discovery

```
GET {sessionUrl}   (default: https://api.fastmail.com/jmap/session)
→ JmapSession { capabilities, accounts, primaryAccounts, apiUrl,
                 downloadUrl, uploadUrl, eventSourceUrl }
```

The client finds the primary account for the FileNode capability
(`https://www.fastmail.com/dev/filenode`) and derives a scope key
(`username/accountId`) used to key all persistent state.

### 2. Sync root registration

`SyncRoot.RegisterAsync` creates the local directory and registers it with
Windows Cloud Files API:

- **HydrationPolicy**: Progressive (files start dehydrated)
- **HydrationPolicyModifier**: AutoDehydrationAllowed (OS can reclaim space)
- **PopulationPolicy**: AlwaysFull (directories always show all children)
- **InSyncPolicy**: FileLastWriteTime | DirectoryLastWriteTime
- **ProviderId**: `f5e2d9a1-3b7c-4e8f-9a01-6c2d5e8f1b3a` (stable GUID)

After registration, `SyncRoot.Connect` installs seven cfapi callback handlers
(FETCH_PLACEHOLDERS, FETCH_DATA, CANCEL_FETCH_DATA, NOTIFY_DELETE,
NOTIFY_RENAME, NOTIFY_DEHYDRATE, NOTIFY_DEHYDRATE_COMPLETION) with flags
`CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH`.

### 3. Initial population

Two paths depending on whether a node cache exists:

**Cold start** (`PopulateFullAsync`):

1. `FindHomeNodeIdAsync` — two-call JMAP batch:
   - `FileNode/query { filter: { hasRole: "home" } }` → IDs
   - `FileNode/get { #ids: resultOf query }` → home node
2. `FindTrashNodeIdAsync` — same pattern with `hasRole: "trash"`
3. `QueryAllFileNodeIdsAsync` — paginated `FileNode/query` (limit 4096/page)
4. `GetFileNodesByIdsPagedAsync` — paginated `FileNode/get` (1024 IDs/page)
5. `BuildTreeAndCreatePlaceholders` — BFS from home node, create placeholders
   via `CfCreatePlaceholders`, populate `_pathToNodeId` / `_nodeIdToPath`
6. Save node cache with state token

**Warm start** (`PopulateFromCacheAsync`):

1. Load `nodecache.json` — restore mappings, verify items exist on disk
2. Rebuild `_pathToNodeId`, `_nodeIdToPath`, `_readOnlyPaths` from cache
3. `PollChangesAsync(cachedState)` to catch up with server
4. If `cannotCalculateChanges` error → fall back to `ReconcileFromServerAsync`
   (full re-fetch and diff against local state)

### 4. Steady-state sync loop

`AccountSupervisor.SyncLoopAsync` blocks on a bounded channel (capacity 16,
drop-oldest). The SSE push watcher writes state tokens to this channel.

```
SSE event → LoginManager.RunPushWatcherAsync → supervisor.PushState(state)
         → _stateChannel.Writer.TryWrite(state)
         → SyncLoopAsync wakes → engine.PollChangesAsync(currentState)
```

Empty string = forced poll (used after SSE reconnect). Duplicate state tokens
are skipped as an optimisation.

---

## JMAP methods reference

All requests use capabilities `["urn:ietf:params:jmap:core", "https://www.fastmail.com/dev/filenode"]`
unless noted. Properties requested for FileNode/get:
`["id", "parentId", "blobId", "name", "type", "size", "created", "modified", "role", "myRights"]`.

### Query and fetch

| Method | When called | Request | Response |
|--------|-------------|---------|----------|
| `FileNode/query` | Home/trash discovery, child listing, ID enumeration | `{ accountId, filter?, position?, limit?, sort? }` | `QueryResponse { ids, queryState, total }` |
| `FileNode/get` | Fetch node details (often back-referenced from query) | `{ accountId, ids?, #ids?, properties }` | `GetResponse { list: FileNode[], state, notFound }` |
| `FileNode/changes` | Incremental sync (via `GetChangesAndNodesAsync`) | `{ accountId, sinceState }` | `ChangesResponse { created[], updated[], destroyed[], newState, hasMoreChanges }` |

### Mutations

| Method | When called | Request | Response |
|--------|-------------|---------|----------|
| `FileNode/set` create | New file/folder upload | `{ accountId, create: { "c0": { parentId, blobId?, name, type? } }, onExists? }` | `SetResponse { created: { "c0": FileNode } }` |
| `FileNode/set` create with `onExists:"replace"` | Replace file content (blobId is immutable) | `{ accountId, onExists: "replace", create: { "c0": { parentId, blobId, name, type? } } }` | Same — old node destroyed, new node returned |
| `FileNode/set` update | Move/rename | `{ accountId, update: { nodeId: { parentId, name } }, onExists? }` | `SetResponse { updated }` |
| `FileNode/set` destroy | Delete (rare — usually trash instead) | `{ accountId, onDestroyRemoveChildren: true, destroy: [nodeId] }` | `SetResponse { destroyed }` |

### Blob operations

| Operation | Method | Notes |
|-----------|--------|-------|
| Upload | `POST {uploadUrl}` with raw body | Returns `{ blobId, size, type }` |
| Full download | `GET {downloadUrl}` | Template: `{accountId}/{blobId}/{name}?type={type}` |
| Range download | `GET {downloadUrl}` with `Range: bytes=N-M` header | Falls back to full if server returns 200 |
| Blob/get (RFC 9404) | `Blob/get { ids, properties, offset?, length? }` | Uses `urn:ietf:params:jmap:blob` capability. For small files (<=16KB) and digest verification |

### SSE push

```
GET {eventSourceUrl}?types=FileNode&closeafter=no&ping=60
Accept: text/event-stream

event: state
data: {"changed":{"accountId1":{"FileNode":"stateToken123"}}}
```

One SSE connection per JMAP login session, multiplexing all accounts. The
server sends both `"FileNode"` and `"StorageNode"` types; the client filters
for the correct account ID.

Reconnection uses exponential backoff (1s → 60s max), resetting on success.
On reconnect, a forced empty poll ensures the client catches any changes
missed during the disconnect.

### Multi-call batching

Several operations batch multiple JMAP methods in a single HTTP POST using
result references (`#ids` with `resultOf`):

- **Home/trash discovery**: `FileNode/query` + `FileNode/get` (2 calls)
- **GetChangesAndNodesAsync**: `FileNode/changes` + two `FileNode/get` calls
  referencing `/created` and `/updated` paths (3 calls)

---

## Concurrency control: JmapQueue

All JMAP HTTP requests flow through `JmapQueue`, which provides two priority
lanes with independent semaphores:

| Priority | Max concurrent | Used by |
|----------|---------------|---------|
| `Interactive` | 4 | File hydration (FETCH_DATA callback), digest verification |
| `Background` | 4 | Outbox uploads/deletes, change polling, population |

This prevents upload traffic from starving interactive file downloads and
vice versa.

---

## Persistent data formats

### Node cache (`nodecache.json`)

Location: `%LocalAppData%\FastmailFiles\<username>\<accountId>\nodecache.json`

Written atomically (temp file + rename) after population and each change poll.

```json
{
  "v": 4,
  "homeNodeId": "Mdeadbeef",
  "trashNodeId": "M12345678",
  "state": "abc123",
  "entries": {
    "Mdeadbeef01": {
      "path": "Documents\\report.pdf",
      "size": 104857,
      "modified": "2026-02-18T10:30:00Z",
      "isFolder": false
    },
    "Mdeadbeef02": {
      "path": "Photos",
      "size": 0,
      "modified": "0001-01-01T00:00:00",
      "isFolder": true,
      "myRights": { "mayRead": true, "mayWrite": false, "mayShare": false }
    }
  }
}
```

| Field | Description |
|-------|-------------|
| `v` | Cache format version. Must be `4` or cache is discarded |
| `homeNodeId` | Server ID of the sync root folder (role="home") |
| `trashNodeId` | Server ID of the trash folder (role="trash"), null if unavailable |
| `state` | JMAP state token — passed to `FileNode/changes` on next startup |
| `entries` | Map of nodeId → cache entry |
| `entries[].path` | Relative path from sync root (backslash-separated on Windows) |
| `entries[].size` | File size in bytes (0 for folders) |
| `entries[].modified` | `LastWriteTimeUtc` at cache time — used to detect offline edits |
| `entries[].isFolder` | `true` for directories |
| `entries[].myRights` | Folder permissions (only serialised when non-null, folders only) |

On warm start, each entry is verified against the filesystem. Missing entries
are skipped; size/timestamp mismatches are noted but mappings are still
restored (the `FileChangeWatcher` will detect the difference after `Connect()`).

### Sync outbox (`outbox.json`)

Location: `%LocalAppData%\FastmailFiles\<username>\<accountId>\outbox.json`

Written with 1-second debounce after any mutation. Also written atomically.

```json
[
  {
    "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "localPath": "C:\\Users\\brong\\Fastmail Files\\photo.jpg",
    "nodeId": null,
    "contentType": "image/jpeg",
    "isFolder": false,
    "isDirtyContent": true,
    "isDirtyLocation": true,
    "isDeleted": false,
    "createdAt": "2026-02-19T12:00:00Z",
    "updatedAt": "2026-02-19T12:00:05Z",
    "attemptCount": 0,
    "lastError": null,
    "nextRetryAfter": null
  },
  {
    "id": "f9e8d7c6-b5a4-3210-fedc-ba0987654321",
    "localPath": null,
    "nodeId": "Mdeadbeef03",
    "contentType": null,
    "isFolder": false,
    "isDirtyContent": false,
    "isDirtyLocation": false,
    "isDeleted": true,
    "createdAt": "2026-02-19T12:01:00Z",
    "updatedAt": "2026-02-19T12:01:00Z",
    "attemptCount": 0,
    "lastError": null,
    "nextRetryAfter": null
  }
]
```

| Field | Description |
|-------|-------------|
| `id` | GUID, unique per entry |
| `localPath` | Full local path. Null for delete entries (path removed from indexes) |
| `nodeId` | Server node ID. Null for new files not yet created on server |
| `contentType` | MIME type for uploads |
| `isFolder` | Whether this is a directory operation |
| `isDirtyContent` | File content needs uploading (new file or modified existing) |
| `isDirtyLocation` | File needs move/rename on server |
| `isDeleted` | This entry represents a pending server-side deletion |
| `createdAt` | When this entry was first enqueued |
| `updatedAt` | Last modification (coalesced changes update this) |
| `attemptCount` | Number of failed attempts (for exponential backoff) |
| `lastError` | Error message from last failure |
| `nextRetryAfter` | UTC time before which this entry should not be retried |

The outbox maintains two in-memory secondary indexes for O(1) coalescing:
- `_byPath` — localPath → entry GUID (for content change coalescing)
- `_byNodeId` — nodeId → entry GUID (for move/delete coalescing)

On app restart, all entries are loaded with `attemptCount` and `nextRetryAfter`
reset to zero/null, giving a fresh start.

---

## Data flows

### Server → Client (download / change polling)

#### File hydration (user opens a dehydrated placeholder)

```
Explorer opens file
  → cfapi sends FETCH_DATA callback to SyncCallbacks
  → Extract nodeId from placeholder identity blob (UTF-8)
  → FetchBlobDataAsync:
      Path A (≤16KB, full file):
        Blob/get { ids:[blobId], properties:["data:asBase64","size","digest:sha-256"] }
        → base64 decode, verify digest
      Path B (partial request, Range supported):
        GET {downloadUrl} with Range header  (Interactive queue)
        Blob/get { ids:[blobId], properties:["digest:sha-256"], offset, length }  (Background queue, concurrent)
        → verify digest
      Path C (full download fallback):
        GET {downloadUrl}  (Interactive queue)
        Blob/get for digest  (Background queue, concurrent)
        → verify digest
  → TransferData: send to cfapi in 4MB chunks with CfExecute(TRANSFER_DATA)
  → CfReportProviderProgress after each chunk
  → Mark nodeId in RecentlyHydrated (suppress upload echo)
```

#### Incremental change poll (SSE push received)

```
SSE event → PushState(stateToken) → SyncLoopAsync wakes
  → PollChangesAsync(sinceState):
      FileNode/changes { accountId, sinceState }  (batched with two FileNode/get)
      → GetChangesAndNodesAsync returns (changes, createdNodes, updatedNodes)

  Process updates (shallowest-first by existing path depth):
    For each updated node:
      Skip if outbox has pending change for this nodeId
      Resolve parent to local path
      If parent not in home tree → delete local item + clean mappings
      If path changed → rename on disk, update mappings, mark in-sync
      If path same → ensure placeholder exists, mark in-sync

  Process creates:
    For each created node:
      Skip if outbox has pending change for this nodeId
      Create placeholder via CfCreatePlaceholders
      If under pinned directory → auto-hydrate

  Process destroys:
    For each destroyed nodeId:
      Skip if outbox has pending change for this nodeId or path
      Delete local file/directory
      Clean up mappings

  If hasMoreChanges → recurse with newState
  Save node cache with final state
```

#### Full reconciliation (state too old)

When the server returns `cannotCalculateChanges`, the client falls back to
`ReconcileFromServerAsync`:

1. Fetch all alive node IDs from server (`FileNode/query`, paginated)
2. Compare with cached `_nodeIdToPath` mappings
3. Delete local items for nodes no longer on server (skip if pending in outbox)
4. Fetch all node details in pages (`FileNode/get`, 1024/page)
5. BFS from home node — for each node:
   - If at wrong path → rename on disk
   - If missing → create placeholder
   - If present → mark in-sync
6. Return new state token

### Client → Server (upload / outbox processing)

#### Local file change detection

```
User edits file in sync root
  → FileSystemWatcher fires Changed/Created
  → FileChangeWatcher:
      Skip dehydrated placeholders (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)
      Skip pin/unpin attribute changes (routed to pin handlers instead)
      Buffer in _pending dict (1-second debounce)
  → FlushChanges fires OnChanges event
  → SyncEngine.OnLocalFileChanges:
      Skip echo from our own ConvertToPlaceholder (check _recentlyUploaded timestamp)
      Skip directories already mapped to a nodeId
      Skip files just hydrated by cfapi (check RecentlyHydrated)
      Reject changes in read-only folders (delete the local file)
      → outbox.EnqueueContentChange(path, nodeId, contentType, isFolder)
```

#### Outbox processing priority

`DequeueNext()` returns entries in this order:

| Priority | Category | Sort order | Guard |
|----------|----------|-----------|-------|
| 1 | Folder creates | Shallowest first, then alphabetical | Skip if parent folder still pending create |
| 2 | Content uploads | Alphabetical within same parent | Skip if parent folder still pending create |
| 3 | Pure moves | Alphabetical within same parent | Skip if parent folder still pending create |
| 4 | Deletes | Deepest first (children before parents) | — |

Up to 4 entries are processed concurrently (`MaxConcurrency = 4`).

#### Upload flow (new file)

```
OutboxProcessor.ProcessUploadAsync (change.NodeId == null):

  Check _recentlyTrashed for Recycle Bin restore (see below)

  Resolve parentId from local path → _pathToNodeId
  If parent not mapped → return false (retry later)

  Open file with FileShare.ReadWrite|Delete
  Wrap in ProgressStream for UI progress

  POST {uploadUrl} with file body  →  blobId
    Timeout: 120s base + 1s per 25KB

  FileNode/set create {
    parentId, blobId, name, type,
    onExists: "replace"
  }  →  new FileNode

  CfConvertToPlaceholder(path, nodeId, MARK_IN_SYNC)
  UpdateMappings(path, null, nodeId)
  RecordRecentUpload (suppress echo)
  Strip Zone.Identifier ADS
  CfSetInSyncState
```

#### Upload flow (modified existing file)

```
OutboxProcessor.ProcessUploadAsync (change.NodeId != null):

  Resolve parentId
  Open file, wrap in ProgressStream

  POST {uploadUrl}  →  blobId

  FileNode/set create {
    parentId, blobId, name, type,
    onExists: "replace"
  }  →  new FileNode (old nodeId destroyed, new one returned)

  CfUpdatePlaceholder with new identity
  UpdateMappings(path, oldNodeId, newNodeId)
  RecordRecentUpload

  If isDirtyLocation also set:
    FileNode/set update { parentId, name }  (move)

  CfSetInSyncState
```

#### Move flow

```
OutboxProcessor.ProcessMoveAsync:

  Resolve new parentId from local path
  FileNode/set update {
    [nodeId]: { parentId, name: newName }
  }
  CfSetInSyncState
```

#### Delete flow

```
OutboxProcessor.ProcessDeleteAsync:

  Fetch blobId (best-effort, for potential Recycle Bin restore):
    FileNode/get { ids: [nodeId] }  →  node.BlobId
    Update _recentlyTrashed with blobId

  If trashNodeId available:
    FileNode/set update {
      [nodeId]: { parentId: trashNodeId, name }
      onExists: "rename"
    }
  Else:
    FileNode/set destroy { [nodeId], onDestroyRemoveChildren: true }

  Catch "notFound" / "404" → node already gone, treat as success
```

#### Recycle Bin restore flow

When a file is deleted from the sync root, it goes to the Windows Recycle Bin
locally and the server trash folder. If the user restores from the Recycle Bin,
the outbox processor tries a 3-tier restore chain before falling back to a
full re-upload:

```
HandleDeleteRequestAsync records each item:
  _outboxProcessor.RecordTrashed(path, nodeId)
  → _recentlyTrashed[path] = TrashedInfo(nodeId, blobId: null)
  → _trashedPathByNodeId[nodeId] = path

ProcessDeleteAsync fetches blobId before trashing:
  → updates _recentlyTrashed[path].BlobId

When file reappears (Recycle Bin restore):
  FileChangeWatcher fires → OnLocalFileChanges → EnqueueContentChange(nodeId: null)
  → ProcessUploadAsync sees change.NodeId == null, checks _recentlyTrashed:

  Step 1 — Cancel pending delete (instant, no server calls):
    outbox.TryCancelDelete(nodeId)
    If still queued and not processing → remove entry, restore mappings → done

  Step 2 — Move back from server trash:
    FileNode/set update { [nodeId]: { parentId: originalParent, name } }
    If success → restore mappings → done

  Step 3 — Create with existing blobId (node destroyed, blob may survive):
    FileNode/set create { parentId, blobId, name, onExists: "replace" }
    If success → restore mappings with new nodeId → done

  Step 4 — Fall through to normal upload (blob also gone)
```

### Pin / Unpin (hydration and dehydration)

#### Pin ("Always keep on this device")

```
User right-clicks folder → "Always keep on this device"
  → Windows sets FILE_ATTRIBUTE_PINNED on directory
  → FileChangeWatcher detects attribute change → OnDirectoryPinned
  → SyncEngine.OnDirectoryPinned:
      Add to _pinnedDirectories with CancellationTokenSource
      Spawn HydrateDehydratedFiles task:
        For each file in directory (recursive):
          Check FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS → skip if already hydrated
          CfHydratePlaceholder(handle, 0, -1)
            → triggers FETCH_DATA callback → full download cycle
          Retry failed files up to 3 times with increasing delay
        Mark files in-sync
```

Individual file pin events under a pinned directory are deduplicated — the
directory's `HydrateDehydratedFiles` loop handles them sequentially to avoid
concurrent `CfHydratePlaceholder` calls that starve the thread pool.

#### Unpin ("Free up space")

```
User right-clicks folder → "Free up space"
  → Windows sets FILE_ATTRIBUTE_UNPINNED
  → FileChangeWatcher → OnFileUnpinned
  → SyncEngine.OnFileUnpinned:
      Cancel in-progress hydration (cancel CTS)
      Collect all files with known nodeIds
      CancelFetchesWhere (cancel in-flight downloads for these files)
      Spawn DehydrateFiles task:
        For each file:
          CfSetPinState(UNSPECIFIED) — clear pin before dehydrating
          CfUpdatePlaceholder with CF_UPDATE_FLAG_DEHYDRATE | CF_UPDATE_FLAG_MARK_IN_SYNC
            → atomically dehydrate and mark in-sync
          Retry failed files up to 5 times (handles 0x80070187 "cloud files in use")
```

### Rename and delete (cfapi callbacks)

#### Delete

```
User deletes file/folder in Explorer
  → cfapi sends NOTIFY_DELETE callback
  → SyncCallbacks.NotifyDeleteCallback:
      Extract nodeId from placeholder identity
      Call SyncEngine.HandleDeleteRequestAsync(nodeId, path)
  → HandleDeleteRequestAsync:
      If no nodeId or path not in mappings → allow (echo from PollChangesAsync)
      If parent is read-only → deny (return false, cfapi blocks the delete)
      Enqueue descendant deletes (deepest first via outbox priority)
      Record trashed items (for Recycle Bin restore support)
      Clear mappings
      → Acknowledge: allow delete
```

#### Rename / Move

```
User renames or moves file in Explorer
  → cfapi sends NOTIFY_RENAME callback
  → SyncCallbacks.NotifyRenameCallback:
      Extract nodeId, source path, target path, targetInScope flag
  → HandleRenameRequestAsync:
      If no nodeId → allow (untracked)
      If source parent is read-only → deny
      If target parent is read-only → deny
      If target out of sync root → enqueue as delete
      If mappings already point to target → allow (echo from PollChangesAsync)
      Enqueue move in outbox
      Update mappings immediately (before disk rename completes)
```

---

## In-memory state

### SyncEngine mappings

| Dictionary | Type | Purpose |
|-----------|------|---------|
| `_pathToNodeId` | `ConcurrentDictionary<string, string>` | Local path → server nodeId |
| `_nodeIdToPath` | `ConcurrentDictionary<string, string>` | Server nodeId → local path |
| `_readOnlyPaths` | `ConcurrentDictionary<string, FilesRights>` | Folders with `mayWrite: false` |
| `_pinnedDirectories` | `ConcurrentDictionary<string, CancellationTokenSource>` | Pinned folders + hydration CTS |
| `_recentlyUploaded` | `ConcurrentDictionary<string, DateTime>` | Suppress echo after upload |
| `_hydratingDirectories` | `ConcurrentDictionary<string, byte>` | Prevent duplicate hydration |
| `_dehydratingPaths` | `ConcurrentDictionary<string, byte>` | Prevent dehydration feedback loop |

### SyncCallbacks state

| Dictionary | Type | Purpose |
|-----------|------|---------|
| `RecentlyHydrated` | `ConcurrentDictionary<string, byte>` | Suppress re-upload after download |
| `_inFlightFetches` | `ConcurrentDictionary<long, (CTS, nodeId?)>` | Cancellable in-flight downloads |

### OutboxProcessor state

| Dictionary | Type | Purpose |
|-----------|------|---------|
| `_recentlyTrashed` | `ConcurrentDictionary<string, TrashedInfo>` | path → (nodeId, blobId) for Recycle Bin restore |
| `_trashedPathByNodeId` | `ConcurrentDictionary<string, string>` | nodeId → path (reverse lookup for ProcessDeleteAsync) |

### SyncOutbox indexes

| Dictionary | Type | Purpose |
|-----------|------|---------|
| `_entries` | `Dictionary<Guid, PendingChange>` | Primary store (under lock) |
| `_byPath` | `Dictionary<string, Guid>` | O(1) coalesce by local path |
| `_byNodeId` | `Dictionary<string, Guid>` | O(1) coalesce by server nodeId |
| `_processingIds` | `HashSet<Guid>` | Currently in-flight entries |

All `SyncOutbox` mutations are serialised under a single `lock (_lock)`.

---

## Race condition handling

### Upload echo suppression

When the outbox processor uploads a file and calls `ConvertToPlaceholder` or
`UpdatePlaceholderIdentity`, Windows changes file attributes, causing
`FileSystemWatcher` to fire a `Changed` event. Without suppression this would
re-enqueue the file for upload.

**Mechanism**: After upload, `RecordRecentUpload(path)` stores
`File.GetLastWriteTimeUtc`. When `OnLocalFileChanges` fires, it checks this
timestamp — if unchanged, the event is an echo and is skipped. If the
timestamp differs, a real edit happened concurrently and the change is allowed
through.

### Download echo suppression

When cfapi hydrates a file (FETCH_DATA), `FileSystemWatcher` fires a `Changed`
event. Without suppression this would enqueue an upload of content we just
downloaded.

**Mechanism**: After successful hydration, the nodeId is added to
`SyncCallbacks.RecentlyHydrated`. When `OnLocalFileChanges` fires, it checks
this set — if present, the event is consumed and skipped.

### Server change echo suppression

When `PollChangesAsync` renames a file on disk, the cfapi `NOTIFY_RENAME`
callback fires. Without suppression this would enqueue a server-side move
for a change that originated on the server.

**Mechanism**: `HandleRenameRequestAsync` checks if `_nodeIdToPath[nodeId]`
already equals the target path. If so, mappings were pre-updated by
`PollChangesAsync` and the rename is allowed without enqueuing a server call.

Similarly for deletes: if the path is not in `_pathToNodeId`, the delete is
considered an echo from `PollChangesAsync` which already removed the mapping.

### Outbox coalescing

Multiple rapid edits to the same file produce multiple `FileSystemWatcher`
events. The outbox coalesces them:

- **Same path, existing entry, not deleted**: Update `isDirtyContent` and
  `updatedAt`, reset retry state. The actual file content is read at
  processing time, so only the latest content is uploaded.
- **Same path, existing entry, deleted**: Ignore — delete is terminal.
- **Same nodeId, new delete over existing entry**: Convert to delete entry,
  remove from `_byPath` index.

### Concurrent outbox + change poll

Server changes arriving via `PollChangesAsync` skip items with pending outbox
operations:

```csharp
if (_outbox.HasPendingForNodeId(node.Id))
    continue; // Don't overwrite local intent
```

This prevents the server echo of a local upload from deleting or overwriting
the local file while the outbox still has work pending.

For destroyed nodes, the path is also checked:

```csharp
if (_outbox.HasPendingForPath(localPath))
    continue; // Outbox intends to upload content for this path
```

This handles the case where `onExists:"replace"` destroys the old nodeId
but the outbox still intends to upload new content to that path.

### Delete-in-flight + Recycle Bin restore race

If `ProcessDeleteAsync` is currently running when the user restores from the
Recycle Bin:

1. `TryCancelDelete` returns `false` (entry is in `_processingIds`)
2. `MoveFileNodeAsync` may fail (node not yet in trash) — caught as "notFound"
3. `CreateFileNodeAsync` with `onExists:"replace"` succeeds (node still at
   original location — creates alongside or replaces)
4. The in-flight delete then trashes or destroys the old nodeId → "notFound"
   → treated as success

Result: the restored file gets a new nodeId, old nodeId is cleaned up. Safe.

### Parent folder not yet created

When a user copies a folder tree, children may arrive before the parent folder
is created on the server. The outbox handles this with priority ordering
(folder creates before content uploads) and a guard:

```csharp
HasPendingParentCreateLocked(localPath)
```

If any ancestor directory has a pending folder create (`isFolder && !isDeleted
&& nodeId == null`), the child entry is skipped in `DequeueNext()` and will be
retried on the next cycle.

### Pin propagation race

When the user unpins a directory, Windows propagates `FILE_ATTRIBUTE_UNPINNED`
to children asynchronously. The dehydration code may run before propagation
completes, causing `CfUpdatePlaceholder` with `DEHYDRATE` to fail with
`0x80070188` (conflicting pin state).

**Mechanism**: Before dehydrating each file, `CfSetPinState(UNSPECIFIED)` is
called to clear any remaining pin state. Failed dehydrations are retried up to
5 times with 1-second delays.

### Pin fires for directory AND individual files

`FileSystemWatcher` fires `OnDirectoryPinned` for the directory and
`OnFilePinned` for each child. Without deduplication, concurrent
`CfHydratePlaceholder` calls starve the thread pool.

**Mechanism**: `OnFilePinned` checks `IsUnderPinnedDirectory(filePath)` — if
any ancestor is in `_pinnedDirectories`, the individual file event is skipped.
The directory's `HydrateDehydratedFiles` loop handles all children
sequentially.

### Dehydration feedback loop

Dehydrating a file changes its attributes, which fires `FileSystemWatcher`
again. If `FILE_ATTRIBUTE_UNPINNED` is still set, `OnFileUnpinned` fires
again, creating an infinite loop.

**Mechanism**: `_dehydratingPaths` tracks paths currently being dehydrated.
`OnFileUnpinned` checks this set and returns immediately if the path is
already being processed.

### Concurrent hydration and dehydration

A file being hydrated (FETCH_DATA in progress) when the user unpins the
directory will fail to dehydrate with `0x80070187` ("cloud files in use").

**Mechanism**: `OnFileUnpinned` calls `CancelFetchesWhere` to cancel in-flight
downloads for all files under the unpinned directory. Dehydration then retries
up to 5 times with delays, allowing the cancelled hydration to clean up.

### Exponential backoff

Failed outbox entries use exponential backoff:
`delay = min(1000 * 2^attemptCount, 60000)` milliseconds. After
`MaxAttempts` (10) failures, the entry is permanently rejected and removed.

Permission errors (`forbidden`) are immediately rejected without retry.

### Atomic dehydrate + in-sync

`CfUpdatePlaceholder` with `CF_UPDATE_FLAG_DEHYDRATE | CF_UPDATE_FLAG_MARK_IN_SYNC`
atomically dehydrates and marks in-sync. Without this, a window exists where
Explorer sees a dehydrated-but-not-in-sync file and sends a new FETCH_DATA
request, re-hydrating it immediately.

---

## Error handling

| Error | Source | Handling |
|-------|--------|----------|
| `cannotCalculateChanges` | `FileNode/changes` | Fall back to `ReconcileFromServerAsync` |
| `notFound` / `404` | `FileNode/set` destroy or move | Treat as success (node already gone) |
| `forbidden` / `Forbidden` | Any JMAP call | Immediately reject outbox entry; delete untracked local file |
| HTTP timeout | Upload/download | Exponential backoff retry |
| `IOException` | File open for upload | Silent retry (file still being written) |
| `copying content to a stream` | HttpClient upload | Silent retry (file not ready) |
| Digest mismatch | Blob/get verification | Log warning, do not fail download |
| `0x80070187` | `CfUpdatePlaceholder` | Retry up to 5 times (cloud file in use) |
| `0x80070188` | `CfUpdatePlaceholder` | Clear pin state first, retry (conflicting pin) |
