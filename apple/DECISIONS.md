# Apple Client — Design Decisions

This document records *why* each significant architectural, language, and protocol choice was made: what was chosen, what the main alternatives were, and the reasoning. It exists alongside `DESIGN.md` (which explains *how* things work) so that future changes can be evaluated against the original rationale rather than relitigating closed questions.

---

## Table of Contents

1. [Platform API: NSFileProviderReplicatedExtension](#1-platform-api-nsfileproviderreplicatedextension)
2. [Language: Swift 6 with Strict Concurrency](#2-language-swift-6-with-strict-concurrency)
3. [Database: SQLite (GRDB) Actor](#3-database-sqlite-grdb-actor)
4. [Cross-Process Communication: App Group + Darwin Notifications](#4-cross-process-communication-app-group--darwin-notifications)
5. [Authentication: OAuth2 + flock(2) Refresh Serialization](#5-authentication-oauth2--flock2-refresh-serialization)
6. [Sync Model: System-Driven vs Client Outbox](#6-sync-model-system-driven-vs-client-outbox)
7. [Incremental Sync: FileNode/changes vs Full Re-enumeration](#7-incremental-sync-filenodechanges-vs-full-re-enumeration)
8. [Cold-Start Enumeration: BFS Tree Walk vs Flat Page Queries](#8-cold-start-enumeration-bfs-tree-walk-vs-flat-page-queries)
9. [Change Notification: SSE + Polling Hybrid](#9-change-notification-sse--polling-hybrid)
10. [Uploads: Chunked Blob/set + Delta Patch](#10-uploads-chunked-blobset--delta-patch)
11. [Direct HTTP Write for Small Files](#11-direct-http-write-for-small-files)
12. [Conflict Resolution: Two Strategies](#12-conflict-resolution-two-strategies)
13. [Filename Normalization: NFC + Case Collision Deduplication](#13-filename-normalization-nfc--case-collision-deduplication)
14. [Item Version: blobId as contentVersion](#14-item-version-blobid-as-contentversion)
15. [Bandwidth Policy: NWPathMonitor Tiers](#15-bandwidth-policy-nwpathmonitor-tiers)
16. [Progress and Cancellation: Task + cancellationHandler](#16-progress-and-cancellation-task--cancellationhandler)
17. [SSE Reconnect: Bandwidth-Aware Backoff](#17-sse-reconnect-bandwidth-aware-backoff)
18. [Dependency Management: Swift Package Manager](#18-dependency-management-swift-package-manager)
19. [FUSE Code Isolation](#19-fuse-code-isolation)

---

## 1. Platform API: NSFileProviderReplicatedExtension

**Chosen:** `NSFileProviderReplicatedExtension` (macOS 12 / iOS 16+).

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| `NSFileProviderExtension` (non-replicated, legacy) | Extension manages its own on-disk storage. Available since macOS 10.10. |
| FUSE via macFUSE / libfuse | User-space filesystem; same approach as the Linux client. |
| A regular app with a document-based UI | No Finder integration; user must manually copy files. |

**Why the replicated extension:**

The replicated model inverts responsibility cleanly: the *system* owns the on-disk representation. The extension never directly creates, renames, or deletes files — it answers questions ("what exists on the server?", "here is the content") and receives notifications ("the user wrote this file"). This eliminates an entire class of bugs about who is moving files on disk and when.

The system provides placeholder files, hydration/dehydration progress in Finder, eviction policies, offline access semantics, and Spotlight integration for free. Building those correctly ourselves would take months and still not match what the OS does.

App Store distribution requires the extension model. FUSE requires a kernel extension which Apple increasingly restricts (removed from the default installer path in macOS 13+) and cannot be submitted to the App Store.

**Why not non-replicated:**

The non-replicated extension must maintain its own disk representation including eviction, which duplicates logic the OS already implements correctly and introduces a second authoritative store that can diverge. It is also formally deprecated as of macOS 12.

---

## 2. Language: Swift 6 with Strict Concurrency

**Chosen:** Swift 6, `Package.swift` with `-strict-concurrency=complete`.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Swift 5.x with async/await, no strict concurrency | Easier to write initially; races are runtime failures |
| Objective-C | Mature; no structured concurrency; Foundation-only API surface |
| Swift 5.x + GCD / OperationQueue | Serial queues equivalent to actors, but no compile-time checking |

**Why Swift 6 strict concurrency:**

The extension is killed and restarted by `fileproviderd` at arbitrary points. `createItem`, `modifyItem`, and `fetchContents` are called concurrently by the system for different items. Races between these callbacks cause silent data corruption — a file write clobbers a rename, a download overwrites an upload result. With strict concurrency, the compiler rejects data races at build time rather than letting them surface as hard-to-reproduce bugs in production.

`actor` types (`JmapClient`, `NodeDatabase`, `BandwidthPolicy`, `PushWatcher`) provide compile-verified mutual exclusion with no manual locking. `Sendable` enforcement ensures no mutable state crosses actor boundaries without copying.

The extension's constraints (no UI, no main thread, process lifetime measured in seconds) are a good fit for structured concurrency: every unit of work is a `Task`, cancellation propagates automatically, and `async let` parallelises independent work without callback nesting.

**Why not Objective-C:**

No structured concurrency. The FileProvider framework has Swift-first API surfaces (generic closures, typed errors, Swift-only availability annotations). Bridging these correctly in Obj-C requires more boilerplate than the Swift implementation itself.

---

## 3. Database: SQLite (GRDB) Actor

**Chosen:** SQLite in WAL mode via the GRDB library, wrapped in a Swift `actor`.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| JSON file + atomic rename | Simple; what the project started with |
| CoreData with NSPersistentCloudKitContainer | Apple-standard; complex concurrency model |
| Raw SQLite via FMDB or SQLite.swift | Fewer dependencies; more boilerplate |
| In-memory dictionary, write-through to plist | Fast reads; loses data on crash |

**Why SQLite / GRDB:**

The app and extension run in separate processes, both reading and writing the same database. SQLite WAL mode permits any number of concurrent readers plus one writer and survives a mid-write process kill (WAL recovery is atomic). JSON + atomic rename appeared to work but lost data when the extension was killed during the `rename(2)` syscall (root cause of early "database deleted on extension restart" symptoms).

GRDB adds type-safe query building, versioned schema migrations, and Codable-based row mapping with minimal overhead. The alternative of raw SQLite C API requires error-checking every return code and manual memory management for query strings — not worth the reduced dependency.

**Why not CoreData:**

CoreData's concurrency model (`NSManagedObjectContext` with `performAndWait`, merge policies, persistent store coordinator locking) predates Swift concurrency and actively fights against actor isolation. In extension contexts specifically, CoreData has a history of locking problems when `NSPersistentStoreCoordinator` is shared across processes. We only need simple parent-child tree queries and key-value state storage; CoreData is substantial complexity for no benefit.

**Why an actor wrapper around GRDB:**

`DatabasePool` (GRDB) is internally thread-safe for individual reads and writes, but read-modify-write sequences are not atomic. The `NodeDatabase` actor serializes all mutation sequences (check-then-update, upsert-and-set-state-token) without explicit locks, which the Swift compiler verifies cannot be broken.

---

## 4. Cross-Process Communication: App Group + Darwin Notifications

**Chosen:** `NSFileProviderManager` APIs for domain operations; flat JSON status files in the shared App Group container for status; Darwin notifications (`notify_post`) for edge-triggered UI updates; shared `UserDefaults(suiteName:)` for user preferences.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| XPC service | Structured RPC; requires persistent connection |
| NSDistributedNotificationCenter | Not guaranteed across process suspension |
| Shared SQLite as message queue | Heavier; adds read polling |
| IPC via named pipe / Unix socket | Cross-platform; more plumbing |

**Why App Group files + Darwin notifications:**

`fileproviderd` manages the extension's process lifetime, not the app. The app cannot reliably establish or maintain a connection to the extension — the extension may be killed between the app's connection attempt and the response. Status files (one JSON file per account in the shared container) tolerate this: the extension writes on state change, the app reads on display, with no connection required.

Darwin notifications (`notify_post` / `notify_register_dispatch`) wake the app when the extension writes a new status file. They are cross-process, fire-and-forget, and survive process restarts — unlike `NSDistributedNotificationCenter` whose delivery is not guaranteed when the receiving process is suspended.

**Why not XPC:**

XPC would require the app to maintain a persistent connection to the extension and handle reconnection when `fileproviderd` kills and restarts the extension. The status-file approach achieves the same observable behaviour with no connection management.

---

## 5. Authentication: OAuth2 + flock(2) Refresh Serialization

**Chosen:** OAuth2 PKCE flow for login; tokens in Keychain; cross-process token refresh serialized via `flock(2)` on a lock file in the shared container.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| App Passwords (static bearer token) | Simpler; no refresh; still supported as "advanced" login |
| Extension manages its own refresh | Independent; leads to token races |
| XPC token vending service | Cleanest API; requires persistent helper |
| Semaphore / NSDistributedLock | NSDistributedLock is documented as unreliable |

**Why flock(2) for refresh serialization:**

Both the app and extension may need to refresh the OAuth token simultaneously (extension is syncing, app is checking status, access token expired). Without serialization, both call the token endpoint concurrently, each with the same refresh token. The first succeeds and issues a new pair. The second's request arrives with a now-revoked refresh token, invalidating the session and forcing re-login.

`flock(2)` is a POSIX advisory lock on a file in the shared App Group container. It works across processes, survives `fork()`, and is released automatically if the holding process is killed. The loser blocks until the winner finishes, then re-reads the Keychain to pick up the new token rather than issuing a second refresh.

NSDistributedLock is documented as "suitable for single-system inter-process coordination" but has known spurious failure modes and requires manual stale-lock cleanup. `flock(2)` has no expiry issue because the OS releases it on process death.

**Why PACC for server discovery:**

`draft-ietf-mailmaint-pacc-01` (Provider Auto-Configuration Client) lets the user type only their email address. The client discovers the OAuth issuer and JMAP session URL automatically from `ua-auto-config.{domain}`. Without PACC the user must copy-paste two URLs from documentation — a significant drop-off for non-technical users. Manual URL entry is kept as an "Advanced" fallback for servers without PACC.

---

## 6. Sync Model: System-Driven vs Client Outbox

**Chosen:** The `NSFileProviderReplicatedExtension` callback model with no client-managed outbox.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Client outbox (queue of pending operations) | Used by the Windows cfapi client |
| Polling-only (re-enumerate on interval) | Simpler; high latency; server load |
| Hybrid: callbacks + outbox for reliability | More state to manage |

**Why no outbox:**

`NSFileProviderReplicatedExtension` calls `createItem`, `modifyItem`, `deleteItem` exactly once per user action. If the extension crashes mid-operation, `fileproviderd` retries the callback on restart. The system IS the outbox. Adding a second outbox creates a synchronisation problem: the outbox state can diverge from the system's knowledge of pending items if the extension crashes between acknowledging the callback and writing to its own outbox.

The Windows client uses an outbox because the cfapi model does not retry — the service must persist the intent itself. On Apple platforms this work is done by the OS.

**Why callbacks don't need idempotency guards beyond what's natural:**

The retry mechanism calls the same callback with the same arguments. Natural idempotency comes from the JMAP `onExists: "replace"` flag on creates and from checking the current DB state before acting on modifies. The `uploadId` deduplication key in chunk uploads prevents double-posting blobs.

---

## 7. Incremental Sync: FileNode/changes vs Full Re-enumeration

**Chosen:** `FileNode/changes` with the last known state token; fall back to full re-enumeration only when `cannotCalculateChanges` is returned.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Full query on every sync | Always correct; O(n) traffic per sync |
| Client-side change detection (diff local DB vs server) | Requires fetching all server nodes first |
| ETag-per-folder polling | Not part of the JMAP FileNode spec |

**Why incremental changes:**

At 50,000 nodes, full re-enumeration generates substantial API traffic and database churn on every sync. `FileNode/changes` returns only the delta since the last state token — a no-op sync is a single API call returning empty arrays.

`cannotCalculateChanges` (state token expired) triggers a full re-enumeration automatically. To prevent an infinite retry loop if the server keeps expiring tokens, re-enumeration attempts are capped at 5 consecutive failures before the account is reported as needing user attention (BUG-007 root cause; fix in commit a86c18d).

---

## 8. Cold-Start Enumeration: BFS Tree Walk vs Flat Page Queries

**Chosen:** Breadth-first search (BFS) from the home node using batched `parentId` queries — up to 16 folders per HTTP request (32 JMAP method calls). Items are reported to the FileProvider system progressively as each BFS level arrives.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Fetch all node IDs in pages, then fetch nodes by ID in pages | Two-pass; no progressive reporting; fetches orphans |
| Fetch all folder nodes first, then fill each folder separately | Two-pass; still needs a second enumeration of folder children |
| Single `FileNode/get` with no `ids` (fetch everything) | Not in spec; server would need to cap response size arbitrarily |
| Depth-limited query via `filter: { depth: N }` | Not in the current spec |

**The three approaches in detail:**

*Flat page query (fetch all IDs → fetch all nodes):*
```
FileNode/query (position 0, limit 4096) → 50,000 IDs
FileNode/get (ids: first 1024)
FileNode/get (ids: next 1024)
... (49 round-trips)
```
All 50,000 nodes arrive in one burst. Nothing can be shown to the user until the last page returns. Orphaned nodes (whose `parentId` points to a deleted folder) are fetched and stored — they consume DB space and are never reachable from the home root.

*Directories-first, then fill:*
```
FileNode/query filter:{isFolder:true} → folder IDs
FileNode/get (folder IDs in batches)
  → then for each folder: FileNode/query filter:{parentId:X}
```
Requires two passes through the tree and still does not report progressively. The second pass is equivalent to the tree walk but without the BFS batching optimization. It also assumes that "isFolder" is an efficient filter — the spec does not guarantee this.

*BFS tree walk (chosen):*
```
Batch 1: query+get children of [home]          → level 1 (immediate children of root)
Batch 2: query+get children of [16 level-1 folders]  → level 2
Batch 3: query+get children of [next 16 folders]     → ...
```
Each batch is one HTTP request containing up to 32 JMAP method calls (one `FileNode/query filter:{parentId}` + one `#ids`-referenced `FileNode/get` per folder). Items returned by each batch are reported to `NSFileProviderEnumerationObserver` immediately, so Finder starts showing the top of the tree within the first round-trip.

**Why BFS tree walk:**

*Progressive display.* Finder calls `observer.didEnumerate()` as items arrive. With the BFS approach, the home folder's contents appear in Finder after a single HTTP round-trip. The flat-page approach shows nothing until all 50,000 nodes have been fetched.

*Orphan exclusion.* The BFS only visits nodes reachable from the home root. Orphaned nodes — files or folders whose `parentId` refers to a deleted ancestor — are naturally excluded. They would consume database space and confuse the FileProvider system if reported. The flat-page approach fetches every node in the account regardless of reachability.

*Server-efficient queries.* The `use-cases.txt` spec explicitly lists `query by parentId` as an efficient server operation. Flat pagination over the full node set (`position`/`limit` without a filter) requires the server to scan the entire account's node table for each page, which is O(total nodes) per page rather than O(folder size).

*No separate directory discovery pass.* The BFS discovers folders naturally — any node with `isFolder: true` encountered during the walk is enqueued for its own children query. No separate "fetch all folders first" step is needed.

**Implementation detail — batching factor of 16:**

The JMAP spec allows multiple method calls per request. Each folder needs a `FileNode/query` + `FileNode/get` pair (2 calls). The Core capability's `maxCallsInRequest` limit (typically 32–64) bounds how many pairs fit in one request. A batch size of 16 folders (32 calls) comfortably fits within the 32-call minimum limit and keeps individual HTTP requests small enough to avoid server-side timeouts on very large folders.

**When the flat-page path is still present:**

`queryAndGetAllNodes` (flat page + ID fetch) remains in `JmapClient` for two reasons: the `FileNode/changes` recovery path needs to fetch specific node IDs returned by the changes response, and it was used during early development before BFS was implemented. It is not called during normal enumeration.

---

## 9. Change Notification: SSE + Polling Hybrid

**Chosen:** Server-Sent Events (SSE) on the JMAP `eventSourceUrl` for near-real-time notification; a 15-minute polling loop as a backstop.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Polling only | Simple; predictable; up to 15-minute change latency |
| WebSocket | Bidirectional; not in JMAP spec |
| Push notifications (APNS/FCM) | Mobile-only; requires server-side push infrastructure |
| Long-poll HTTP | Supported by some JMAP servers; not standard |

**Why SSE + polling:**

SSE is the JMAP-specified push mechanism (`eventSourceUrl` in the session). It provides sub-second change notification when the extension is online and the connection is healthy. The polling loop (15-minute interval) acts as a safety net: missed SSE events (due to connection drops, server restarts, or the extension being backgrounded) are caught within 15 minutes at most.

SSE events contain only a state-change notification (a JSON object with the new state token per capability). The client then calls `FileNode/changes` to learn what actually changed. This means the SSE channel carries essentially no data — it is purely a "wake up and poll" signal.

**Why not APNS:**

Push notifications require a permanent server-side component to issue tokens and post notifications. SSE is a direct client-server connection that works with the existing JMAP infrastructure. APNS also requires the user to grant notification permission, which is an additional friction point for a background sync service.

---

## 10. Uploads: Chunked Blob/set + Delta Patch

**Chosen:** Chunk files into `chunkSize`-sized blobs, upload each independently via `Blob/set`, then combine. For content edits, compute an xdelta3 binary diff and upload only the patch via `Blob/convert Delta/Patch`.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Single-shot HTTP POST to upload URL | Simple; fails silently on large files with connection drops |
| HTTP Range PUT (resumable) | Server-specific; not part of JMAP spec |
| Multipart form upload | Server-combining; less control over resume |

**Why chunked Blob/set:**

Each chunk is independently addressable by content hash (SHA-1 of the chunk data). If the upload is interrupted, the client queries which chunks the server already has and skips re-uploading them. A 1 GB file interrupted at 90% resumes from 90%, not 0%.

Content-addressed deduplication falls out for free: if the same file is uploaded by two devices concurrently, the second device finds all chunks already present and the combine step is the only work remaining. The server's `chunkSize` capability hint ensures chunks align with the server's storage blocks.

**Why delta patch:**

For files that are frequently edited in small increments (code, notes, documents), the diff between versions is orders of magnitude smaller than the full file. A 10 MB file with a 1 KB change sends ~1 KB over the wire. The server reconstructs the full blob from the patch + the original blobId. This is the `Blob/convert` `Delta/Patch` operation from `draft-ietf-jmap-blobext-01`.

The delta upload falls back to a full chunked upload if the patch computation fails, the old blobId is unavailable, or the patch is larger than the full file.

---

## 11. Direct HTTP Write for Small Files

**Chosen:** `PUT {webWriteUrlTemplate}/{id}` for files under 16 MB when the server capability is present; JMAP `Blob/set + FileNode/set update` for larger files.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Always JMAP (Blob/set + FileNode/set) | Consistent path; two round-trips |
| Always direct HTTP write | Simple; no resume on failure |

**Why direct write for small files:**

One HTTP request instead of two. For common cases (editing a source file, updating a config) the latency difference is measurable. The server returns the new blobId in the PUT response body.

**Why the 16 MB threshold:**

Above 16 MB, the probability of a connection interruption increases and the cost of re-uploading from scratch is significant. The chunked-upload path handles this correctly; direct write does not support resume. The threshold is a pragmatic tradeoff, not a hard protocol limit.

**Why a follow-up FileNode/set is always needed after direct write:**

The spec (§4.1) says the PUT updates only `blobId`, `size`, and `type`. `modified` is client-managed — the server does NOT auto-update it on content change. `JmapClient.directWrite()` issues the follow-up `FileNode/set update { modified }` call internally so callers see one atomic operation.

---

## 12. Conflict Resolution: Two Strategies

**Chosen:** Default "conflict copy" via `onExists: "rename"` (creates a new file with a server-assigned non-colliding name); opt-in "newest wins" via `onExists: "newest"` (server-side conditional update, new protocol extension).

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Last-write-wins (always overwrite) | Simple; silent data loss |
| First-write-wins (refuse if server changed) | Safe; frustrating UX |
| Client names the conflict file | Race: two clients create colliding names |
| Three-way merge | Correct for text; undefined for binary formats |
| Client-side timestamp compare then overwrite | TOCTOU race between two devices |

**Why `onExists: "rename"` for conflict copy:**

When conflict is detected (the blobId the user had when they opened the file differs from the current server blobId), both versions have value. The user's edit goes to the server as a new node with the same name. The server picks a non-colliding name atomically (e.g. "report (2).docx") and returns it in the `FileNode/set created` response. Returning the resulting `FileProviderItem` from the `completionHandler` causes the system to rename the local file to match — no manual filesystem manipulation.

Client-side naming ("document — conflict copy (MacBook, 2026-05-06).txt") cannot be made atomic: two devices running the same conflict logic simultaneously could produce the same conflict filename, creating a second conflict.

**Why `onExists: "newest"` as a new server-side protocol extension:**

A client-side "newest wins" check (compare `localModified` vs `serverModified`, upload only if local is newer) has a TOCTOU race: two devices can both read timestamps, both conclude they are newer, and both write — the later write silently discards the earlier one. Making the comparison atomic with the write on the server eliminates the race. The server returns `alreadyExists` in `notUpdated` when the condition fails; the client falls back to conflict copy so no work is lost.

**Why not three-way merge:**

Merge requires understanding the file's internal format. It is well-defined for line-oriented text but produces corrupt output for binary formats (DOCX, PSD, XLSX). Supporting merge would mean either restricting it to text files (surprising to users) or implementing format-specific merge logic for every file type (unbounded scope). The two-copy approach is always safe regardless of file type.

---

## 13. Filename Normalization: NFC + Case Collision Deduplication

**Chosen:** NFC normalization on all names flowing between the server and the local filesystem; ` (N)` suffix injection for siblings whose names are case-identical on a case-insensitive filesystem.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| No normalization — treat NFD and NFC as distinct | Breaks round-trips; "café" ≠ "café" across devices |
| Normalize to NFD (macOS-native form) | Violates JMAP Net-Unicode requirement |
| Percent-encode non-ASCII | Changes the filename the user sees |
| Case-fold all filenames | Loses information; can't recover original case |
| Hide all but one case-collision sibling | Silent loss; user doesn't know a file exists |

**Why NFC:**

JMAP requires Net-Unicode (RFC 5198), which is NFC. APFS stores filenames in a variant of HFS+ Canonical Decomposition (similar to NFD). Sending NFD bytes to the server would create filenames that look identical in the Finder but are binary-distinct on the server, causing duplicate files after the first round-trip from another platform.

`NSString.precomposedStringWithCanonicalMapping` is the correct, fast NFC transform on Apple platforms.

**Why ` (N)` suffix for case collisions rather than hiding:**

macOS (APFS case-insensitive) cannot represent two files whose names differ only by case in the same directory. On a case-sensitive JMAP server (`caseInsensitiveNames: false`), "Report.docx" and "REPORT.docx" are distinct. Hiding one of them would silently lose a file the user created. Injecting a ` (N)` suffix makes both visible with unique macOS-safe names. The suffix is stripped by `desanitize()` when writing back to the server, so the server always sees the canonical name.

**Why group case-collision siblings by `nfc(name).lowercased()`:**

A server might have both an NFD "café.txt" and an NFC "café.txt" (different byte sequences, logically the same name). Grouping by `name.lowercased()` without prior NFC normalization would miss this collision. Applying NFC before case-folding ensures all Unicode-equivalent names are treated as a single collision group.

---

## 14. Item Version: blobId as contentVersion

**Chosen:** `NSFileProviderItemVersion.contentVersion` = UTF-8 bytes of the server blobId. `metadataVersion` = hash of the node's metadata fields.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Hash of local file content | Requires reading the file; available only after download |
| Monotonic counter | Requires a persistent, crash-safe counter per item |
| Server `modified` timestamp as bytes | Timestamp collisions possible; not unique |
| Server `cTag` or ETag | Not part of the FileNode spec |

**Why blobId:**

The blobId is the server's content-addressable identifier for a blob. It is available immediately from any sync response without reading local file content. It changes exactly when the file content changes, and is stable across devices — two clients that independently sync the same file will have the same blobId.

In `modifyItem`, `baseVersion.contentVersion` (decoded as UTF-8) gives the blobId the FileProvider system held when it last saw the file. Comparing this to the DB's current blobId is a single string comparison that detects whether another device uploaded new content since the local edit began — no additional API round-trips required.

---

## 15. Bandwidth Policy: NWPathMonitor Tiers

**Chosen:** `NWPathMonitor` with four tiers: unrestricted (WiFi/ethernet), expensive (cellular/hotspot), constrained (Low Data Mode), offline. Per-operation gates enforce the policy.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| `SCNetworkReachability` | Only indicates path existence, not quality or cost |
| URL probe (send test request, measure) | Circular; uses the data it's trying to avoid |
| `URLSessionConfiguration.allowsCellularAccess` | Binary; can't distinguish expensive from constrained |
| User-configured bandwidth limit | Requires UI; doesn't adapt to actual conditions |

**Why NWPathMonitor:**

`NWPath.isExpensive` maps directly to cellular and hotspot connections — the OS knows this from the interface type, not by sending traffic. `NWPath.isConstrained` maps to Low Data Mode, which is an explicit user preference to minimize data usage. These are exactly the two dimensions the policy needs: "this costs money" vs "I asked the OS to reduce data".

`SCNetworkReachability` predates Network.framework and only answers "is there a path?". It cannot distinguish WiFi from cellular. The `allowsCellularAccess` URLSession flag is a per-session setting that doesn't help with the finer-grained decision of "allow interactive downloads on cellular but not background pre-fetch".

**Why per-operation gates rather than a global sync pause:**

User-initiated actions (opening a file) must always proceed regardless of connection cost — the user explicitly asked and is waiting. Background pre-fetch (system hydrating placeholders) should stop on constrained connections. Uploads have a size-based gate: small saves (under 1 MB) proceed on cellular because blocking them would be more disruptive than the data cost. Large uploads wait for WiFi.

A global sync pause would be simpler but would block user-initiated opens on cellular, which is the wrong tradeoff.

---

## 16. Progress and Cancellation: Task + cancellationHandler

**Chosen:** `NSProgress` returned synchronously from FileProvider callbacks; a `let Task` stored before wiring `progress.cancellationHandler = { task.cancel() }`.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Fire-and-forget Task, ignore cancellation | Simple; wastes bandwidth on cancelled downloads |
| `withTaskCancellationHandler` inside downloadBlob | Correct but leaks the mechanism upward |
| Custom `CancellationToken` class | More flexibility; more boilerplate |

**Why `let task` + `cancellationHandler`:**

`fetchContents` must return an `NSProgress` synchronously. The actual download is async. Assigning the Task to a `let` constant (not `var` — no race possible on a constant) and wiring the cancellation handler is the idiomatic Swift concurrency pattern with no additional machinery.

`URLSession.download(for: request)` internally uses `withTaskCancellationHandler` — cancelling the Swift Task propagates to the URLSession data task via Swift's concurrency runtime, stopping the HTTP transfer without any plumbing in `downloadBlob`. `CancellationError` is caught at the `fetchContents` level (not inside `downloadBlob`) and converted to `NSURLErrorCancelled`, telling the FileProvider system the operation was intentionally stopped rather than a network failure — which prevents spurious retries.

---

## 17. SSE Reconnect: Bandwidth-Aware Backoff

**Chosen:** Exponential backoff on errors (1s → 2s → ... → 60s); 30s minimum reconnect delay on expensive connections; SSE skipped entirely on constrained/offline (polling only).

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Pure exponential backoff regardless of connection | Can hammer server on cellular with rapid disconnects |
| Always connect SSE, let OS throttle | OS doesn't throttle at the connection level |
| Polling only | No near-real-time notification; higher polling frequency needed |
| Jitter added to backoff | Useful for large fleets; single-instance doesn't need it |

**Why skip SSE on constrained:**

Low Data Mode is an explicit user preference for minimal data. A persistent SSE connection — even one that carries nearly no data — violates that intent. The sync engine's polling loop (15-minute interval) provides adequate change detection for the constrained case. When the user disables Low Data Mode the SSE connection is re-established within `maxBackoff` seconds (60s).

**Why 30s minimum on expensive:**

On cellular, TLS handshake overhead is non-trivial (several kilobytes per connection). If the SSE stream disconnects frequently (the extension may be backgrounded on iOS), reconnecting immediately would generate disproportionate control-plane traffic relative to the data volume of the events themselves. A 30s floor limits this without meaningfully delaying change notification — the server push is still far faster than the 15-minute polling interval.

---

## 18. Dependency Management: Swift Package Manager

**Chosen:** Swift Package Manager (`Package.swift`) with pinned dependencies in `Package.resolved`.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| CocoaPods | Widely used; Ruby toolchain; modifies Xcode project heavily |
| Carthage | Build-from-source; less Xcode integration |
| XCFramework / vendored binaries | No source changes; hard to audit |
| Zero external dependencies | Possible but reinvents tested library code |

**Why SPM:**

SPM is the first-party Swift dependency manager with direct Xcode integration (no Ruby, no separate build phase). The `Package.resolved` file pins exact versions for reproducible builds. All external code is built from source with the same compiler settings as the app, which matters for Swift 6 strict concurrency — the dependency must also compile cleanly under the same settings.

External dependencies are deliberately minimal: GRDB (SQLite ORM), Sparkle (auto-update). Both are well-maintained, have comprehensive test suites, and have no transitive dependencies that would widen the attack surface.

---

## 19. FUSE Code Isolation

**Chosen:** `FuseCLI/` is a separate executable target in `Package.swift`, not compiled into the extension or app targets.

**Alternatives considered:**

| Option | Notes |
|--------|-------|
| Single target including FUSE sources | Simpler Package.swift; FUSE code linked into App |
| Separate repository | Clean separation; harder to share protocol types |

**Why isolated target:**

The FUSE mount is a different product from the Finder-integrated sync client: it requires macFUSE to be installed, targets power users and scripting workflows, and provides a POSIX filesystem interface rather than Finder integration. Linking FUSE code into the FileProvider extension would add a kernel-extension dependency to a component that must run in a sandboxed app extension context — the sandbox would reject it.

Keeping FUSE in its own target means the main app and extension have no FUSE symbols, FUSE can be built and distributed separately (or omitted from the App Store build), and the two products can evolve independently.

---

## Summary Table

| Decision | Chosen | Key Reason |
|----------|--------|-----------|
| Platform API | NSFileProviderReplicatedExtension | System owns disk; App Store distributable |
| Language | Swift 6 strict concurrency | Compile-time race prevention in concurrent callbacks |
| Database | GRDB actor | Multi-process safe (WAL); crash-safe; no CoreData complexity |
| IPC | App Group files + Darwin notifications | Tolerates extension restarts; no persistent connection needed |
| Auth refresh | flock(2) on shared file | Race-free across two processes without persistent service |
| Outbox | None (system retries callbacks) | OS is the outbox on NSFileProviderReplicatedExtension |
| Incremental sync | FileNode/changes | O(delta) traffic; fall back to full on token expiry |
| Change notification | SSE + 15-min polling | Sub-second when online; polling catches missed events |
| Upload | Chunked Blob/set + delta patch | Resumable; content-addressed dedup; bandwidth-efficient edits |
| Small-file update | Direct HTTP write | One round-trip for common case |
| Conflict | onExists:rename + onExists:newest | Server-atomic; user's work never silently lost |
| Filename form | NFC + case-collision suffix | JMAP requires NFC; APFS case-insensitive needs disambiguation |
| Item version | blobId as contentVersion | Available without file read; direct conflict detection |
| Bandwidth detection | NWPathMonitor tiers | Distinguishes cost (expensive) from user preference (constrained) |
| Cancellation | let Task + cancellationHandler | Idiomatic Swift; URLSession honours Task cancellation natively |
| SSE on constrained | Skip (rely on polling) | Respects Low Data Mode intent |
| Dependencies | SPM, minimal | First-party; source-built; narrow surface |
| FUSE | Separate target | Different product; sandbox incompatible with extension |
