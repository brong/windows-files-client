# Apple Files Client — Design Document (macOS & iOS)

Cloud filesystem client for the JMAP FileNode API using Apple's FileProvider framework. Covers both macOS (Finder integration) and iOS (Files app integration) from a single codebase.

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Project Structure](#2-project-structure)
3. [FileProvider Extension](#3-fileprovider-extension)
4. [Enumeration & Sync](#4-enumeration--sync)
5. [File Download (Hydration)](#5-file-download-hydration)
6. [File Upload & Local Changes](#6-file-upload--local-changes)
7. [Delete, Rename, Move](#7-delete-rename-move)
8. [Conflict Resolution & Echo Suppression](#8-conflict-resolution--echo-suppression)
9. [Pin/Unpin & Eviction](#9-pinunpin--eviction)
10. [Thumbnails](#10-thumbnails)
11. [Authentication & Session Management](#11-authentication--session-management)
12. [Containing App ↔ Extension Communication](#12-containing-app--extension-communication)
13. [Error Handling & Resilience](#13-error-handling--resilience)
14. [Offline & Network Resilience](#14-offline--network-resilience)
15. [Permissions & Read-Only Folders](#15-permissions--read-only-folders)
16. [File Naming & Sanitization](#16-file-naming--sanitization)
17. [Platform Differences (macOS vs iOS)](#17-platform-differences-macos-vs-ios)
18. [Build & Distribution](#18-build--distribution)
19. [Pitfalls & Lessons Learned](#19-pitfalls--lessons-learned)

---

## 1. Architecture Overview

### System-Managed vs Self-Managed

The fundamental difference from the Windows client: **on Apple platforms, the system owns the on-disk representation.** The extension never directly creates, renames, or deletes files on disk. Instead:

- The extension **enumerates** items (tells the system what exists on the server)
- The extension **provides content** when the system asks (hydration)
- The extension **propagates local changes** when the system notifies it (upload)
- The system handles placeholders, eviction, and disk management autonomously

This means there is no `PlaceholderManager`, no `FileChangeWatcher`, and no direct filesystem manipulation. The sync engine is the enumeration + mutation protocol between your extension and the system.

### Process Model

```
┌─────────────────────┐    ┌──────────────────────────────┐
│   Containing App     │    │   FileProvider Extension      │
│   (SwiftUI)          │    │   (NSFileProviderReplicated   │
│                      │    │    Extension)                 │
│  • Account mgmt      │    │                              │
│  • Auth UI            │    │  • JMAP protocol             │
│  • Domain register    │    │  • Enumeration               │
│  • Settings           │    │  • Hydration / Upload        │
│  • Status display     │    │  • Thumbnail generation      │
└──────────┬───────────┘    └──────────────┬───────────────┘
           │                               │
           │    App Group (shared)          │
           └──────────┬───────────────────-─┘
                      │
         ┌────────────┼────────────┐
         │            │            │
    ┌────┴────┐  ┌────┴────┐  ┌───┴────┐
    │ SQLite  │  │Keychain │  │Shared  │
    │ (nodes, │  │(tokens) │  │Defaults│
    │  state) │  │         │  │(config)│
    └─────────┘  └─────────┘  └────────┘
```

**Containing App** — User-facing application. Handles authentication, account setup, domain registration/removal, settings, and status display. Has no direct access to the sync root directory.

**FileProvider Extension** — App extension launched on demand by the system. Runs in a separate sandboxed process. Implements `NSFileProviderReplicatedExtension`. Owns all JMAP communication (API calls, blob transfers, SSE push). May be terminated at any time by the system; must persist all important state to the shared container.

**App Group Shared Container** — Both processes access a shared container (SQLite database for node metadata, Keychain for auth tokens, UserDefaults for configuration). This is the only reliable IPC mechanism.

### Why Not Two Long-Lived Processes

On Windows, the sync engine runs as a persistent background service because cfapi requires a connected long-lived process. On Apple, the system manages the extension lifecycle:

1. The system launches the extension on demand (directory opened, file accessed, push notification received)
2. The system may terminate the extension at any time (memory pressure, inactivity)
3. The system handles placeholders, eviction, and caching independently
4. No need for a separate service — the extension IS the sync engine

The containing app is only needed for account management and settings. It can be closed without affecting sync.

---

## 2. Project Structure

```
apple/
├── DESIGN.md                          # This file
├── CLAUDE.md                          # Build instructions
├── FastmailFiles/                     # Xcode project root
│   ├── FastmailFiles.xcodeproj
│   ├── Shared/                        # Code shared between app and extension
│   │   ├── JmapClient.swift           # JMAP HTTP client (session, API calls)
│   │   ├── JmapTypes.swift            # FileNode, Blob, Session models
│   │   ├── NodeDatabase.swift         # SQLite wrapper for node cache
│   │   ├── AuthManager.swift          # Token storage/refresh (Keychain)
│   │   └── Constants.swift            # Capability URIs, defaults
│   ├── App/                           # Containing app target
│   │   ├── FastmailFilesApp.swift     # SwiftUI app entry
│   │   ├── AccountView.swift          # Account management UI
│   │   ├── StatusView.swift           # Sync status display
│   │   └── Info.plist
│   ├── FileProvider/                  # FileProvider extension target
│   │   ├── FileProviderExtension.swift  # NSFileProviderReplicatedExtension
│   │   ├── FileProviderItem.swift     # NSFileProviderItem implementation
│   │   ├── FileProviderEnumerator.swift # Enumeration logic
│   │   ├── SyncEngine.swift           # JMAP sync orchestration
│   │   ├── BlobTransfer.swift         # Upload/download with progress
│   │   ├── PushWatcher.swift          # SSE push connection
│   │   └── Info.plist
│   └── FileProviderUI/               # FileProviderUI extension (auth prompts)
│       ├── AuthViewController.swift
│       └── Info.plist
```

### Language & Frameworks

- **Language:** Swift (6.0+, strict concurrency)
- **Minimum deployment:** macOS 12.0 / iOS 16.0 (for `NSFileProviderReplicatedExtension`)
- **UI:** SwiftUI (containing app)
- **Networking:** URLSession (async/await)
- **Database:** SQLite via swift-sqlite or GRDB
- **Keychain:** Security framework (shared access group)

### Shared Code Strategy

The JMAP protocol layer (`JmapClient`, `JmapTypes`), node database, and auth manager are compiled into both the app and extension targets. They live in a shared directory and are added to both targets in Xcode. No framework embedding needed — just shared source files.

---

## 3. FileProvider Extension

### NSFileProviderReplicatedExtension

The extension class conforms to:

- `NSFileProviderReplicatedExtension` — core sync protocol
- `NSFileProviderEnumerating` — enumeration support
- `NSFileProviderThumbnailing` — thumbnail generation

```
Required methods:
  init(domain:)                    — initialize for a domain (account)
  invalidate()                     — cleanup before termination
  item(for:request:completionHandler:) — return cached item metadata
  fetchContents(for:version:request:completionHandler:) — download file
  createItem(basedOn:fields:contents:options:request:completionHandler:) — upload new item
  modifyItem(_:baseVersion:changedFields:contents:options:request:completionHandler:) — update item
  deleteItem(identifier:baseVersion:options:request:completionHandler:) — delete item
  enumerator(for:request:)         — return enumerator for a container
```

### Domain ↔ Account Mapping

Each JMAP account maps to one `NSFileProviderDomain`. The domain identifier is the JMAP `accountId`. The containing app registers/removes domains:

```
Add account:    NSFileProviderManager.add(domain)
Remove account: NSFileProviderManager.remove(domain, mode: .removeAll)
```

On macOS, each domain creates a directory under `~/Library/CloudStorage/FastmailFiles-DisplayName/`. On iOS, the domain appears as a location in the Files app.

### Extension Lifecycle

The extension process is transient:

1. System launches the extension when it needs enumeration, hydration, or has local changes to propagate
2. `init(domain:)` is called — open database, load auth tokens, initialize JMAP client
3. System calls methods as needed
4. If idle too long, system calls `invalidate()` and terminates the process
5. On next need, the cycle repeats from step 1

**Implications:**
- All state must be in the shared SQLite database, not in-memory
- The SSE push connection must be re-established each time the extension launches
- Auth tokens must be in the shared Keychain, not in-memory
- The extension should be fast to initialize (no heavy startup work)

### NSFileProviderItemIdentifier Mapping

| System Identifier | JMAP Equivalent |
|-------------------|----------------|
| `.rootContainer` | `role: "home"` node ID |
| `.trashContainer` | `role: "trash"` node ID |
| `.workingSet` | All known nodes |
| Custom string | `FileNode.id` |

The extension must translate between these. On `init(domain:)`, look up `homeNodeId` and `trashNodeId` from the database (or fetch from server if cold start).

---

## 4. Enumeration & Sync

### How Remote Sync Works (Key Difference from Windows)

On Windows, the sync engine directly manipulates placeholders on disk. On Apple, the flow is:

1. SSE push arrives → extension calls `NSFileProviderManager.signalEnumerator(for: .workingSet)`
2. System calls `enumerateChanges(for: .workingSet, from: syncAnchor)` on your enumerator
3. Your enumerator calls `FileNode/changes` + `FileNode/get` using the JMAP state token
4. Report changes via `observer.didUpdate([items])` and `observer.didDeleteItemsWithIdentifiers([ids])`
5. System applies changes to disk (creates placeholders, updates metadata, removes items)

You never touch the filesystem. The system does it for you.

### Enumerator Types

The system requests enumerators for different containers:

| Container | When Requested | What to Return |
|-----------|---------------|----------------|
| `.rootContainer` | Finder opens the sync root | Children of the home node |
| `.trashContainer` | Finder opens trash | Children of the trash node |
| `.workingSet` | System wants remote changes | All changed items (the main sync path) |
| Folder identifier | Finder opens a subfolder | Children of that folder |

### Initial Enumeration (enumerateItems)

Called when the system has no cached state for this container.

```
enumerateItems(for observer, startingAt page):
  If page is .initialPageSortedByName:
    → FileNode/query { filter: { parentIds: [containerId] }, sort: [{ property: "name" }] }
    → FileNode/get { #ids: resultOf query }
    → Convert to NSFileProviderItem array
    → observer.didEnumerate(items)
    → observer.finishEnumerating(upTo: nextPage or nil)
```

For the working set, enumerate all nodes (not just one folder):

```
enumerateItems(for observer, startingAt page):
  If container is .workingSet:
    → Paginated FileNode/query + FileNode/get for ALL nodes
    → observer.didEnumerate(items) in batches
    → observer.finishEnumerating(upTo: nextPage or nil)
    → Save state token as new sync anchor
```

### Change Enumeration (enumerateChanges)

Called after `signalEnumerator` to fetch incremental changes.

```
enumerateChanges(for observer, from syncAnchor):
  Deserialize JMAP state token from syncAnchor
  → FileNode/changes { sinceState: stateToken }

  If cannotCalculateChanges:
    → observer.finishEnumeratingWithError(.syncAnchorExpired)
    → System will restart with full enumeration

  For each created/updated node:
    → Convert to NSFileProviderItem
    → observer.didUpdate([item])

  For each destroyed node:
    → observer.didDeleteItemsWithIdentifiers([id])

  → observer.finishEnumeratingChanges(
      upTo: newSyncAnchor,
      moreComing: hasMoreChanges
    )
  → Save new state token to database
```

### Sync Anchor ↔ JMAP State Token

The sync anchor is opaque `Data`. Serialize the JMAP state token string as UTF-8:

```swift
// State token → sync anchor
let anchor = NSFileProviderSyncAnchor(stateToken.data(using: .utf8)!)

// Sync anchor → state token
let stateToken = String(data: anchor.rawValue, encoding: .utf8)!
```

### Triggering Sync

Two paths to trigger remote change polling:

**SSE Push (primary):** The extension maintains an SSE connection to `eventSourceUrl`. On receiving a `state` event with a changed FileNode state token:

```swift
NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { error in
    // System will call enumerateChanges
}
```

**Periodic poll (fallback):** If SSE disconnects, the extension should poll periodically. Use `NSFileProviderManager.reimportItems(below:)` or just signal the working set enumerator on a timer as a fallback.

### SSE Push Connection Lifecycle

Because the extension is transient, the SSE connection needs careful management:

1. On `init(domain:)`, start the SSE connection
2. On `invalidate()`, close the SSE connection
3. Use exponential backoff for reconnection (1s → 60s, reset on success)
4. On reconnect, signal the working set enumerator to catch up on missed changes
5. Consider running the SSE connection in the containing app instead (it lives longer) and using a shared database flag or `DistributedNotificationCenter` to wake the extension

**Alternative: App-hosted push.** The containing app can maintain the SSE connection (since it may run longer than the extension) and signal the extension's enumerator via `NSFileProviderManager.signalEnumerator`. This avoids re-establishing SSE every time the extension launches. The tradeoff is the app must be running for push to work — but the system will also poll changes on its own schedule.

---

## 5. File Download (Hydration)

### fetchContents

When the user opens a dehydrated file, the system calls:

```swift
func fetchContents(for itemIdentifier: NSFileProviderItemIdentifier,
                   version requestedVersion: NSFileProviderItemVersion?,
                   request: NSFileProviderRequest,
                   completionHandler: @escaping (URL?, NSFileProviderItem?, Error?) -> Void
) -> Progress
```

The extension must:

1. Look up the node in the database (get `blobId`, `size`, `type`)
2. Download the blob to a temporary file
3. Call `completionHandler(tempFileURL, updatedItem, nil)`
4. The system clones the file content and deletes the temp file

### Download Decision Tree

```
fetchContents called →
  Look up node in database → blobId, size, type

  Size ≤ 16KB AND Blob capability available?
    → Blob/get inline (base64 + digest)
    → Write decoded data to temp file

  Else:
    → HTTP GET {downloadUrl}/{accountId}/{blobId}/{name}
    → Stream response body to temp file
    → Concurrent Blob/get for digest verification (if Blob capability available)

  → Return (tempFileURL, item, nil)
```

### Progress & Cancellation

```swift
let progress = Progress(totalUnitCount: Int64(fileSize))

// During download:
progress.completedUnitCount = bytesReceived

// Handle cancellation:
progress.cancellationHandler = {
    downloadTask.cancel()
}

return progress
```

The system uses the Progress object to show download progress in Finder/Files. If the user cancels (closes the file, navigates away), the system triggers the cancellation handler.

### Streaming to Temp File

Stream the HTTP response body to a temp file in the App Group shared container. Do not buffer the entire file in memory:

```
Temp file location: appGroupContainer/tmp/{itemIdentifier}-{UUID}.download
```

Use `URLSession` with a download task for automatic temp file management, or a data task with manual streaming for finer progress control.

### Concurrent Downloads

Control download concurrency via `NSExtensionFileProviderDownloadPipelineDepth` in the extension's Info.plist (macOS 12.3+). Default is 1; set to 4 to match the Windows client's interactive queue depth.

---

## 6. File Upload & Local Changes

### How Local Changes Arrive

Unlike Windows (where a `FileChangeWatcher` detects changes), on Apple platforms the system tells you what changed via three methods:

| Method | When Called | JMAP Action |
|--------|-----------|-------------|
| `createItem` | User creates a new file/folder | `Blob/set` + `FileNode/set create` |
| `modifyItem` | User edits, renames, or moves a file | `Blob/set` + `FileNode/set create` (content change) or `FileNode/set update` (rename/move) |
| `deleteItem` | User deletes a file | `FileNode/set update` (→ trash) or `FileNode/set destroy` |

### No Outbox Needed

The system handles the outbox role:

- It queues local changes and delivers them to the extension one at a time per item
- If the extension is terminated, pending changes are re-delivered on next launch
- If the extension returns an error, the system retries with backoff
- The system handles coalescing (multiple rapid edits → one `modifyItem` call)

This eliminates the need for a persistent outbox file (`outbox.json`) and all the coalescing logic from the Windows client.

### createItem

```swift
func createItem(basedOn itemTemplate: NSFileProviderItem,
                fields: NSFileProviderItemFields,
                contents url: URL?,
                options: NSFileProviderCreateItemOptions,
                request: NSFileProviderRequest,
                completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
) -> Progress
```

**For files:**
1. Read content from `url` (system-provided temp file)
2. Upload blob: `POST {uploadUrl}/{accountId}/` → `blobId`
3. Create node: `FileNode/set create { parentId, blobId, name, type, created, modified, onExists: "replace" }`
4. Save node to database
5. Return the created item with the server-assigned identifier

**For folders:**
1. Create node: `FileNode/set create { parentId, name }`
2. Save node to database
3. Return the created item

**Important:** The returned item's `itemIdentifier` is the server's `FileNode.id`, which will differ from the template's identifier. The system handles the remapping. Set the `shouldFetchContent` parameter of the completion handler to `false` (we already have the content).

**Timestamps:** Pass the template item's `creationDate` and `contentModificationDate` to the server as `created` and `modified`. This preserves local timestamps on the server, just as the Windows client does.

### modifyItem

```swift
func modifyItem(_ item: NSFileProviderItem,
                baseVersion version: NSFileProviderItemVersion,
                changedFields: NSFileProviderItemFields,
                contents newContents: URL?,
                options: NSFileProviderCreateItemOptions,
                request: NSFileProviderRequest,
                completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void
) -> Progress
```

Check `changedFields` to determine what changed:

**Content change** (`.contents` in changedFields):
1. Upload new blob → `blobId`
2. `FileNode/set update { [nodeId]: { blobId, type, modified } }` — nodeId stays the same (v10+)
3. Update database with new blobId (same nodeId)
4. Return the same item — no identifier remap needed

**Rename** (`.filename` in changedFields):
1. `FileNode/set update { [nodeId]: { name: newName } }`
2. Update database
3. Return updated item

**Move** (`.parentItemIdentifier` in changedFields):
1. `FileNode/set update { [nodeId]: { parentId: newParentId } }`
2. Update database
3. Return updated item

**Move to trash** (`.parentItemIdentifier` == `.trashContainer`):
1. `FileNode/set update { [nodeId]: { parentId: trashNodeId }, onExists: "rename" }`
2. Update database
3. Return updated item

**Combined changes** (e.g., rename + content):
Handle in the correct order: upload content first (which creates a new node), then rename/move if needed.

### Chunked Upload (Large Files)

Same strategy as Windows (see DESIGN.md §5), adapted for URLSession:

1. Gate on `HasBlob` capability
2. Calculate effective chunk size (double from base until within `maxDataSources`)
3. Upload chunks as separate blob POSTs
4. `Blob/set` combine with all chunk blobIds → final blobId
5. Create FileNode with combined blobId

For resumability, persist chunk blobIds in the database keyed by the item identifier. On retry, verify stored blobIds via `Blob/get`, then resume.

**HTTP/2 concern:** URLSession uses HTTP/2 by default with multiplexing. Unlike the Windows client (which forces HTTP/1.1 for uploads), URLSession doesn't expose per-request HTTP version control easily. Instead, use a separate `URLSession` instance for uploads with `httpMaximumConnectionsPerHost` set appropriately, or use `URLSessionConfiguration` with `httpShouldUsePipelining = false`. Monitor whether upload traffic starves downloads; if so, use separate URLSession instances with different `URLSessionConfiguration` settings for interactive (downloads) vs background (uploads) traffic.

---

## 7. Delete, Rename, Move

### deleteItem

```swift
func deleteItem(identifier: NSFileProviderItemIdentifier,
                baseVersion version: NSFileProviderItemVersion,
                options: NSFileProviderDeleteItemOptions,
                request: NSFileProviderRequest,
                completionHandler: @escaping (Error?) -> Void
) -> Progress
```

**Preferred path (trash available):**
```
FileNode/set update { [nodeId]: { parentId: trashNodeId }, onExists: "rename" }
```

**Fallback (no trash or `.deletionBypassesTrash` in options):**
```
FileNode/set destroy { [nodeId], onDestroyRemoveChildren: true }
```

**Recursive:** If `options` does not contain `.recursive` and the folder is non-empty, return `.directoryNotEmpty` error. The system will re-call with `.recursive`.

**Idempotent:** If the server returns `notFound`, treat as success (node already gone).

### Rename and Move

These come through `modifyItem` with `.filename` and/or `.parentItemIdentifier` in `changedFields`. See §6 above.

### Trash Restore

When the system moves an item from `.trashContainer` back to a folder, it calls `modifyItem` with `.parentItemIdentifier` changed from trash to the destination. The extension:

1. `FileNode/set update { [nodeId]: { parentId: originalParentId } }`
2. If `notFound` (node was permanently deleted from trash), return error — system will handle

---

## 8. Conflict Resolution & Echo Suppression

### Echo Suppression Is Simpler on Apple

On Windows, the sync engine directly manipulates the filesystem, which triggers its own file watcher, requiring careful mtime-based echo suppression (see DESIGN.md §6 and pitfall #6).

On Apple, the system mediates all changes:

- **Remote changes** arrive via enumeration — the system applies them to disk
- **Local changes** arrive via `createItem`/`modifyItem`/`deleteItem` — the extension propagates to server
- The system **never** calls `modifyItem` for changes the extension reported via enumeration

This eliminates the echo suppression problem entirely. No `_recentlyUploaded`, `_recentlyHydrated`, or mtime comparison maps are needed.

### Version-Based Conflict Detection

The system passes `baseVersion` to `modifyItem` and `deleteItem`. This is the version the system had when the user made the change. The extension can compare it against the current server version:

1. If `baseVersion.contentVersion` matches the server's current blobId → no conflict, proceed
2. If they differ → server was updated since the user's edit → conflict

### Conflict Resolution Strategies

**Server wins (simple):** Return the server's current item from `modifyItem`. The system will update the local file to match the server. The user's changes are lost.

**Conflict copy (safe):** Create a "Conflicted copy of..." file on the server with the user's content, then return the server's current item. The user keeps both versions.

**Last writer wins (current approach):** Just upload with `onExists: "replace"`. The server keeps whatever was written last. This matches the Windows client's behavior and is sufficient for single-user scenarios.

### Handling nodeId Changes

When content changes, `onExists: "replace"` creates a new node with a new ID. The extension must return the new item with the new identifier from `modifyItem`. The system handles the identifier remapping — the file on disk stays the same, but the system updates its internal tracking to use the new identifier.

---

## 9. Pin/Unpin & Eviction

### System-Managed Eviction

Eviction (dehydration) on Apple is fundamentally different from Windows:

- The **system decides** when to evict files (low disk space, cache policy)
- The extension is **not notified** when eviction happens
- After eviction, the next access triggers `fetchContents` to re-download
- The system only evicts files where `isUploaded == true`

The extension must accurately report `isUploaded` and `isDownloaded` status on each item.

### Content Policies (Pin/Unpin Equivalent)

The extension controls download behavior per item via two mechanisms:

**Keep downloaded (pin):** Call `NSFileProviderManager.requestDownload(for:)` or set the item's content policy to `downloadEagerlyAndKeepDownloaded`. The system downloads immediately and never evicts.

**Free up space (unpin):** Call `NSFileProviderManager.evictItem(identifier:)` to remove local content. The item reverts to a placeholder. Will fail if the item has unsynced changes.

**Lazy download (default):** Items start as placeholders. Content is downloaded on first access and may be evicted by the system when space is needed.

### Handling Pin Requests from User

If we implement a "Keep offline" / "Free up space" context menu action (via FileProviderUI):

1. User selects "Keep offline" → UI extension calls `NSFileProviderManager.requestDownload(for:)`
2. System downloads the file (calling `fetchContents`)
3. System marks as non-evictable
4. User selects "Free up space" → UI extension calls `NSFileProviderManager.evictItem(identifier:)`

### No Pin Propagation Race

Unlike Windows (DESIGN.md pitfall #12), there is no pin propagation race on Apple. The system handles pin state per-item atomically. No need for `CfSetPinState(UNSPECIFIED)` workarounds or dehydration retry loops.

---

## 10. Thumbnails

### NSFileProviderThumbnailing

The extension class adopts `NSFileProviderThumbnailing`:

```swift
func fetchThumbnails(
    for itemIdentifiers: [NSFileProviderItemIdentifier],
    requestedSize: CGSize,
    perThumbnailCompletionHandler: @escaping (NSFileProviderItemIdentifier, Data?, Error?) -> Void,
    completionHandler: @escaping (Error?) -> Void
) -> Progress
```

Called in batches. For each item:

1. Look up `blobId` from the database (no hydration!)
2. If `blob2` capability available:
   - `Blob/convert { create: { "t0": { imageConvert: { blobId, width, height, type: "image/png", autoOrient: true } } } }`
   - Download the resulting thumbnail blob
   - Call `perThumbnailCompletionHandler(identifier, pngData, nil)`
3. If `blob2` not available:
   - Call `perThumbnailCompletionHandler(identifier, nil, nil)` — system shows generic icon
4. After all items: call `completionHandler(nil)`

### Caching

Cache thumbnails in an LRU cache in the database or a dedicated cache directory in the App Group container, keyed by `(blobId, width, height)`. Since blobId changes whenever file content changes, cached thumbnails keyed by blobId never go stale for a given version of the content.

---

## 11. Authentication & Session Management

### Token Storage

Store auth tokens in the shared Keychain (accessible to both app and extension via a shared access group):

```
Keychain access group: $(TeamIdentifierPrefix)com.fastmail.files.shared
```

### Session Discovery

Same as DESIGN.md §10. On extension `init(domain:)`:

1. Read token from shared Keychain
2. Fetch JMAP session from session URL
3. Find primary account for FileNode capability
4. Cache session data in shared UserDefaults (for fast startup)
5. Re-fetch session on 401 or capability change

### Auth Failure Handling

When the extension encounters a 401 or invalid token:

1. Return `NSFileProviderError(.notAuthenticated)` from the failing operation
2. The system shows an authentication banner in Finder/Files
3. Implement `FPUIActionExtensionViewController.prepare(forError:)` in the FileProviderUI extension
4. Present a login/re-auth UI
5. On successful auth, store new token in Keychain
6. Call `NSFileProviderManager.signalErrorResolved(NSFileProviderError(.notAuthenticated))`
7. System retries the failed operation

### OAuth2 (Future)

Same discovery flow as DESIGN.md §10. The containing app handles the OAuth flow (ASWebAuthenticationSession for the auth code exchange), stores tokens in Keychain, and the extension reads them.

---

## 12. Containing App ↔ Extension Communication

### Shared State (Primary)

The shared App Group container holds:

| What | Storage | Access Pattern |
|------|---------|---------------|
| Node metadata | SQLite database | Extension writes, app reads for status |
| Auth tokens | Keychain (shared group) | App writes, extension reads |
| JMAP session cache | UserDefaults (shared suite) | Both read/write |
| Sync state token | SQLite database | Extension writes |
| Account config | UserDefaults (shared suite) | App writes, extension reads |
| Error/status | SQLite database or UserDefaults | Extension writes, app reads |

### App → Extension Signals

**Domain management:**
```swift
// App adds an account
let domain = NSFileProviderDomain(identifier: .init(rawValue: accountId), displayName: displayName)
try await NSFileProviderManager.add(domain)

// App removes an account
try await NSFileProviderManager.remove(domain, mode: .removeAll)
```

**Trigger sync:**
```swift
// Signal the extension to check for changes
NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet)
```

**Reimport (force full refresh):**
```swift
NSFileProviderManager(for: domain)?.reimportItems(below: .rootContainer)
```

### Extension → App Signals

**Status updates:** Write current status to shared UserDefaults or SQLite. The app polls or observes changes:

```swift
// Extension writes
UserDefaults(suiteName: appGroupId)?.set("syncing", forKey: "status-\(accountId)")

// App observes
UserDefaults(suiteName: appGroupId)?.addObserver(self, forKeyPath: "status-\(accountId)", ...)
```

**DistributedNotificationCenter (macOS only):**
```swift
// Extension posts
DistributedNotificationCenter.default().post(name: .init("com.fastmail.files.statusChanged"), object: nil)

// App observes
DistributedNotificationCenter.default().addObserver(...)
```

### XPC Services (Advanced)

For richer communication, the extension can expose XPC services:

1. Extension adopts `NSFileProviderServicing`
2. Exposes `NSFileProviderServiceSource` with an `NSXPCListener`
3. App retrieves services via `FileManager.fileProviderServicesForItem(at:)`
4. Opens `NSXPCConnection` to the extension

Use cases: detailed sync status, outbox contents, manual sync trigger, error details.

---

## 13. Error Handling & Resilience

### Error Classification

The FileProvider system has built-in error handling with retry:

| Extension Error | System Behavior | JMAP Source |
|----------------|-----------------|-------------|
| `NSFileProviderError(.notAuthenticated)` | Shows auth banner, pauses sync | HTTP 401 |
| `NSFileProviderError(.serverUnreachable)` | Shows offline badge, retries | Network timeout |
| `NSFileProviderError(.insufficientQuota)` | Shows quota error | Quota exceeded |
| `NSFileProviderError(.syncAnchorExpired)` | Restarts full enumeration | `cannotCalculateChanges` |
| `NSFileProviderError(.filenameCollision)` | System handles rename | Name conflict |
| `NSFileProviderError(.directoryNotEmpty)` | System retries with `.recursive` | Non-empty folder delete |
| `NSFileProviderError(.pageExpired)` | Restarts enumeration from scratch | Stale pagination token |
| `NSCocoaError(.fileWriteOutOfSpace)` | System pauses downloads | Disk full |

### Mapping JMAP Errors

| JMAP Error | FileProvider Error |
|------------|-------------------|
| HTTP 401 | `.notAuthenticated` |
| HTTP 403 / `forbidden` | `.notAuthenticated` (likely expired token) |
| HTTP 413 / `maxSizeBlobSet` exceeded | `.insufficientQuota` or custom error with user-visible message |
| HTTP 5xx / timeout | `.serverUnreachable` (system retries automatically) |
| `cannotCalculateChanges` | `.syncAnchorExpired` |
| `notFound` on delete/move | Success (idempotent) |
| `notFound` on fetch | `.noSuchItem` |
| Rate limit (`urn:ietf:params:jmap:error:limit`) | `.serverUnreachable` (triggers retry) |

### Retry Strategy

The system handles retry for most errors automatically. The extension should:

- Return errors promptly (don't retry internally for transient network errors — let the system handle it)
- Only retry internally for specific JMAP patterns (e.g., re-fetch session on 401 before giving up)
- Use `NSFileProviderError(.cannotSynchronize)` for unrecoverable per-item errors

### SSE Reconnection

Same strategy as DESIGN.md §12: exponential backoff (1s → 60s), reset on successful connection. On reconnect, signal the working set enumerator.

---

## 14. Offline & Network Resilience

### Offline Behavior Overview

The Apple FileProvider model has strong inherent offline support because the system manages the on-disk state independently from the extension. When the network is down:

- **Hydrated files work fully offline.** They are real files on disk. Users can read, edit, rename, move, and delete them without any network.
- **Dehydrated files cannot be opened.** The system calls `fetchContents`, the extension returns `.serverUnreachable`, and Finder/Files shows "The file isn't available offline."
- **Local changes are queued by the system.** Edits, creates, renames, and deletes are persisted by the system and delivered to the extension when it can process them.
- **No data loss.** The system never discards queued changes, even across device reboots.

### Network State Detection

Use `NWPathMonitor` (Network framework) to track connectivity:

```
Extension init(domain:):
  Start NWPathMonitor on a dedicated queue
  Store current path status (satisfied / unsatisfied / requiresConnection)
  Store current connection type (wifi / cellular / wiredEthernet / other)
  Store isExpensive and isConstrained flags

Path update handler:
  On transition to satisfied:
    → Re-establish SSE push connection
    → Signal working set enumerator (catch up on missed changes)
    → Resume background transfers if paused for network reasons
  On transition to unsatisfied:
    → Close SSE connection cleanly (don't let it timeout)
    → Mark extension as offline in shared state (for containing app UI)
    → Don't return errors for in-progress operations yet — let URLSession fail naturally
  On isExpensive/isConstrained change:
    → Re-evaluate bandwidth policy (see below)
```

Store the network state in shared UserDefaults so the containing app can display accurate status without its own monitor.

### Bandwidth-Aware Transfer Policy

Not all connections are equal. The extension should distinguish between full-bandwidth and constrained connections and adjust behavior accordingly.

#### Connection Classification

```
NWPath properties:
  isExpensive    — true for cellular, Personal Hotspot, some Wi-Fi (user-marked)
  isConstrained  — true when Low Data Mode is enabled by the user

Classify into three tiers:
  Unrestricted  — !isExpensive && !isConstrained (home/office Wi-Fi, Ethernet)
  Expensive     — isExpensive && !isConstrained (cellular with normal data plan)
  Constrained   — isConstrained (Low Data Mode active, regardless of connection type)
```

#### Per-Tier Behavior

| Operation | Unrestricted | Expensive | Constrained |
|-----------|-------------|-----------|-------------|
| **Interactive hydration** (user opens a file) | Full speed | Full speed | Full speed |
| **Background hydration** (pin, Spotlight) | Full speed | Pause | Pause |
| **Uploads** (local edits → server) | Full speed | Small files only (≤ 1 MB) | Pause |
| **Chunked uploads** (large files) | Full speed | Pause | Pause |
| **Change enumeration** (metadata sync) | Full speed | Full speed | Full speed |
| **Thumbnail fetches** | Full speed | On-demand only | Pause |
| **SSE push connection** | Always on | Always on | Always on |

**Key principle: never block interactive user actions.** If the user explicitly opens a file, download it regardless of connection type. Only pause *background* operations — things the user didn't directly request right now.

#### Implementation Strategy

The extension maintains a `TransferPolicy` that gates background operations:

```
TransferPolicy:
  allowBackgroundDownloads: Bool    — gates pin hydration, Spotlight pre-fetching
  allowBackgroundUploads: Bool      — gates outbox processing for large files
  smallUploadThreshold: Int         — max bytes for uploads on expensive connections (1 MB)
  allowThumbnails: Bool             — gates non-interactive thumbnail fetches

Update on NWPath change:
  unrestricted → all true, threshold irrelevant
  expensive    → downloads false, uploads true (small only), thumbnails false
  constrained  → all false except interactive
```

#### Distinguishing Interactive from Background

When `fetchContents` is called, the extension needs to know if it's an interactive request (user opened the file) or a background request (pin hydration, Spotlight indexing):

- `NSFileProviderRequest.isFileViewerRequest` — true if the user is directly opening the file (Finder, an app, Quick Look). Always allow.
- Other requests (Spotlight indexing, pin-initiated hydration) — check `TransferPolicy.allowBackgroundDownloads`.

For uploads via `createItem`/`modifyItem`:
- Always allow if the file is small (≤ `smallUploadThreshold` on expensive connections)
- For large files on expensive/constrained connections: return `.serverUnreachable` so the system retries later. The system treats this as transient and will re-deliver when conditions improve.

**Caution:** Returning `.serverUnreachable` for uploads on expensive connections means the system retries with its own backoff schedule. The extension must re-check the transfer policy on each retry — if the connection has become unrestricted, proceed normally. Don't permanently block uploads just because the connection was expensive when the change was first attempted.

#### User Override

The containing app should offer a "Sync on cellular" toggle (stored in shared UserDefaults):

```
Settings:
  syncOnCellular: Bool (default: false)
    true  → treat Expensive same as Unrestricted
    false → apply Expensive tier restrictions

  respectLowDataMode: Bool (default: true)
    true  → apply Constrained tier restrictions
    false → treat Constrained same as Expensive
```

When the user changes these settings, post a `DistributedNotification` (macOS) or update shared UserDefaults and signal the extension to re-evaluate its transfer policy.

### Offline State Machine

```
[Online] ←→ [Offline] ←→ [Expensive/Constrained]

Online:
  SSE connected, all transfers active
  Status: "Up to date" or "Syncing..."

Offline:
  SSE disconnected, no transfers possible
  System queues all local changes
  Status: "Offline — changes will sync when connected"
  On network recovery → transition to Online or Expensive

Expensive (cellular, no user override):
  SSE connected, metadata sync active
  Interactive hydration active
  Background downloads paused
  Large uploads paused, small uploads active
  Status: "On cellular — background sync paused"

Constrained (Low Data Mode):
  SSE connected, metadata sync active
  Interactive hydration active
  All background transfers paused
  Status: "Low Data Mode — background sync paused"
```

### Pending Changes Visibility

The system tracks pending local changes. The containing app can enumerate them:

```swift
// In the containing app
let manager = NSFileProviderManager(for: domain)
let enumerator = try manager.enumeratorForPendingItems()
// Enumerate to get items waiting to sync
```

Display these in the app UI:
- Number of pending uploads/creates/deletes
- Individual file names and change types
- Error details for failed items

The extension also writes summary stats to shared UserDefaults for the menu bar status item:

```
pending-uploads-{accountId}: 3
pending-downloads-{accountId}: 0
last-sync-{accountId}: "2026-03-12T10:30:00Z"
network-state-{accountId}: "expensive"
transfer-policy-{accountId}: "restricted"
```

### Recovery After Extended Offline

When the device comes back online after a long period:

1. **SSE reconnects** with exponential backoff (immediate on network recovery, then 1s → 60s if server is slow to respond)
2. **Signal working set enumerator** — triggers `enumerateChanges` with the stale sync anchor
3. **State token may have expired** — if `FileNode/changes` returns `cannotCalculateChanges`, return `.syncAnchorExpired` and the system falls back to full enumeration
4. **System replays queued local changes** — calls `createItem`/`modifyItem`/`deleteItem` for everything that was queued offline
5. **Conflicts are possible** — a file edited offline may have also been edited on the server. Handle via the conflict strategy in §8 (last-writer-wins with `onExists: "replace"`, or conflict copies for detected conflicts)

### iOS-Specific: Background Refresh

On iOS, the extension may not be running when the device comes back online. Two mechanisms ensure timely sync:

**Push Notifications (primary):**
The server sends a silent APNS push when files change. The containing app handles it and signals the extension's enumerator:

```swift
func application(_ application: UIApplication,
                 didReceiveRemoteNotification userInfo: [AnyHashable: Any],
                 fetchCompletionHandler completionHandler: @escaping (UIBackgroundFetchResult) -> Void) {
    let manager = NSFileProviderManager(for: domain)
    manager?.signalEnumerator(for: .workingSet) { _ in
        completionHandler(.newData)
    }
}
```

**Background App Refresh (fallback):**
Register for `BGAppRefreshTask` to periodically wake and signal the extension:

```swift
BGTaskScheduler.shared.register(forTaskWithIdentifier: "com.fastmail.files.refresh", using: nil) { task in
    let manager = NSFileProviderManager(for: domain)
    manager?.signalEnumerator(for: .workingSet) { _ in
        task.setTaskCompleted(success: true)
    }
}
```

Schedule refreshes at reasonable intervals (15-30 minutes). iOS decides the actual timing based on usage patterns and battery.

### macOS-Specific: Menu Bar Status

The containing app's menu bar status item reflects the current state:

| State | Icon | Menu Text |
|-------|------|-----------|
| Up to date | Cloud checkmark | "All files up to date" |
| Syncing | Animated arrows | "Syncing 3 files..." |
| Pending (offline) | Cloud with slash | "Offline — 5 changes pending" |
| Pending (expensive) | Cloud with pause | "On cellular — 3 uploads paused" |
| Pending (constrained) | Cloud with pause | "Low Data Mode — sync paused" |
| Error | Cloud with exclamation | "Authentication required" / error detail |

---

## 15. Permissions & Read-Only Folders

### NSFileProviderItemCapabilities

Map JMAP permissions to FileProvider capabilities:

```swift
var capabilities: NSFileProviderItemCapabilities {
    var caps: NSFileProviderItemCapabilities = [.allowsReading]

    if myRights.mayWrite {
        caps.insert(.allowsWriting)
        caps.insert(.allowsRenaming)
        caps.insert(.allowsReparenting)
    }
    if myRights.mayWrite {
        caps.insert(.allowsTrashing)
        caps.insert(.allowsDeleting)
    }

    // Always allow eviction for hydrated files
    caps.insert(.allowsEvicting)

    return caps
}
```

When a capability is not set, the system prevents the operation in Finder/Files (greys out menu items, blocks drag-and-drop, etc.). This is cleaner than the Windows approach of NTFS ACLs + callback veto — the system enforces permissions at the UI level.

### Read-Only Inheritance

If a folder has `mayWrite: false`, all items enumerated within it should also omit write capabilities. Track read-only folder IDs in the database and check ancestry when building item capabilities.

---

## 16. File Naming & Sanitization

### macOS/iOS Invalid Characters

| Character | Issue |
|-----------|-------|
| `/` | Path separator on both macOS and iOS — forbidden in filenames |
| `:` | Finder displays `:` as `/` internally (HFS+ legacy) — forbidden in filenames |
| Null byte | Always forbidden |

Much less restrictive than Windows. No reserved names (CON, PRN, etc.). No trailing dot/space issues.

### Sanitization Strategy

1. Replace `/` with `∕` (U+2215 DIVISION SLASH) or `_`
2. Replace `:` with `꞉` (U+A789 MODIFIER LETTER COLON) or `_`
3. Maintain a mapping in the database if the local name differs from the server name
4. On upload, use the original server name (or the user's intended name)

### Path Length

macOS: 1024 bytes per path component, 1024 characters total path (NAME_MAX / PATH_MAX). Less constraining than Windows (260 char default).

### Bundle Handling (macOS-specific)

macOS treats certain directory structures as opaque "bundles" (`.app`, `.rtfd`, `.bundle`, etc.). FileProvider may present these as single items. The extension should treat bundles as directories on the JMAP side (upload/download all contents) but present them as single items to the system.

---

## 17. Platform Differences (macOS vs iOS)

### API Availability

| Feature | macOS | iOS |
|---------|-------|-----|
| `NSFileProviderReplicatedExtension` | macOS 11+ | iOS 16+ |
| Partial content fetching | macOS 12.3+ | Not available |
| Sidebar integration | Finder sidebar under "Locations" | Files app sidebar |
| Storage location | `~/Library/CloudStorage/` | Managed by system |
| Eviction | Full support | Full support |
| Context menus | FinderSync or FileProviderUI actions | FileProviderUI actions |
| Thumbnails | `NSFileProviderThumbnailing` | `NSFileProviderThumbnailing` |
| XPC Services | `NSFileProviderServicing` | `NSFileProviderServicing` |

### App Group Identifier Format

| Platform | Format | Example |
|----------|--------|---------|
| macOS | `$(TeamIdentifierPrefix)com.fastmail.files` | `ABCDE12345.com.fastmail.files` |
| iOS | `group.com.fastmail.files` | `group.com.fastmail.files` |

Use conditional compilation or a shared constant to handle this difference.

### Containing App UI

**macOS:**
- Menu bar status item (like the Windows tray icon)
- Shows sync status, pending changes, account management
- Login window for account setup
- Optionally: a full Settings window

**iOS:**
- Standard iOS app with navigation
- Account setup and management
- Sync status display
- Settings
- Push notification registration for remote change alerts when the app is backgrounded

### Background Execution (iOS)

On iOS, the extension is the only reliable way to run background sync. The containing app cannot maintain a persistent SSE connection. Options:

1. **Push notifications:** Server sends an APNS push when files change. The app handles `application:didReceiveRemoteNotification:fetchCompletionHandler:` and signals the extension's enumerator.
2. **Background App Refresh:** Request periodic background time to poll for changes.
3. **Extension-initiated SSE:** The extension maintains its own SSE connection while alive. When terminated, push notifications fill the gap.

### Shared Codebase

Most code is shared between macOS and iOS:

| Component | Shared? | Notes |
|-----------|---------|-------|
| JMAP client | Yes | URLSession works on both |
| Node database | Yes | SQLite works on both |
| Auth manager | Yes | Keychain API is the same |
| FileProvider extension | 95% | Minor `#if os(macOS)` for partial fetching |
| Containing app UI | No | SwiftUI but different navigation patterns |
| Push handling | No | APNS on iOS, SSE-only on macOS |

---

## 18. Build & Distribution

### Xcode Project Setup

```
Targets:
  1. FastmailFiles (macOS app)
  2. FastmailFiles (iOS app)
  3. FastmailFilesFileProvider (macOS extension)
  4. FastmailFilesFileProvider (iOS extension)
  5. FastmailFilesFileProviderUI (macOS UI extension)
  6. FastmailFilesFileProviderUI (iOS UI extension)
```

Each extension target is embedded in its corresponding app target.

### Entitlements

**App:**
```xml
<key>com.apple.security.application-groups</key>
<array>
    <string>$(TeamIdentifierPrefix)com.fastmail.files</string>
</array>
<key>com.apple.developer.fileprovider.testing-mode</key>
<true/>  <!-- Development only -->
```

**Extension:**
```xml
<key>com.apple.security.application-groups</key>
<array>
    <string>$(TeamIdentifierPrefix)com.fastmail.files</string>
</array>
```

### Info.plist (Extension)

```xml
<key>NSExtension</key>
<dict>
    <key>NSExtensionPointIdentifier</key>
    <string>com.apple.fileprovider-nonui</string>
    <key>NSExtensionPrincipalClass</key>
    <string>$(PRODUCT_MODULE_NAME).FileProviderExtension</string>
    <key>NSExtensionFileProviderDocumentGroup</key>
    <string>$(TeamIdentifierPrefix)com.fastmail.files</string>
    <key>NSExtensionFileProviderSupportsEnumeration</key>
    <true/>
</dict>
<key>NSExtensionFileProviderDownloadPipelineDepth</key>
<integer>4</integer>
```

### Development Testing

```bash
# Build and run (Xcode)
# 1. Select the app scheme
# 2. Run — the extension is embedded automatically
# 3. Add a domain via the app UI
# 4. Open Finder → sidebar → your domain name

# Debug the extension separately:
# 1. In Xcode, select the extension scheme
# 2. Set "Ask on Launch" for the executable
# 3. Run, then trigger the extension (open the sync folder in Finder)

# Reset extension state:
# macOS: Delete ~/Library/CloudStorage/FastmailFiles-*/
# Both: Remove domain via NSFileProviderManager.remove()
```

### Distribution

- **macOS:** Direct distribution (notarized .app) or Mac App Store
- **iOS:** App Store only (TestFlight for beta)
- Both require Apple Developer Program membership for FileProvider entitlements

---

## 19. Pitfalls & Lessons Learned

These are anticipated issues based on the Windows client experience and Apple FileProvider documentation. They should be validated during implementation.

### Architecture Pitfalls

**1. The extension is transient — persist everything.**
Unlike the Windows service (which runs continuously), the FileProvider extension can be launched and terminated at any time. All state (node cache, state tokens, in-progress transfer metadata) must be in the shared SQLite database. In-memory caches are optimization only — they must be rebuildable from the database on every `init(domain:)`.

**2. Database access must handle concurrent access.**
Both the app and extension may access the shared SQLite database simultaneously. Use WAL mode and handle `SQLITE_BUSY` properly. Consider using a single-writer pattern or GRDB's `DatabasePool`.

**3. Don't touch ~/Library/CloudStorage directly.**
The system owns the sync root directory. If the extension or app directly creates, modifies, or deletes files there, the system's internal state becomes inconsistent, leading to duplicate items, missing items, or crashes. All changes must go through the FileProvider API.

**4. App Group misconfiguration is silent and fatal.**
If the App Group identifier doesn't match between app and extension entitlements, the shared container resolves to different paths and nothing works. On macOS, the format requires the team ID prefix. Triple-check this.

### Protocol Pitfalls (same as Windows)

**5. blobId is mutable since v10 — content updates keep the same nodeId.**
Use `FileNode/set update { [nodeId]: { blobId: newBlobId, modified: timestamp } }`. The nodeId stays the same; no destroy+create needed. The `modifyItem` completion handler returns the same identifier.

**6. State tokens expire — return .syncAnchorExpired.**
When `FileNode/changes` returns `cannotCalculateChanges`, return `NSFileProviderError(.syncAnchorExpired)`. The system restarts with a full enumeration.

**7. Preserve timestamps on upload.**
Same as DESIGN.md §5. Pass the file's `creationDate` and `contentModificationDate` to the server. Without this, the server assigns its own timestamps, and the next enumeration would see a difference.

### FileProvider-Specific Pitfalls

**8. Working set must include all items the system needs.**
If you omit items from the working set enumeration, the system won't know about remote changes to those items. The working set should include at minimum all materialized items and their ancestors.

**9. Enumeration pagination must be consistent.**
If items change between pages of an enumeration, you may report duplicates or miss items. Use the JMAP state token captured at the start of enumeration for consistency. If the state changes mid-enumeration, note it and signal a change enumeration after completing the current one.

**10. signalEnumerator is rate-limited.**
The system may not immediately call your enumerator after `signalEnumerator`. Multiple rapid signals may be coalesced. This is fine — your enumerator fetches all changes since the last anchor, so coalesced signals just mean one slightly larger change batch.

**11. fetchContents must return a file URL, not data.**
The completion handler takes a `URL?` to a temp file, not raw `Data`. Write downloaded content to a temp file first. The system clones it and deletes the original.

**12. Spotlight will trigger fetchContents.**
macOS Spotlight indexes files in `~/Library/CloudStorage`, which triggers hydration of dehydrated files. This is analogous to Windows Search (DESIGN.md pitfall #15). For large sync roots, this can cause significant bandwidth usage on first sync. There's no clean way to prevent it — Spotlight respects the file's eviction state but will re-download evicted files for indexing.

**13. Don't return .noSuchItem for items being created.**
If `item(for:)` is called for an identifier the extension doesn't recognize (e.g., a locally-created item not yet synced), don't return `.noSuchItem`. Return a best-effort item from the template data. The system may call `item(for:)` for items it's in the process of creating.

**14. Handle .mayAlreadyExist in createItem options.**
When the system re-delivers a create (after extension restart or error), `options` includes `.mayAlreadyExist`. The extension must handle this idempotently — check if the item already exists on the server before creating a duplicate.

**15. Version mismatch on modifyItem is not always a conflict.**
The system may pass a `baseVersion` that's slightly behind the current server version due to enumeration timing. Don't treat every mismatch as a conflict — check if the server change and local change are compatible (e.g., server changed metadata, local changed content).

### Performance Pitfalls

**16. Use URLSession download tasks for large files.**
`URLSession.dataTask` buffers the response in memory. For large files, use `downloadTask` which streams to a temp file, or use `bytes(for:)` and stream manually. This prevents out-of-memory crashes in the extension (which has a lower memory budget than the containing app).

**17. HTTP/2 multiplexing can starve downloads.**
Same as DESIGN.md pitfall #22. If uploads and downloads share a URLSession, uploads can starve downloads. Use separate URLSession instances or configure QoS appropriately.

**18. Batch thumbnail requests.**
`fetchThumbnails` receives multiple identifiers at once. Batch the `Blob/convert` calls into a single JMAP request to reduce round-trips. Cache results aggressively by blobId — a given blobId always maps to the same content, so the cache never needs invalidation for a known blobId.

**19. Extension memory limit is lower than app.**
FileProvider extensions have a lower memory budget (~50-80MB depending on device). Use streaming for large file transfers. Don't cache the entire node tree in memory for large sync roots — page from the database.

### Offline & Bandwidth Pitfalls

**20. Don't block interactive hydration on bandwidth policy.**
When a user opens a file, they expect it to open. Never return `.serverUnreachable` for an interactive `fetchContents` request just because the connection is expensive. Check `request.isFileViewerRequest` and always allow interactive downloads regardless of connection type. Only restrict *background* operations (pin hydration, Spotlight pre-fetch, large uploads).

**21. Re-check transfer policy on every system retry.**
If the extension returns `.serverUnreachable` for a large upload on a cellular connection, the system retries later. The extension must re-evaluate the `NWPath` on each retry — the user may be back on Wi-Fi by then. Don't cache a stale "expensive" decision from the first attempt.

**22. NWPathMonitor fires on the extension's queue, not the main thread.**
Set up the monitor with a dedicated serial `DispatchQueue`. Path updates arrive on that queue. Updating shared state (UserDefaults, transfer policy) must be thread-safe. Don't use `DispatchQueue.main` — the extension has no main run loop guarantee.

**23. Offline edits + server edits = conflicts on reconnect.**
After extended offline, the system replays all queued local changes. If the same file was edited on the server during the offline period, `modifyItem` will upload content that conflicts with the server version. The `baseVersion` won't match the server's current version. Detect this case and either create a conflict copy or use last-writer-wins (`onExists: "replace"`), depending on the chosen strategy (§8).

**24. iOS background refresh is not guaranteed.**
`BGAppRefreshTask` scheduling is advisory — iOS may delay or skip refreshes based on battery, usage patterns, and system load. Don't rely on it for timely sync. Silent APNS pushes are more reliable but also not instant. Accept that iOS sync after extended background will have latency.

**25. Returning .serverUnreachable too eagerly causes retry storms.**
The system has its own retry backoff, but if the extension returns `.serverUnreachable` for every operation during a brief network blip, the system queues many retries that all fire when the network recovers. Let URLSession's own timeout handle transient blips (it retries internally for connection-level errors). Only return `.serverUnreachable` when the extension has confirmed the network is down (NWPathMonitor says unsatisfied) or after URLSession itself has given up.

### Testing Pitfalls

**26. Reset extension state between test runs.**
Stale state in `~/Library/CloudStorage` and the system's FileProvider cache can cause confusing behavior. Remove the domain and re-add it, or use `NSFileProviderManager.reimportItems(below:)` to force a fresh sync.

**27. Console.app is essential for debugging.**
FileProvider extension logs go to the system log, not stdout. Use `Logger` (os.log) and filter by your subsystem in Console.app. The extension process name will be something like `com.fastmail.files.FileProvider`.

**28. Simulating eviction for testing.**
Use `NSFileProviderManager.evictItem` to programmatically evict files during testing. There's no way to simulate system-initiated eviction directly.
