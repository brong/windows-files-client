# Cloud Files Client — Cross-Platform Design Document

This document captures the architecture, design decisions, protocol details, and hard-won lessons from building the Windows cloud files client. It is intended as a reference for agents implementing the same functionality on macOS (FileProvider), iOS (FileProvider), Android (Storage Access Framework / DocumentsProvider), and Linux (FUSE).

## Table of Contents

1. [System Architecture](#1-system-architecture)
2. [JMAP Protocol Layer](#2-jmap-protocol-layer)
3. [Sync Engine Design](#3-sync-engine-design)
4. [File Download (Hydration)](#4-file-download-hydration)
5. [File Upload & Outbox](#5-file-upload--outbox)
6. [Conflict Resolution & Echo Suppression](#6-conflict-resolution--echo-suppression)
7. [Delete, Rename, Move](#7-delete-rename-move)
8. [Pin/Unpin & Offline Access](#8-pinunpin--offline-access)
9. [Thumbnails & Previews](#9-thumbnails--previews)
10. [Authentication & Session Management](#10-authentication--session-management)
11. [IPC Between UI and Sync Service](#11-ipc-between-ui-and-sync-service)
12. [Error Handling & Resilience](#12-error-handling--resilience)
13. [Permissions & Read-Only Folders](#13-permissions--read-only-folders)
14. [File Naming & Sanitization](#14-file-naming--sanitization)
15. [Feature Detection & Backward Compatibility](#15-feature-detection--backward-compatibility)
16. [Platform-Specific Integration Points](#16-platform-specific-integration-points)
17. [Specs & References](#17-specs--references)
18. [Pitfalls & Lessons Learned](#18-pitfalls--lessons-learned)

---

## 1. System Architecture

### Two-Process Model

The client runs as two cooperating processes:

- **Service** (background, headless): Owns the sync engine, JMAP connections, file system callbacks, and blob transfers. Runs continuously. On Windows this is a background process launched by the tray app; on other platforms it would be whatever the OS provides (launchd agent, Android foreground service, systemd user unit).

- **App** (UI, user-facing): System tray icon (Windows/macOS/Linux) or settings activity (mobile). Displays sync status, pending changes, account management. Communicates with Service over IPC.

### Why Two Processes

1. The sync engine must survive UI dismissal (mobile) or tray icon restart.
2. Platform file provider APIs (cfapi, FileProvider, FUSE) require a long-lived process.
3. Clean separation: the UI never touches the file system or network directly.

### Project Layering

```
┌─────────────┐  ┌─────────────┐
│     App      │  │   Service   │
│  (UI, tray)  │  │ (sync, I/O) │
└──────┬───────┘  └──────┬──────┘
       │    IPC (pipes)   │
       └────────┬─────────┘
                │
     ┌──────────┼──────────┐
     │          │          │
┌────┴────┐ ┌──┴───┐ ┌────┴─────┐
│ Platform│ │ Jmap │ │ Logging  │
│ (cfapi/ │ │(proto)│ │          │
│  FUSE/  │ └──────┘ └──────────┘
│FileProvr│
└─────────┘
```

The **Jmap** and **Logging** layers are cross-platform (no OS dependencies). The **Platform** layer is the only part that changes per OS. The **IPC** layer needs platform-appropriate transport but the message schema is portable.

---

## 2. JMAP Protocol Layer

### Session Discovery

1. GET the session URL (e.g. `https://api.fastmail.com/jmap/session`) with `Authorization: Bearer <token>`.
2. Response contains:
   - `apiUrl` — POST JMAP method calls here
   - `uploadUrl` — template: `{uploadUrl}/{accountId}/`
   - `downloadUrl` — template: `{downloadUrl}/{accountId}/{blobId}/{type}/{name}`
   - `eventSourceUrl` — SSE push endpoint
   - `accounts` — map of accountId to account metadata
   - `primaryAccounts` — map of capability URI to the default accountId for that capability
   - `capabilities` — server-level capability objects with limits

3. Find the primary account for the FileNode capability. The capability URI is currently `https://www.fastmail.com/dev/filenode` (pre-standard); the IETF capability will be `urn:ietf:params:jmap:filenode` once the draft is finalized.

### Capabilities to Check

| Capability | URI | What It Provides |
|-----------|-----|-----------------|
| JMAP Core | `urn:ietf:params:jmap:core` | Method call limits, upload limits |
| FileNode | `urn:ietf:params:jmap:filenode` (or dev URI) | File sync API. Account capability includes: `webTrashUrl`, `webUrlTemplate`, `webWriteUrlTemplate`, `mayCreateTopLevelFileNode`, `caseInsensitiveNames` |
| Blob | `urn:ietf:params:jmap:blob` | `Blob/upload` (chunked combine, update/destroy), `Blob/get` (inline fetch + digest verification + chunks), `maxDataSources`, `maxSizeBlobSet` (RFC 9404) |
| BlobExt | `urn:ietf:params:jmap:blobext` | `Blob/convert` (ImageConvert, Archive, Compress, Delta/Patch), `chunkSize` hint, `digest:sha` verification, `supportedImageReadTypes`/`supportedImageWriteTypes`, `supportedArchiveTypes`, `supportedCompressTypes`, `supportedDeltaTypes`, `supportedPatchTypes`, `maxConvertSize` |
| Quota | `urn:ietf:params:jmap:quota` | Storage quota reporting (RFC 9425) |

Always check for capability presence before using its methods. The server may not support all capabilities, and the client must degrade gracefully. For example, if `Blob` capability is absent, fall back to simple uploads (no chunked combine). If `BlobExt` is absent, skip thumbnail fetching via `Blob/convert` and omit `digest:sha` fields from chunked upload combine requests.

### FileNode Data Model

```
FileNode {
    id: String              — server-assigned unique ID
    parentId: String?       — parent folder ID (null only for the invisible root)
    blobId: String?         — content identifier (null for folders, MUTABLE since v10)
    name: String            — display name within parent
    type: String?           — MIME type (null for folders)
    size: Int?              — file size in bytes (server auto-updates when blobId changes)
    created: UTCDate?       — creation timestamp (client-managed)
    modified: UTCDate?      — last-modified timestamp (client-managed, server does NOT auto-update)
    accessed: UTCDate?      — last-accessed timestamp (client-managed)
    role: String?           — special roles: "home", "trash", "temp", "root", "documents", "downloads", "music", "pictures", "videos"
    executable: Bool        — whether the file is executable (default false)
    isSubscribed: Bool      — whether the user is subscribed to changes (default true)
    myRights: {             — permissions for the authenticated user
        mayRead: Bool
        mayWrite: Bool
        mayShare: Bool
    }
    shareWith: Map?         — sharing ACLs (Id[FilesRights])
}
```

Key design points:

- **`blobId` is now mutable (v10).** You can update file content by setting a new blobId via `FileNode/set update`. The node ID stays the same. The server automatically updates the `size` field. You cannot change a file into a folder or vice versa. This is a significant improvement over the old destroy+create pattern — the node ID is now stable across content updates, which simplifies local state tracking.

- **`onExists: "replace"`** is still available for creating files that may already exist (e.g., new file creation where a name collision is possible). But for updating existing files where you already know the node ID, use `FileNode/set update { blobId: newBlobId }` instead.

- **Timestamps are client-managed.** The server does NOT auto-update `modified` or `accessed`. Clients SHOULD explicitly set `modified` on content changes and metadata changes (rename, move). Setting a timestamp to `null` tells the server to use the current time.

- **`role: "home"`** is the sync root. Find it with `FileNode/query { filter: { role: "home" } }`. Do not hardcode an ID.

- **`role: "trash"`** is the server-side recycle bin. Moving nodes here (via `FileNode/set update { parentId: trashNodeId }`) instead of destroying them enables restore.

- **Folders have `blobId: null`.** This is how you distinguish files from folders.

- **Direct HTTP Write.** If the server provides `webWriteUrlTemplate` in the FileNode capability, clients can PUT to `{webWriteUrlTemplate}/{id}` to replace file content directly. Returns `{blobId, size, type}`. Suitable for small files (< 16 MB). PATCH applies deltas.

### JMAP Method Patterns

**Batched requests:** Multiple methods can be sent in one HTTP POST. Use result references (`#ids` with `resultOf`) to chain query → get without a round-trip:

```json
[
    ["FileNode/query", { "accountId": "A", "filter": { "role": "home" } }, "c0"],
    ["FileNode/get", { "accountId": "A", "#ids": { "resultOf": "c0", "name": "FileNode/query", "path": "/ids" } }, "c1"]
]
```

**Pagination:** `FileNode/query` returns up to `limit` results (we use 4096). If `total > limit`, re-query with `position` offset. `FileNode/get` accepts up to ~1024 IDs per call (server limit from `maxObjectsInGet`).

**Incremental sync:** `FileNode/changes { sinceState }` returns `created[]`, `updated[]`, `destroyed[]`, plus `hasMoreChanges` (loop until false). If the state token is too old, the server returns error `cannotCalculateChanges` — fall back to full reconciliation.

### Blob Operations

**Upload:** Raw HTTP POST to `{uploadUrl}/{accountId}/` with file bytes in the body. Set `Content-Type` header. Returns `{ blobId, size, type }`.

**Direct HTTP Write (v10):** If `webWriteUrlTemplate` is available in the FileNode capability, PUT to `{webWriteUrlTemplate}/{id}` to replace file content directly without a separate blob upload + FileNode/set update. Returns `{blobId, size, type}`. Best for small files (< 16 MB) as it is non-resumable. PATCH applies binary deltas.

**Smart upload strategy:** Three tiers based on file size:
1. **Direct HTTP PUT** (< 16 MB, when `webWriteUrlTemplate` available): Single non-resumable PUT. Simplest and fastest.
2. **Chunked upload with delta** (≥ 16 MB, existing file): Upload only changed chunks by comparing local chunk hashes against server's `Blob/get chunks`. Combine old + new chunks via `Blob/upload`. Then `FileNode/set update { blobId }`.
3. **Full chunked upload** (new files or no prior version): Upload all chunks, combine via `Blob/upload`, then `FileNode/set update { blobId }`.

**Download:** HTTP GET from `{downloadUrl}/{accountId}/{blobId}/{type}/{name}`. Supports `Range` header (server may return 206 or 200).

**Inline fetch (small files):** `Blob/get { ids: [blobId], properties: ["data:asBase64", "size", "digest:sha"] }` — returns base64-encoded content inline for files ≤16KB. Avoids a separate HTTP round-trip.

**Digest verification:** Fetch `digest:sha` or `digest:sha-256` via `Blob/get` concurrently with the download. Compare after transfer completes. Log mismatches but don't fail (network corruption is possible but rare; blocking would be worse).

**Thumbnails:** `Blob/convert { create: { "t0": { imageConvert: { blobId, width, height, type: "image/png", autoOrient: true } } } }` — server-side resize, returns a new blobId for the thumbnail. Download the result as a normal blob.

### SSE Push

```
GET {eventSourceUrl}?types=FileNode&closeafter=no&ping=60
Accept: text/event-stream
```

Server sends `event: state` with JSON payload containing changed state tokens per account and type. On receiving a push, wake the sync loop and call `FileNode/changes`.

The push connection should reconnect with exponential backoff (1s → 60s, reset on successful connection). Use a bounded channel/queue between the SSE reader and the sync loop — if multiple pushes arrive while a sync is running, they coalesce naturally (you only need the latest state token).

---

## 3. Sync Engine Design

### State Machine

```
[Starting] → RegisterSyncRoot → PopulateAsync → Connect → [Running]

Running:
  SSE push or timer → PollChangesAsync → apply creates/updates/destroys
  Local file change → enqueue in outbox → OutboxProcessor uploads/deletes
  User action (pin/unpin/delete) → immediate callback handling
```

### Initial Populate

Two paths:

**Cold start** (no cache, or `--clean`):
1. Query for `role: "home"` node → homeNodeId
2. Query for `role: "trash"` node → trashNodeId
3. Paginated `FileNode/query` + `FileNode/get` for all nodes
4. BFS from home node: create placeholder files/directories on disk
5. Save node cache with state token

**Warm start** (cache exists):
1. Load node cache → restore in-memory mappings
2. Verify cached entries exist on disk (skip missing)
3. `FileNode/changes` with cached state
4. If `cannotCalculateChanges` → full reconciliation (fetch all, diff against cache)

### In-Memory Mappings

The sync engine maintains bidirectional mappings:

| Map | Key → Value | Purpose |
|-----|-------------|---------|
| pathToNodeId | local path → server nodeId | Resolve uploads, renames |
| nodeIdToPath | server nodeId → local path | Apply server changes locally |
| nodeIdToBlobId | nodeId → blobId | Thumbnail lookups without hydration |
| readOnlyPaths | folder path → rights | Enforce write protection |

These are rebuilt from cache on warm start and kept in sync during operation.

### Persistent State

Two files per account, stored in a platform-appropriate location:

**Node cache** (`nodecache.json`):
- Version number (increment on schema change to force full refetch)
- homeNodeId, trashNodeId
- State token for incremental sync
- Map of nodeId → { relativePath, size, modified, isFolder, blobId, myRights }

**Sync outbox** (`outbox.json`):
- Array of pending local changes (see section 5)
- Persisted on every mutation, loaded on restart

### Concurrency Control

Use two priority lanes for JMAP requests (4 concurrent slots each):

- **Interactive**: File hydration (user opened a file), digest verification
- **Background**: Uploads, deletes, change polling, initial population

This prevents bulk uploads from starving user-initiated downloads. The number of slots (4) is tunable but should match server rate limits.

**HTTP version matters for concurrency.** HTTP/2 multiplexes all requests to the same host over a single TCP connection. This means rapid chunk uploads can saturate the connection and starve interactive downloads despite the separate queue lanes. Force uploads to use HTTP/1.1 so each upload opens its own TCP connection, leaving the HTTP/2 connection free for interactive traffic. See pitfall #22.

---

## 4. File Download (Hydration)

When the user opens a dehydrated placeholder, the OS notifies the sync engine (via platform callback). The sync engine must provide the file content.

### Decision Tree

```
File requested →
  Extract nodeId from placeholder identity →
  Is sync paused or disk full? → reject with error →
  Fetch FileNode by ID (get size, type) →

  Size ≤ 16KB AND Blob capability available?
    → Blob/get inline (base64 + digest in one call)
    → Decode, verify digest, return data

  Range request from OS AND server supports Range?
    → HTTP GET with Range header (interactive queue)
    → Concurrent Blob/get for digest (background queue)
    → Verify digest

  Else:
    → HTTP GET full file (interactive queue)
    → Concurrent Blob/get for digest
    → Verify digest
```

### Streaming to the OS

For large files, stream the HTTP response body to the OS file system API in chunks (we use 4MB). Don't buffer the entire file in memory. Report progress to the OS so it can show a progress bar.

### Cancellation

The OS can cancel a hydration (user closed the file, app crashed, etc.). Implement a `CANCEL_FETCH_DATA` callback that cancels the in-flight HTTP download. Use a CancellationToken threaded through the download pipeline. Track active downloads by transfer key so you can cancel the right one.

### Upload Timeout Scaling

Upload timeouts should scale with file size: `base_timeout + (file_size / expected_bandwidth)`. We use 120s + 1s per 25KB. Download timeouts should be similar.

---

## 5. File Upload & Outbox

### Outbox Model

Local changes are **not** uploaded immediately. They're enqueued in a persistent outbox and processed asynchronously. This decouples the file system callback (which must return quickly) from the network operation (which may be slow or fail).

Each outbox entry tracks:
- `localPath`, `nodeId` (null for new files)
- `isDirtyContent` (file content changed), `isDirtyLocation` (rename/move), `isDeleted`
- `attemptCount`, `lastError`, `nextRetryAfter` (for backoff)
- `contentType` (MIME type from file extension)

### Coalescing

**Same file edited multiple times:** Update the existing entry's `isDirtyContent` flag and reset retry state. Content is always read at processing time (not enqueue time), so we get the latest version.

**Delete after create/edit:** Convert the existing entry to a delete. No need to upload content that's about to be destroyed.

**Create then rename:** Merge into a single create with the final name.

### Processing Priority

Process outbox entries in this order:

1. **Folder creates** — shallowest first (parents before children)
2. **Content uploads** — alphabetical within parent
3. **Moves/renames** — after content is settled
4. **Deletes** — deepest first (children before parents)

Skip an entry if its parent folder is still pending creation (it'll be processed next cycle).

### Upload Flow

**New file:**
1. Resolve parentId from the local path (look up parent directory in pathToNodeId)
2. Capture `creationTimeUtc` and `lastWriteTimeUtc` from the local file (before opening)
3. Open file for reading (shared access — the user may still have it open)
4. POST blob to upload URL → get blobId
5. `FileNode/set create { parentId, blobId, name, type, created, modified, onExists: "replace" }` → new nodeId
6. Convert local file to a platform placeholder with the new nodeId as identity
7. Update all mappings

**Modified file:**
1. Capture `creationTimeUtc` and `lastWriteTimeUtc` from the local file
2. Upload new blob → new blobId
3. `FileNode/set create { parentId, blobId, name, type, created, modified, onExists: "replace" }` → old node destroyed, new node created with new ID
4. Update placeholder identity with new nodeId
5. Update mappings

**Important:** Because `blobId` is immutable, updating file content always creates a new node. The `onExists: "replace"` option makes this atomic — the server handles the destroy-and-create in one operation.

### Chunked Upload (Large Files)

Chunked uploads require the `urn:ietf:params:jmap:blob` capability (`Blob/upload` with `dataSourceObjects`). The `blobext` capability is **not** required — it only adds `chunkSize` (a server-suggested chunk size hint) and `digest:sha` verification on individual chunks. When only the base Blob capability is available, use a default chunk size (64MB) and omit `digest:sha` fields from the combine request.

1. Gate on `HasBlob` (not `ChunkSize` or `HasBlobExt`) — use chunked upload whenever `urn:ietf:params:jmap:blob` is available
2. Calculate effective chunk size: start from `chunkSize` (from `blobext` capability, or 64MB default), double until `ceil(fileSize / chunkSize) <= maxDataSources` (from `urn:ietf:params:jmap:blob`), clamp to [1MB, 64MB]
3. If fileSize > `maxSizeBlobSet`, reject immediately — the file cannot be uploaded
4. Upload each chunk as a separate blob POST, collecting `(chunkIndex, blobId)` pairs
5. Persist chunk blobIds with the outbox entry for resumability
6. `Blob/upload create` with `data` array referencing all chunk blobIds → final blobId. Include `digest:sha` fields only when the `blobext` capability is present
7. Create the FileNode with the combined blobId

Force HTTP/1.1 for chunk uploads (see §3 Concurrency Control). Insert `Task.Yield()` between chunks to let interactive work proceed.

### Rejected Files

Some files cannot be uploaded (too large for `maxSizeBlobSet`, forbidden by server, permanently failing). These are marked as rejected in the outbox rather than silently removed:

- **Explorer indicator**: Mark the file not-in-sync so it shows an error overlay
- **Activity list**: Show rejected files in red (permanent), temporary errors in orange (will retry)
- **Tray icon**: Red when rejected files exist; stays green for temporary errors
- **Auto-clear**: If the user modifies or deletes the file, the rejection is dismissed and the entry is either re-queued (modify) or removed (delete)

See pitfalls #23 and #24 for details.

**Preserving timestamps:** Always pass the local file's `created` and `modified` timestamps to the server on create/replace. This keeps the server's properties in sync with the local filesystem. Without this, the server assigns its own timestamps, and the next `PollChanges` would see a spurious difference between the server node and the local file. This is especially important for files copied or moved into the sync root — they may have old creation/modification times that should be preserved on the server, not replaced with "now". Preserving mtime also simplifies echo suppression — the server-known mtime can be used as the ground truth for "has this file been edited?"

---

## 6. Conflict Resolution & Echo Suppression

### The Echo Problem

Every sync client has this problem: you make a change (download a file, rename something, convert to placeholder), and the OS fires a file-change notification back at you. Without suppression, you'd try to upload what you just downloaded, or re-sync what you just synced.

### Echo Suppression Strategies

The core principle: **always compare mtime, never suppress based on key presence alone.** A "recently uploaded" or "recently hydrated" marker tells you to *check* for an echo, but the mtime comparison is what actually distinguishes an echo from a real edit. If you suppress based on key presence, a real user edit that arrives while the marker is still live will be silently dropped.

**After download (hydration):**
- Record `{ nodeId: lastWriteTimeUtc }` at hydration completion (this is the server-set mtime, since placeholders have their mtime set from the server's `Modified` property)
- When a file-change notification fires, look up the recorded mtime for that nodeId
- If current mtime == recorded mtime: echo from hydration → skip
- If current mtime != recorded mtime: real edit after hydration → let through
- Consume (remove) the entry when the debounced file-change event processes it

**After upload:**
- Record `{ path: lastWriteTimeUtc }` at upload time
- When a file-change notification fires, compare current lastWriteTime to recorded value
- If unchanged: echo from our ConvertToPlaceholder/UpdatePlaceholder call → skip
- If changed: real edit happened concurrently → let through
- Because the client passes `modified` to the server on upload, the server's mtime stays in sync with the local file, avoiding spurious change detection on the next PollChanges

**After server-driven rename:**
- When PollChanges renames a file locally, update the pathToNodeId mapping first
- The OS rename callback checks if mappings already reflect the new name → skip

**Two notification paths:** Some platforms have both a "file closed after modification" callback (fast, no debounce) and a general file-system watcher (debounced). Both paths must check mtime independently. The fast path uses non-consuming lookups (the entry stays for the debounced path to consume later). The debounced path does the final consume-and-check. This ensures neither path permanently suppresses real edits.

### Server Conflicts

We use a "server wins" model for most conflicts:
- Server rename + local rename of same file: server version applied, local change re-enqueued
- Server delete + local edit: local file becomes a new create (reparented if needed)
- Server edit + local edit: both changes survive as `onExists: "replace"` is last-writer-wins on the server

For more sophisticated conflict resolution (three-way merge, conflict copies), the client would need to detect the situation and create a "Conflicted copy of..." file. We haven't needed this yet because the single-user case dominates.

---

## 7. Delete, Rename, Move

### Delete

**Preferred path (trash available):**
```
FileNode/set update { nodeId: { parentId: trashNodeId }, onExists: "rename" }
```
Moves the node to the server trash. `onExists: "rename"` handles name collisions in trash.

**Fallback (no trash):**
```
FileNode/set destroy { nodeId, onDestroyRemoveChildren: true }
```
Permanent deletion. Use `onDestroyRemoveChildren` for directories.

**Idempotent:** If the node is already gone (404 / notFound), treat as success.

### Rename

```
FileNode/set update { nodeId: { name: "newName" } }
```

### Move

```
FileNode/set update { nodeId: { parentId: "newParentId" } }
```

Move + rename in one call:
```
FileNode/set update { nodeId: { parentId: "newParentId", name: "newName" } }
```

### Recycle Bin Restore

When a user restores a file from the OS recycle bin, it reappears locally. Detection:

1. **Pending delete not yet sent:** Cancel the outbox entry immediately (free).
2. **Already trashed on server:** `FileNode/set update { nodeId: { parentId: originalParent } }` to move it back.
3. **Blob still exists:** `FileNode/set create { parentId, blobId, name, onExists: "replace" }` to recreate without re-uploading.
4. **Blob gone:** Full re-upload.

This chain is tried in order, falling through on failure.

---

## 8. Pin/Unpin & Offline Access

Users can mark files/folders as "Always keep on this device" (pin) or "Free up space" (unpin). The sync engine must handle both.

### Pin (Make Available Offline)

1. User pins a folder → OS sets a pinned attribute
2. Sync engine detects the pin (file system watcher or OS callback)
3. Enumerate all dehydrated files in the folder
4. Hydrate each file sequentially (not in parallel — see pitfalls)
5. Store a cancellation token per pinned directory

**Limit concurrent hydrations.** Each `CfHydratePlaceholder` blocks a thread and consumes an interactive queue slot for the download. Use a semaphore (we use 2) to cap concurrent pin-hydrations below the interactive queue size (4 slots), leaving room for user-initiated file opens. Too many concurrent hydrations starves both the thread pool and the interactive queue.

### Unpin (Free Up Space)

1. User unpins a folder → OS sets an unpinned attribute
2. Cancel any in-progress hydration for that folder (cancel the CTS)
3. Cancel in-flight downloads for files in that folder
4. For each file:
   a. Clear pin state (set to "unspecified") — required before dehydration
   b. Dehydrate the file atomically (mark in-sync at the same time)
5. Guard against feedback loops (dehydrating changes attributes, which fires more events)

### Pitfalls

- **Pin propagation is asynchronous.** When the user unpins a directory, the OS propagates the attribute to children over time, not instantly. If you start dehydrating before propagation completes, you'll get errors.
- **Dehydration changes attributes**, which can trigger your own file watcher again. Use a "currently dehydrating" set to prevent re-entry.
- **Atomic dehydrate + mark-in-sync** is essential. If you dehydrate and then separately mark in-sync, there's a window where the OS sees a dehydrated-not-in-sync file and tries to re-download it.

---

## 9. Thumbnails & Previews

### Architecture

The OS asks for thumbnails of dehydrated files (which have no local content). The sync engine must provide them without hydrating the file.

```
OS thumbnail request
  → resolve nodeId from placeholder identity
  → look up blobId from nodeIdToBlobId mapping
  → Blob/convert { blobId, width, height, type: "image/png" }
  → download the resulting thumbnail blob
  → return image data to OS
```

### Requirements

- **Blob/convert capability** (`blobext`): The server must support server-side image resizing. Check for this capability; if absent, return no thumbnail (the OS will show a generic icon).
- **Fast path**: Cache thumbnails in memory (LRU) to avoid repeated server calls for the same file.
- **No hydration**: The thumbnail handler must never trigger hydration of the full file. Use the blobId mapping, not the file path.

### Platform Integration

Each platform has its own thumbnail provider mechanism:
- **Windows**: COM `IThumbnailProvider` registered via MSIX manifest, loaded by dllhost.exe
- **macOS**: `NSFileProviderThumbnailing` protocol on the FileProvider extension
- **Linux FUSE**: No standard mechanism; could use a D-Bus thumbnailer or desktop-specific protocol
- **Android/iOS**: `DocumentsProvider` / `FileProvider` has built-in thumbnail support via `openDocumentThumbnail`

---

## 10. Authentication & Session Management

### Bearer Token

The simplest auth: a long-lived app password or OAuth2 access token passed as `Authorization: Bearer <token>`.

### OAuth2 (Future)

Discovery via `draft-ietf-mailmaint-oauth-public-01`:
1. `GET .well-known/oauth-public` on the mail domain
2. Returns authorization and token endpoints
3. Standard OAuth2 PKCE flow for public clients
4. Refresh tokens for long-lived sessions

### Session Lifecycle

- Fetch session on startup and after auth
- Re-fetch session on 401 or if capabilities change
- The session URL itself may redirect (follow redirects)
- Store the session response; re-derive all URLs from it

---

## 11. IPC Between UI and Sync Service

### Message Types

**Commands** (UI → Service):
- GetStatus, AddLogin, RemoveLogin, ConfigureLogin
- DiscoverAccounts (list available accounts from session)
- Pause, Resume, SyncNow
- GetOutbox (list pending changes)

**Events** (Service → UI, push-based):
- StatusSnapshot (all accounts)
- AccountStatusChanged (one account)
- ActivityChanged (per-account activity snapshot: active/pending/rejected uploads, active/pending downloads, counts)

The activity feed is push-based (not polled). The service fires a throttled event (100ms) whenever outbox or download state changes. The UI debounces (50ms) and renders from a cached snapshot per account. Initial state is fetched via `GetOutbox` on connect; after that, pushes take over.

Upload progress is reported as **bytes uploaded** (not percentage). The service streams push `uploadedBytes` and `fileSize` per entry; the UI calculates percentage locally. This avoids rounding artifacts, enables accurate display for any file size, and lets the UI show bytes/total in tooltips. Progress callbacks from the upload streams are throttled to 100ms to avoid flooding the IPC channel.

### Per-Account Status

```
{
    accountId, loginId, displayName, syncRootPath, username,
    status: Idle | Syncing | Error | Disconnected | Paused,
    statusDetail: "human readable message",
    pendingCount: 5,
    quotaUsed: 1073741824,
    quotaLimit: 5368709120,
    pauseReason: "UserRequested" | "DiskFull" | "MeteredConnection" | null
}
```

### Transport

Platform-appropriate:
- **Windows**: Named pipes
- **macOS**: XPC (preferred for sandboxed apps) or Unix domain sockets
- **Linux**: Unix domain sockets or D-Bus
- **Android**: Bound service with AIDL or Messenger
- **iOS**: App Groups shared container + Darwin notifications (limited)

The message format (JSON lines or similar) can be shared.

---

## 12. Error Handling & Resilience

### Retry Strategy

| Error Class | Action | Severity |
|-------------|--------|----------|
| HTTP 5xx / timeout | Exponential backoff: `min(1000 * 2^attempt, 60000)` ms | Temporary (orange) |
| `cannotCalculateChanges` | Fall back to full reconciliation | — |
| `notFound` / 404 (JMAP method) | Treat as success (idempotent delete/update) | — |
| HTTP 403 / `forbidden` | Retry — usually bad/expired token, recoverable after re-auth | Temporary (orange) |
| HTTP 400 / `urn:ietf:params:jmap:error:limit` | Retry — rate limit or transient server limit | Temporary (orange) |
| HTTP 413 (Payload Too Large) | Permanent rejection — file exceeds server upload size limit | Permanent (red) |
| File too large (`maxSizeBlobSet`) | Permanent rejection — file cannot be chunked within limits | Permanent (red) |
| I/O error (file locked) | Silent retry — file is still being written | Temporary (orange) |
| Network down | Pause sync, resume on connectivity change | — |

The principle: **only reject permanently when the file itself is the problem** (too large). Everything else (auth, rate limits, network, server errors) is potentially recoverable and should retry indefinitely with backoff.

### Error Visibility

Errors are shown differently based on whether they are transient or permanent:

- **Temporary errors** (orange in activity list): Timeouts, 5xx, 400, 403, network errors, file-locked I/O. These auto-retry with exponential backoff (capped at 60s) indefinitely and disappear when the upload succeeds. The tray icon stays green.
- **Permanent rejections** (red in activity list): Only when the *file itself* is the problem — HTTP 413 (Payload Too Large) or exceeding `maxSizeBlobSet`/`maxDataSources` limits. These persist in the outbox until the user modifies or deletes the file. The tray icon turns red. The file is marked not-in-sync in the file system so it shows an error overlay in the file manager.

Never give up on a file that the server *could* accept. A 403 means a bad token, not a bad file. A 400 might be a rate limit (RFC 8620 `urn:ietf:params:jmap:error:limit`). Network errors are by definition transient. Only reject when the file *will never* be accepted.

This distinction prevents users from worrying about transient network hiccups while ensuring they notice files that genuinely cannot sync.

### Disk Space

- Check available disk space before hydrating files
- Emergency threshold: reject hydration below 100MB free
- Monitor disk space continuously; pause sync when full, resume when space freed

### Metered Connections

- Detect metered/cellular networks (OS API)
- Pause background sync on metered connections
- Allow user override

### SSE Reconnection

- Exponential backoff: 1s → 2s → 4s → ... → 60s max
- Reset backoff on successful connection
- On disconnect during active sync, complete current operation, then reconnect

---

## 13. Permissions & Read-Only Folders

### Server Permissions

`myRights.mayWrite: false` means the user cannot create, modify, or delete files in that folder.

### Enforcement

- Track read-only folders in a set, keyed by path
- Reject local writes: if a file appears in a read-only folder, delete it locally and warn the user
- Use OS-level protection where available (NTFS ACLs on Windows, file permissions on Unix) to prevent the user from accidentally creating files the server will reject

### ACL Inheritance

Permissions inherit from parent to child. If a shared folder has `mayWrite: false`, all its contents are read-only regardless of individual file permissions.

---

## 14. File Naming & Sanitization

### Platform-Specific Invalid Characters

| Platform | Invalid Characters |
|----------|-------------------|
| Windows | `\ / : * ? " < > \|` and trailing dots/spaces |
| macOS | `/` and `:` (Finder shows `:` as `/`) |
| Linux | `/` and null byte |
| Android/iOS | Same as their base OS |

### Reserved Names (Windows-specific)

`CON`, `PRN`, `AUX`, `NUL`, `COM0-9`, `LPT0-9` — these cannot be used as file names on Windows regardless of extension. Prefix with underscore or similar.

### Strategy

1. Define a per-platform sanitization function
2. Apply it when creating local placeholders from server names
3. Maintain a mapping between sanitized local name and server name if they differ
4. On upload, use the original (un-sanitized) name if the file was renamed by sanitization

### Path Length

Each OS has different path length limits. Files that exceed the limit simply can't be synced — log a warning and skip them.

---

## 15. Feature Detection & Backward Compatibility

### Server Capabilities

Always check capability presence before using methods. The client must work with servers that support different capability sets:

- No `urn:ietf:params:jmap:blob` → skip digest verification, skip inline small-file fetch
- No `blobext` → skip thumbnails, skip `Blob/convert`
- No `urn:ietf:params:jmap:quota` → skip quota display

### OS Version Detection

Some OS APIs were introduced in specific versions. Use runtime feature detection (check OS version at startup) rather than try-catch around missing APIs:

- **Cheaper**: A version check is a single comparison; exception handling has overhead
- **Clearer**: The code explicitly documents which features need which version
- **Safer**: Some missing APIs cause crashes, not catchable exceptions

On Windows we use `Environment.OSVersion.Version.Build`. Each platform will have its own equivalent (`@available` on Apple, `Build.VERSION.SDK_INT` on Android, kernel version on Linux).

---

## 16. Platform-Specific Integration Points

This section lists what each platform needs to implement. The JMAP protocol layer, sync engine logic, outbox processing, and error handling are shared. Only the file system integration and OS-specific hooks differ.

### What Each Platform Must Provide

| Concern | What to Implement |
|---------|------------------|
| **Placeholder files** | Files that appear in the file system but have no local content until opened |
| **Hydration callback** | OS notifies you when a file needs content; you download and provide it |
| **Dehydration** | Convert a hydrated file back to a placeholder (free up disk space) |
| **File change detection** | Know when the user creates, edits, renames, or deletes files |
| **Progress reporting** | Show download/upload progress in the OS file manager |
| **Status badges** | Show sync status (synced, syncing, error) on file icons |
| **Thumbnail provider** | Return image thumbnails for dehydrated files |
| **Context menus** | "Share", "View on web" entries in the file manager |
| **Sync root registration** | Register your folder as a cloud storage provider with the OS |

### Platform Specs to Research

**macOS / iOS — FileProvider framework:**
- `NSFileProviderReplicatedExtension` (modern API, macOS 12+ / iOS 16+)
- `NSFileProviderItem` for metadata
- `NSFileProviderEnumerator` for directory listing
- `fetchContents(for:)` for hydration
- `NSFileProviderThumbnailing` for thumbnails
- App Extension architecture (separate process from main app)
- CloudKit integration is NOT required (FileProvider works standalone)

**Android — Storage Access Framework:**
- `DocumentsProvider` (extends `ContentProvider`)
- `openDocument()` / `openDocumentThumbnail()`
- `queryDocument()` / `queryChildDocuments()`
- `createDocument()` / `deleteDocument()` / `renameDocument()`
- Scoped storage considerations (Android 11+)
- Foreground service for background sync

**Linux — FUSE:**
- `libfuse` (FUSE 3 preferred)
- Implement `open()`, `read()`, `write()`, `readdir()`, `getattr()`, etc.
- Extended attributes for sync status metadata
- No standard thumbnail/context menu protocol (DE-specific)
- `inotify` or `fanotify` for local change detection
- D-Bus for IPC with desktop environment

### Key Differences by Platform

| Feature | Windows (cfapi) | macOS (FileProvider) | Linux (FUSE) | Android (SAF) |
|---------|----------------|---------------------|-------------|---------------|
| Placeholder support | Native (1KB stub) | Native (dataless) | Must implement (sparse files or custom) | Virtual (no local file) |
| Hydration trigger | Kernel callback | Extension callback | FUSE read() | openDocument() |
| Dehydration | CfUpdatePlaceholder | evictItem() | Truncate + mark | N/A (virtual) |
| File watching | cfapi callbacks + FSW | Extension signals | inotify/fanotify | ContentResolver |
| Progress | CfExecute chunks | NSProgress | Custom UI | Notification |
| Thumbnails | COM IThumbnailProvider | NSFileProviderThumbnailing | D-Bus thumbnailer | openDocumentThumbnail() |
| Context menus | COM IExplorerCommand | FinderSync extension | Desktop-specific | Intent filters |

---

## 17. Specs & References

### IETF Standards

| Document | Description |
|----------|-------------|
| [RFC 8620](https://datatracker.ietf.org/doc/rfc8620/) | JMAP Core Protocol |
| [draft-ietf-jmap-filenode](https://datatracker.ietf.org/doc/draft-ietf-jmap-filenode/) | JMAP FileNode (file storage) |
| [RFC 9404](https://datatracker.ietf.org/doc/rfc9404/) | JMAP Blob Management |
| [RFC 9425](https://datatracker.ietf.org/doc/rfc9425/) | JMAP Quotas |
| [draft-ietf-mailmaint-oauth-public-01](https://datatracker.ietf.org/doc/draft-ietf-mailmaint-oauth-public-01/) | OAuth Public Client Discovery |

### Platform Documentation

| Platform | Key Documentation |
|----------|------------------|
| Windows cfapi | [Build a Cloud File Sync Engine](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine) |
| Windows Cloud Mirror sample | [GitHub: Windows-classic-samples/CloudMirror](https://github.com/Microsoft/Windows-classic-samples/tree/master/Samples/CloudMirror) |
| macOS FileProvider | Apple Developer: File Provider framework |
| iOS FileProvider | Apple Developer: File Provider framework (shared with macOS) |
| Android DocumentsProvider | Android Developer: Storage Access Framework |
| FUSE | [libfuse GitHub](https://github.com/libfuse/libfuse) |

---

## 18. Pitfalls & Lessons Learned

These are problems we actually hit during development. Each one cost hours to debug. Future platform implementations should watch for analogous issues.

### Protocol Pitfalls

**1. blobId is now mutable (v10) — use FileNode/set update for content changes.**
Before v10, blobId was immutable and content updates required destroy+create with `onExists: "replace"`. Since v10, you can update blobId directly via `FileNode/set update { blobId: newBlobId }`. The node ID stays the same, which is a major simplification — no need to update local mappings or placeholder identities. The server automatically updates the `size` field. Always explicitly set the `modified` timestamp in the update call — timestamps are now client-managed and the server does NOT auto-update them. Keep `onExists: "replace"` for new file creation where a name collision is possible.

**2. State tokens expire — handle `cannotCalculateChanges`.**
If the client is offline too long, the server can't compute incremental changes. You must detect this error and fall back to a full reconciliation (fetch all nodes, diff against cache).

**3. SSE push type names may vary.**
The server may send both `"FileNode"` and `"StorageNode"` as event types. Filter by the type you care about and ignore others. Don't assume the event type name matches the capability name exactly.

**4. Capability URIs will change.**
The FileNode capability URI is currently a dev/pre-standard URI. When the IETF spec is finalized, it will change to `urn:ietf:params:jmap:filenode`. Check for both during the transition period.

**5. Check for capability before calling methods.**
`Blob/convert` requires `blobext`. `Blob/get` requires `urn:ietf:params:jmap:blob`. Quota methods require `urn:ietf:params:jmap:quota`. Calling methods without the capability causes server errors.

### Sync Engine Pitfalls

**6. Echo suppression must always compare mtime — never suppress on key presence alone.**
Every operation you perform (download, rename, convert-to-placeholder) triggers file system notifications back at you. Without suppression, you'll create infinite sync loops or re-upload content you just downloaded. However, suppression must be mtime-based: record the mtime when you perform the operation, then compare it when the echo arrives. If you suppress based solely on "this file was recently uploaded/hydrated" (key presence without mtime check), a real user edit that arrives while the marker exists will be silently dropped. This was a real bug: the "file close" fast-path suppressed all close events for a file as long as it was in the "recently hydrated" set, regardless of whether the user had edited it since hydration.

**7. Outbox coalescing prevents redundant uploads.**
If a user saves a file 10 times in 30 seconds, you should upload once (the final version), not 10 times. Coalesce by path, always read content at processing time.

**8. Deletion must be idempotent.**
The file may already be gone on the server (deleted by another client, or a race condition). Treat `notFound` responses as success.

**9. Parent folders must exist before children.**
When processing the outbox, create folders shallowest-first. When deleting, delete deepest-first. If a parent is still pending creation, skip the child and retry next cycle.

### File System Pitfalls

**10. Opening a dehydrated file for reading triggers hydration.**
On Windows, `GENERIC_READ` on a dehydrated placeholder triggers a FETCH_DATA callback to your own process, creating a deadlock. Use write-only access for metadata operations. Other platforms will have analogous issues — any read of a virtual file triggers the hydration callback.

**11. File attribute changes trigger file watcher events.**
Converting a file to a placeholder, marking it in-sync, or dehydrating it changes file attributes. Your file watcher will fire. Guard against this with "currently operating" sets.

**12. Pin propagation is asynchronous.**
When the user unpins a directory, the OS may propagate the attribute to children over time. Don't assume all children are unpinned immediately. Clear pin state explicitly on each file before dehydrating.

**13. Unbounded parallel hydration starves the interactive queue.**
Each `CfHydratePlaceholder` triggers a FETCH_DATA callback that consumes an interactive queue slot. If the interactive queue has N slots and you launch N concurrent pin-hydrations, user-initiated file opens (also interactive) have to wait for a multi-hundred-MB download to finish. Use a semaphore to cap pin-hydrations at N-2 (we use 2 out of 4 interactive slots), leaving headroom for user actions.

**14. Atomic dehydrate-and-mark-in-sync.**
If you dehydrate a file and then separately mark it in-sync, there's a window where the OS sees a dehydrated, not-in-sync file and immediately tries to re-download it. Always combine these operations atomically.

**15. The OS search indexer will hydrate your files.**
Windows Search (SearchProtocolHost.exe) and Spotlight (macOS) will try to read dehydrated files for indexing. This is low-volume and not harmful, but it can be confusing during debugging. Use process info logging to identify the caller.

### Debugging Pitfalls

**16. Stale OS registrations persist across reinstalls.**
On Windows, sync root registrations, shell namespace CLSIDs, and COM registrations survive app uninstall. They must be cleaned up explicitly. This caused false positives during bisecting (crashes attributed to wrong commits because stale state from a previous version was still active).

**17. Never kill shared host processes.**
On Windows, `dllhost.exe` hosts both our COM handlers AND the WSL2 filesystem bridge. Killing all instances breaks the development environment. Each platform may have similar shared hosting situations — always target specifically.

**18. Log which process triggers callbacks.**
On Windows, `CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO` reveals the caller. This is essential for debugging. Without it, you can't tell if hydration was triggered by the user, the search indexer, your own code, or an antivirus scanner.

### Performance Pitfalls

**19. Debounce file system events, but beware of coalescing.**
File saves often write multiple times (write temp file, rename over original, update metadata). Without debouncing (we use 1 second), you'll enqueue multiple uploads for a single save. However, debouncing coalesces events — if a hydration echo and a real user edit both arrive within the debounce window, they become one event. Your echo suppression must still use mtime comparison (not just "consume the marker"), or the real edit gets suppressed along with the echo.

**20. Download timeout must scale with file size.**
A fixed 30-second timeout will fail for large files on slow connections. Use `base_timeout + size_proportional_timeout`.

**21. Digest verification should be concurrent, not sequential.**
Fetch the content digest via `Blob/get` in parallel with the content download. Don't add a round-trip.

### Upload & Transfer Pitfalls

**22. HTTP/2 multiplexing starves downloads during uploads.**
HTTP/2 multiplexes all requests to the same origin over a single TCP connection. When the client is rapidly uploading small chunks (each a separate POST), the upload stream saturates the shared connection and interactive downloads (user opening a file) get no bandwidth. The fix: force uploads to use HTTP/1.1 (`HttpRequestMessage.Version = HttpVersion.Version11`), which creates separate TCP connections per upload. This leaves the HTTP/2 connection free for interactive traffic (downloads, JMAP method calls, SSE). Also insert `Task.Yield()` between upload chunks so the async scheduler can dispatch interactive work between chunk uploads.

**23. Chunk size must respect `maxDataSources` from the Blob capability.**
The `urn:ietf:params:jmap:blob` capability includes `maxDataSources` (maximum number of data sources in a `Blob/upload` combine call) and `maxSizeBlobSet` (maximum total size for blob operations). The server-suggested `chunkSize` from `blobext` may be too small for large files — if `ceil(fileSize / chunkSize) > maxDataSources`, the upload will fail. Calculate the effective chunk size by doubling the base chunk size until the file fits within `maxDataSources` chunks, clamped to `[1MB, 64MB]`. Reject files exceeding `maxSizeBlobSet` outright — they cannot be uploaded.

**24. Distinguish permanent rejections from temporary errors — and never give up on retriable files.**
When an upload fails, the user needs to know whether to wait or to act. Use two tiers: **temporary errors** (orange, auto-retry with backoff — timeouts, 5xx, 400, 403, network errors, I/O) and **permanent rejections** (red, persist until user fixes — HTTP 413, `maxSizeBlobSet` exceeded). Only reject permanently when the *file itself* is the problem. A 403 is a bad token, not a bad file. A 400 might be a rate limit (`urn:ietf:params:jmap:error:limit`). These must retry indefinitely (backoff caps at 60s). Keep permanent rejections with `IsRejected=true` and a human-readable `RejectionReason`. Show them in the activity list (red, below active transfers). Mark the file not-in-sync in the file system (Explorer overlay icon). Turn the tray icon red. Auto-clear the rejection when the user modifies or deletes the file. Temporary errors stay visible in the activity list (orange) so the user can see retries in progress, but don't affect the tray icon.

**25. Resumable chunked uploads must persist chunk blobIds.**
When uploading a large file in chunks, each chunk returns a blobId. If the upload is interrupted (app crash, network failure, process restart), the already-uploaded chunks still exist on the server. Persist the list of `(chunkIndex, blobId)` pairs with the outbox entry. On retry, verify the stored blobIds are still valid via `Blob/get` (just fetch `size` — no data transfer), then resume from where you left off. Without this, every retry re-uploads the entire file from scratch, which wastes bandwidth and time for multi-gigabyte files.

### Architecture Pitfalls

**26. Subscribe to events before loading initial state.**
When wiring up event-driven components, always subscribe to change events *before* loading the initial snapshot. If you load first and subscribe second, changes that occur between the load and the subscription are silently lost. This was a real bug: the outbox loaded its persisted state, then later the `Changed` event was wired up — any outbox mutations during that gap (e.g., from a concurrent sync operation) were never broadcast to the UI.

**27. Push-based activity feeds beat polling.**
Polling the service for activity data (outbox entries, active downloads) at a fixed interval (e.g., 500ms) wastes IPC round-trips when nothing has changed, introduces visible latency (up to one poll interval before changes appear), and misses short-lived events entirely (a download that starts and completes between polls never appears). Push-based: the service fires a throttled event (100ms) whenever activity changes, and the UI renders what it receives. Use debouncing on the UI side (50ms) to batch rapid pushes into a single render pass.

**28. SafeInvoke for cross-thread UI updates.**
In event-driven architectures, background events frequently arrive on non-UI threads. Blindly calling `Control.Invoke()` or `Control.BeginInvoke()` throws `ObjectDisposedException` if the form is disposing, or `InvalidOperationException` if the handle isn't created yet. Wrap all cross-thread UI calls in a `SafeInvoke` helper that checks `IsDisposed` and `IsHandleCreated` before invoking. For fire-and-forget event handlers, catch and log all exceptions — an unhandled exception in an event handler tears down the entire process.

**29. Upload timeout should be stall-based, not wall-clock.**
A fixed wall-clock timeout (even one scaled to file size) fails for large files on variable connections — a brief network stall triggers the timeout even though the upload was making progress moments earlier. Use a stall-based timeout: reset a timer on every successful chunk upload or progress callback. Only trigger the timeout if no progress has been made for N seconds (we use 30s). This lets large uploads on slow connections complete while still catching genuinely stalled transfers.

**30. Stall timer must not start until the upload begins.**
If the stall timer starts ticking when the upload is *enqueued* rather than when it actually starts sending bytes, queue wait time counts toward the stall timeout. With 4 concurrent upload slots and large files, a queued upload can easily wait 30+ seconds for a slot, triggering a false stall cancellation before any bytes are sent. Start the timer unarmed (infinite initial due time) and arm it on the first progress callback. This was a real bug: the client cancelled the HTTP request after the server had received all bytes but before it sent the response (nginx logged 499), then the retry hit a 403.

**31. Report upload progress as bytes, not percentage.**
Shipping percentage (int 0-100) over IPC loses precision for large files and makes it impossible to show bytes/total in the UI. With a 2GB file, each 1% is 20MB — the progress bar appears frozen for long stretches. Report cumulative bytes uploaded and file size separately; the UI calculates percentage locally. Throttle progress callbacks from the upload streams (100ms) to avoid flooding the IPC channel — without throttling, every 8KB socket write generates a callback that propagates through the outbox, IPC broadcast, and UI render path.

**32. Use SocketsHttpHandler with connection lifetime limits — HttpClientHandler poisons the pool.**
`HttpClientHandler` has no `PooledConnectionLifetime` control. When a server closes a TCP connection during a long transfer (e.g., nginx timeout), the dead connection stays in the pool and subsequent requests fail with `SocketException: An operation was attempted on something that is not a socket`. This is especially catastrophic for large file uploads where the server-side timeout fires before the upload completes. Use `SocketsHttpHandler` with `PooledConnectionLifetime` (5 minutes) and `PooledConnectionIdleTimeout` (2 minutes) to recycle connections before they go stale. This applies to all HTTP handler chains — both `DelegatingHandler` subclasses (token auth, OAuth) should create `SocketsHttpHandler` as their inner handler.

**33. Chunked upload requires Blob capability, not BlobExt — `digest:sha` is optional.**
`Blob/upload` with `dataSourceObjects` (combining chunks into a final blob) is part of the base `urn:ietf:params:jmap:blob` capability (RFC 9404). The `blobext` capability adds two things: a server-suggested `chunkSize` and the ability to include `digest:sha` fields for per-chunk and overall SHA verification. When only the base Blob capability is present: use a default chunk size (64MB), omit all `digest:sha` fields from the combine request, and use `["urn:ietf:params:jmap:core", "urn:ietf:params:jmap:blob"]` as the capability list (not `blobext`). Gate chunked uploads on `HasBlob`, not on `ChunkSize` or `HasBlobExt`.
