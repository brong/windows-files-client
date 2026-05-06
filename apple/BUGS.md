# Fastmail Files — Bug Log

Running record of bugs found, root causes, and fixes. Goal: never repeat the same mistake.

Status: **Open** | **Fixed** | **Mitigated** (workaround in place, root cause not fully resolved)

---

## BUG-001 — Stale build artifact causes "undefined symbol" linker error
**Status:** Fixed (workaround: ⇧⌘K Clean Build Folder)
**Symptom:** Xcode reports `Undefined symbol: JmapClient.SessionManager.__allocating_init(...)` after editing Swift Package sources outside Xcode.
**Root cause:** Xcode's incremental build cache holds compiled `.o` files for the SPM package. When source files are modified by an external editor, Xcode sometimes doesn't detect the change and links the extension against the stale library.
**Fix:** No code change needed — this is an Xcode bug. Clean Build Folder before reporting as a real error.
**Lesson:** When you see "undefined symbol" for a function that clearly exists in source, try Clean Build Folder before investigating further.

---

## BUG-002 — os_log messages appear as `<private>` in Console
**Status:** Fixed (commit a61d38e / 85240fb)
**Symptom:** Log entries show `<private>` instead of account IDs and error messages, making log analysis useless.
**Root cause:** Swift string interpolation inside `Logger.info()` captures values as `@autoclosure`, which marks them private by default. Must explicitly add `privacy: .public`.
**Fix:** Add `, privacy: .public` to every interpolated value in logger calls.
**Lesson:** All logging of identifiers and error messages needs explicit `privacy: .public`. Default is private. Never log without checking this.

---

## BUG-003 — `await` illegal inside `&&` operator (Swift 6 concurrency)
**Status:** Fixed (commit a932484)
**Symptom:** Swift 6 compiler error: "'await' in an autoclosure that does not support concurrency" on lines like `if children.isEmpty && (await db.stateToken ?? "").isEmpty`.
**Root cause:** The `&&` operator's right-hand side is `@autoclosure`, which doesn't support concurrency. Same applies to any short-circuit operator and string interpolation in logger calls.
**Fix:** Hoist the `await` expression into a `let` binding before the `if` condition.
**Lesson:** Never write `await` inside `&&`, `||`, or logger string interpolation. Always hoist to a `let` first.

---

## BUG-004 — Session document re-fetched on every extension process launch
**Status:** Fixed (commit 0818aa2)
**Symptom:** Every time the FileProvider extension wakes up (which can be every 10–30 seconds), it makes an HTTP round-trip to fetch the JMAP session document, even though the document rarely changes.
**Root cause:** `SessionManager` only had an in-memory cache. Extension processes are short-lived; the cache was lost on every kill/restart.
**Fix:** Added disk cache (`session-{accountId}.json` in shared container). Three-tier lookup: in-memory → disk → network. Session state invalidated when server's `sessionState` header differs from cached session's `state` field.
**Lesson:** Extension processes are transient. Any "once per session" work must be persisted to disk, not just held in memory.

---

## BUG-005 — Finder empty after account remove + re-add
**Status:** Fixed
**Symptom:** After removing an account and re-adding it, Finder shows the domain in the sidebar but no files/folders appear, despite the extension reporting "Up to date".
**Root cause:** `removeDomain` tries to delete the NodeCache directory, but this silently fails when the extension process still has the SQLite file open. The stale DB retains the old state token. `currentSyncAnchor` returns non-nil, so the system calls `enumerateChanges` instead of `enumerateItems`. `performChangeEnumeration` runs, finds no server changes, calls `setIdle()` → "Up to date" in the UI. But the system's FileProvider item cache is empty (cleared when the domain was removed), so Finder shows nothing.
**Fix applied:**
  1. `registerDomain` now explicitly opens NodeDatabase and sets the state token to `""` via the DB API (atomic SQLite write, survives the directory-deletion race).
  2. `registerDomain` pre-writes a "syncing" status file so the UI cannot show "Up to date" before `enumerateWorkingSet` has actually run.
  3. `currentSyncAnchor` treats `""` same as nil (earlier fix) so the empty token → nil anchor → system calls `enumerateItems`.
  4. `performChangeEnumeration` returns `syncAnchorExpired` when DB state token is empty (earlier fix) as a second guard.
  5. `signalEnumerator(for: .workingSet)` called after domain add.
**Lesson:** `try? removeItem` is not a reliable way to reset state — it silently fails when the target is in use. Always reset persistent state via its own API (write "" to the DB) before relying on file deletion. And never let the extension report "idle" before the initial `enumerateWorkingSet` has completed.

---

## BUG-006 — Status view shows `nodeCount: 0` after successful enumeration
**Status:** Fixed
**Symptom:** The account status shows "0 files" even after `enumerateWorkingSet` successfully fetches nodes.
**Root cause:** `ExtensionStatusWriter.init` always started with `nodeCount: 0` in memory. The extension is killed every ~60s; the new process has count 0. The first no-op `enumerateChanges` poll calls `setIdle()` (no count arg), which writes 0 to disk — overwriting the count that `enumerateWorkingSet` stored.
**Fix:** `ExtensionStatusWriter.init` now reads back the existing status JSON from disk on startup and uses it as the initial in-memory state. Subsequent no-arg `setIdle()` calls preserve the already-correct count across process restarts.
**Lesson:** Any state that must survive extension process restarts must be read back from disk at startup — not re-initialised to a zero/default value. The extension is ephemeral; the file is not.

---

## BUG-007 — No retry cap on syncAnchorExpired path (potential DoS loop)
**Status:** Fixed
**Symptom:** If `enumerateWorkingSet` repeatedly fails or writes an empty state token to the DB, the system calls `enumerateItems` again immediately, which hits the JMAP server again, with no backoff or cap.
**Root cause:** `performChangeEnumeration` returns `syncAnchorExpired` when the DB has no state token. This is correct for the first call after a fresh domain registration. But if `enumerateWorkingSet` itself then fails to persist a state token (e.g., `getChildrenBatched` returns no state string), the cycle repeats indefinitely.
**Fix:** Added `enumerationFailureCount` to `NodeDatabase` (persisted in `sync_state` table, survives extension process kills). `performChangeEnumeration` increments this counter on each `syncAnchorExpired` return from the empty-DB guard. After 5 consecutive failures it emits `.cannotSynchronize` instead, halting the loop. `enumerateWorkingSet` resets the counter to 0 on successful completion.
**Violates:** use-cases.txt: "Not have the client go into an infinite loop (which causes denial of service) if something is broken, either in client OR server."
**Lesson:** Every error recovery path that triggers a network call needs a retry cap and backoff. "It should succeed next time" is not a loop-safety argument. The counter must be persisted to disk, not held in memory, because extension instances are ephemeral.

---

## BUG-008 — `removeLogin` skipped cleanup for non-synced accounts
**Status:** Fixed (commit b2d5cc8)
**Symptom:** If an account was added to a login but never enabled for sync (`isSynced = false`), removing the login left that account's database, session cache, and status files behind in the shared container.
**Root cause:** `removeLogin` iterated `login.accounts where acct.isSynced`, skipping non-synced accounts entirely.
**Fix:** Added `purgeAccountFiles(accountId:)` helper called for non-synced accounts in `removeLogin`. This deletes all per-account files without touching the FileProvider domain (which was never registered).
**Lesson:** Cleanup must cover all accounts in a login, not just the ones currently syncing. "Didn't sync it" doesn't mean "didn't create any files".

---

## Known divergence scenarios — DB vs FileProvider system cache

These are situations where our SQLite database and the system's FileProvider metadata cache can hold inconsistent views of the world. Most are tolerated; the dangerous ones are marked.

### 1 — Extension killed between DB write and `completionHandler`
**Scenario:** `createItem` or `modifyItem` writes the new node to the DB (via `upsertFromServer`), then the extension is killed before calling `completionHandler(item, ...)`.
**Result:** DB has the node; system cache does not. System will retry the operation.
**Current mitigation:** On retry the server call is re-issued. For `modifyItem` (rename/move) the server returns success again and the DB is re-written — idempotent. For `createItem` see scenario 2 (now fixed).

### 2 — `createItem` retry blob deduplication
**Scenario:** `createItem` succeeds on the server and in our DB, extension dies before `completionHandler`. System retries → we call `FileNode/set create` again → a second node with the same name is created on the server (or an error if `onExists` rejects it).
**Result:** Two files on the server where the user intended one, or a spurious error on the retry. If the retry gets a new node ID, DB ends up with two entries for the same local file.
**Fix applied:** Before uploading, `createItem` checks the DB for an existing node with the same (name, parentId). If found, it computes the local file's SHA1 and verifies via `Blob/get + digest:sha` that the server still holds that blob with matching content. If so, the re-upload is skipped and the existing blobId is reused. `onExists: "replace"` on `FileNode/set create` then makes the node creation idempotent. Combined, this means a retry either returns the same node (blob still on server) or creates a fresh one after a clean upload — never a duplicate.
**Status:** Fixed.

### 3 — `deleteItem` (move to trash) succeeds on server, DB delete fails
**Scenario:** `client.updateNode(parentId: trashId)` succeeds. DB write (`upsertFromServer` / `remove`) then throws or extension dies.
**Result:** Node is in trash on server, DB still shows it at original location. Next `enumerateChanges` fetches the server change, re-deletes from DB, and signals the system — eventually self-correcting.
**Current mitigation:** Self-correcting within one sync cycle. Not dangerous.

### 4 — `modifyItem` content upload succeeds, `updateNodeContent` fails
**Scenario:** Blob is uploaded and stored on the server. `FileNode/set update { blobId }` then fails (network, auth, etc.).
**Result:** A blob exists on the server that nothing references. The file content on the server is stale (old blobId). Our DB still has the old blobId.
**Fix applied:** `modifyItem` now calls `uploadBlobDelta(oldBlobId: currentBlobIdFromDB)` instead of `uploadBlobChunked`. On retry, the delta upload fetches the previously-uploaded blob's chunk structure from the server, compares SHA1 hashes, and reuses all matching chunks — so if the file hasn't changed since the failed attempt, no bytes are re-uploaded. The orphaned blob from the first attempt is still GC'd by the server, but the retry is now cheap.
**Status:** Fixed (retry is now delta-aware; orphaned blob cleanup is server-side as before).

### 5 — Partial `enumerateWorkingSet` BFS
**Scenario:** BFS fetches some pages then crashes or is killed mid-way. `stateToken` is only written on full success.
**Result:** DB has partial data; system has partial enumeration; stateToken is empty. Next restart retries the full BFS from scratch.
**Current mitigation:** Empty stateToken → `currentSyncAnchor` returns nil → system calls `enumerateItems` → full re-population. Self-correcting.

### 6 — DB cleared (`stateToken = ""`) but system cache still populated
**Scenario:** `cleanAccount` clears the DB. System still holds the previously-enumerated item set from before the reset.
**Result:** System calls `enumerateItems` (not `enumerateChanges`) because anchor is nil. BFS re-populates DB and signals updates. System reconciles its cache against the fresh enumeration.
**Current mitigation:** Correct by design — intentional full re-sync. Not dangerous.

### 7 — Pin state orphaned after account remove + re-add
**Scenario:** `pinned_nodes` table is local-only and not cleared by a node-table reset or domain removal. After re-add, the BFS may assign different node IDs (unlikely but possible if server state changed).
**Result:** `pinned_nodes` contains IDs that no longer exist in `nodes`. `contentPolicy` lookups return false (correct: no match). Orphaned rows are harmless but accumulate.
**Fix applied:** `NodeDatabase.cleanupOrphanedPins()` runs a single `DELETE FROM pinned_nodes WHERE nodeId NOT IN (SELECT id FROM nodes)` after each successful `enumerateWorkingSet` BFS completes. Orphaned rows are pruned automatically after every full re-enumeration.
**Status:** Fixed.

### 8 — `enumerateChanges` returns items not yet in DB
**Scenario:** Server reports a node as created/updated. We call `FileNode/get`, get the node back, write to DB, then call `observer.didUpdate(item)`. If `item(for:identifier:)` is called by the system before the DB write is committed, it fetches a stale or missing entry.
**Result:** System gets a `noSuchItem` error for an item it just received as an update. Typically causes a brief Finder glitch; system retries.
**Fix applied:** `SyncEngine.fetchChanges()` now returns the new state token without writing it to the DB. `performChangeEnumeration` writes the token to DB only *after* `observer.finishEnumeratingChanges` succeeds. This gives the commit ordering: (1) apply node writes idempotently, (2) signal system, (3) advance state token. A kill at any point before step 3 leaves the old token in DB; the next restart re-fetches and re-applies the same changes (upserts and removes are both idempotent). The DB writes in step 1 happen before `observer.didUpdate`, so `item(for:)` lookups always find the node in the DB when the system calls back.
**Status:** Fixed.

---

## BUG-009 — Stale "syncing" status file causes perpetual spinner
**Status:** Fixed
**Symptom:** Menu bar shows "Syncing" indefinitely even though all accounts are idle. Restarting the app doesn't help.
**Root cause:** The extension writes `state: syncing` at the start of `enumerateWorkingSet`. If the extension process is killed before the BFS finishes (and thus before `setIdle()` is called), the status file remains on disk with `state: syncing`. If the system never re-wakes that extension (because the user hasn't opened its Finder folder), the file stays stale indefinitely. The app reads all status files without checking freshness, so it derives "Syncing" from the stale file.
**Fix:** `reloadExtensionStatuses()` in the app now normalizes any status with `state == .syncing || .initializing` and `activeOperationCount == 0` that hasn't been updated in > 5 minutes to `state = .idle`. Live extensions always write back within seconds of being driven; a 5-minute-old "syncing" status with zero active ops is definitionally stale.
**Lesson:** Status files have a freshness window. An extension that is not being driven will not update its status file. The app must treat old syncing-with-no-ops statuses as idle — never show a spinner indefinitely when there is no evidence of live activity.

---

## BUG-010 — Upload architecture does not survive extension process kills

**Status:** Fixed
**Symptom:** Large file uploads (≥ 5 MB, chunked path) restart from scratch whenever fileproviderd kills the extension mid-upload. For a 200 MB file killed and restarted three times, ~600 MB of bandwidth is wasted before the upload ever completes.
**Root cause:** `JmapClient.uploadBlobChunked` used `URLSession(configuration: .default)` (misleadingly called `backgroundSession`). This session is tied to the extension process; when fileproviderd kills the extension (which it does on normal lifecycle management, typically after a few minutes of wall-clock time), all in-flight URLSession tasks are cancelled. The accumulated chunk blobIds (partially-completed upload state) lived only in local variables on the call stack — never persisted. When fileproviderd restarts the extension and retries the FileProvider operation, `uploadBlobChunked` started from chunk 0 with no knowledge of what was already uploaded.
**Contributing factors (all fixed):**
  1. Chunk temp files were written to `FileManager.default.temporaryDirectory` (process-scoped). Extension kill mid-loop left orphaned 64 MB temp files that accumulated with each retry. **Fix:** temp files now written to App Group container `tmp/` and swept at extension startup.
  2. Each chunk (up to 64 MB) was read entirely into RAM with `fileHandle.readData(ofLength:)`. Peak allocation ~128–192 MB — a direct jetsam trigger. **Fix:** `processChunk(from:to:length:updating:)` streams in 1 MB buffers; peak RAM is ~2 MB per chunk regardless of chunk size.
  3. `Progress.cancellationHandler` was never set. System calls `progress.cancel()` to abort; without a handler the upload Task ran to completion regardless. **Fix:** `cancellationHandler` now cancels the upload `Task`.
  4. No concurrency guard against duplicate uploads. **Status:** Fixed — generation-counter guard in `registerInFlight`/`setInFlight`/`deregisterInFlight` helpers; `createItem` and `modifyItem` both cancel any in-flight task for the same item identifier before starting a new one.
**Fix applied:**
  - **`BackgroundUploader` actor** (`BackgroundUploader.swift`): wraps `URLSessionConfiguration.background(withIdentifier:)`. Chunk HTTP POSTs run in `nsurlsessiond` and survive extension process termination. `UploadTaskDelegate` (NSObject) bridges delegate callbacks to `CheckedContinuation`. Orphaned task completions (chunks that finished after the extension was killed) fire `onOrphan`, which calls `completeUploadChunkByTaskId` to persist the blobId without re-uploading.
  - **`upload_chunks` table** in `NodeDatabase`: keyed by `(upload_id, chunk_index)`. `upload_id` is stable across retries (`accountId:itemIdentifier:fileSize:modTimeMs`). On restart, `resumedChunks` are loaded from DB; those chunks contribute to the overall SHA1 hash (hash-only path, no temp file) but are not re-uploaded.
  - **SHA1→blobId cache** (`sha1_cache` table): after each chunk upload, SHA1 → blobId is persisted. Future uploads of identical content (same file re-uploaded, or another file with overlapping chunks) hit the cache and skip the POST entirely. `uploadBlobDelta`'s Blob/get response seeds the cache with the old file's chunk hashes for free. Entries swept after 30 days of non-use.
  - All of the above wired through `UploadChunkStore` closures passed into `JmapClient.init` from `FileProviderExtension.init`.
**Lesson:** Extensions are ephemeral. Any operation that takes more than a few seconds must be structured to survive a process kill at any point. "Background" URLSession is not just a performance hint — it is the mechanism that keeps transfers alive when the process is gone. Never use `URLSession.default` for uploads in a FileProvider extension.

---

## BUG-011 — PushWatcher delegate never set; real-time sync is silently disabled

**Status:** Fixed
**Symptom:** The extension never signals the working-set enumerator in response to server-side changes. Finder shows stale content until the system's next poll cycle (typically minutes).
**Root cause:** `FileProviderExtension.startPushWatcher()` called `pushWatcher.start()` but never assigned `pushWatcher.delegate`. `PushWatcher.handleEvent` called `await delegate?.pushWatcherDidReceiveChange(self)` — a no-op when delegate is nil. All received SSE state-change events were discarded. The comment said "we'll use a bridge object" — this code was never completed.
**Fix applied:**
  - `FileProviderExtension` now conforms to `PushWatcherDelegate`.
  - `pushWatcherDidReceiveChange` calls `NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet)`.
  - `pushWatcherDidReconnect` does the same — any changes that arrived while the connection was down are caught by the next `enumerateChanges` poll triggered by the signal.
  - `startPushWatcher()` calls `await pushWatcher.setDelegate(self)` before `start()`. Added `setDelegate(_:)` to `PushWatcher` as the actor-safe setter.
**Lesson:** An unset delegate is silent. There is no compiler warning, no runtime error, and no log entry — the feature just doesn't work. Any protocol-delegate wiring needs a test that verifies the delegate receives a call.

---

## BUG-012 — SSE OAuth token not refreshed on mid-stream expiry

**Status:** Fixed
**Symptom:** After an OAuth access token expires mid-stream, the SSE connection could in theory loop without refreshing the token.
**Root cause:** `PushWatcher.connect()` fetches the token once at the start of each connection attempt. The 401 invalidation path only fires if the *initial* HTTP response is 401. If the token expires *while* the stream is open, `bytes.lines` throws a `URLError`, not an HTTP 401 — so the 401 handler never runs.
**Fix applied:** The existing 401 handler on the *initial response* of the next `connect()` call is the correct fix point. When we reconnect after a mid-stream drop, we send the cached token; if it was expired the server returns 401, `invalidateAccessToken()` fires, and the following retry gets a fresh token. No mid-stream URLError handling is needed and adding it would be wrong: a genuine network drop (WiFi flap, airplane mode) must not cycle the token, since the token is still valid and the OAuth server may also be unreachable.
**Lesson:** The 401 handler only fires when the server gets to respond — but that is fine, because the token expiry manifests as a 401 on the *next* connection's initial response, not mid-stream. Do not invalidate tokens on transport errors: you cannot distinguish auth expiry from a network drop at that layer, and unnecessary invalidation during a network outage makes recovery harder.

---

## BUG-013 — `isDomainBeingRemoved` guard has a TOCTOU race in deleteItem

**Status:** Fixed
**Symptom:** A file delete can propagate to the server during domain teardown, potentially deleting server content that the user intended to keep.
**Root cause:** `deleteItem` checked `isDomainBeingRemoved`, then suspended on `await specialNodes.value`. `invalidate()` can set the flag on any thread during that suspension. When the task resumed, the guard had already passed and the delete proceeded.
**Fix applied:** Two additional checks added: immediately after `await specialNodes.value`, and again (as a `guard`) immediately before the destructive server call. All three suspension points between the entry check and the server call (`specialNodes.value`, `database.entry`, `activityTracker.start`) are now bracketed.
**Lesson:** Concurrency guards around `await` suspension points are TOCTOU races. Any state that can change asynchronously must be re-checked after every suspension, not just at entry.

---

## BUG-014 — chunkSize clamping formula allows unbounded chunk size

**Status:** Fixed
**Symptom:** For very large files on servers with small `maxDataSources`, chunk size can grow beyond `maxChunkSize` (64 MB), causing unbounded memory allocation and upload task sizes.
**Root cause:** The clamp formula on line 639 of `JmapClient.swift` is:
```swift
chunkSize = max(Self.minChunkSize, min(chunkSize, max(Self.maxChunkSize, Int(fileSize))))
```
The intended upper bound is `maxChunkSize`. The actual upper bound is `max(maxChunkSize, fileSize)` — for a large file the entire file becomes the upper bound. Combined with the `while` loop that doubles `chunkSize` to reduce chunk count below `maxDataSources`, this can produce chunk sizes of hundreds of MB that are read entirely into RAM.
**Fix applied:** `chunkSize = min(max(Self.minChunkSize, chunkSize), Self.maxChunkSize)` — standard two-sided clamp. Swift stdlib has no `Comparable.clamped(to:)` for scalars.
**Lesson:** Clamp formulas with `max` inside `min` (or vice versa) are easy to get wrong. The correct bounded-range clamp is `min(max(lo, x), hi)`. Any other nesting silently lets one bound escape.

---

## BUG-015 — `fetchContents` blocks interactive file-open on expensive connections and hides real errors

**Status:** Fixed
**Symptom:** Opening a file in Finder shows "Your device couldn't connect to the server. There may be a problem with the server or network." Downloading files fails silently with no diagnostic information in the traffic log.
**Root causes:**
  1. **Wrong bandwidth gate for interactive downloads.** `fetchContents` used `allowsBackgroundDownload` (only passes on `.unrestricted`/WiFi) to gate even user-initiated file opens. On macOS < 13.0, `isFileViewerRequest` is unavailable and the code defaulted to `isInteractive = false`. On cellular/VPN connections (where `NWPath.isExpensive = true`), ANY file open on macOS 12 would return `.serverUnreachable` — a bandwidth policy decision with no indication that bandwidth is the cause.
  2. **`Accept: application/octet-stream` header.** Sent on all download requests. A strict server that honors content negotiation could return 406 Not Acceptable if the blob's content type differs.
  3. **No traffic logging for downloads.** `downloadBlob` made HTTP GET requests with no `TrafficLog` entries. The traffic log captured all JMAP API calls but had a blind spot for blob downloads — making it impossible to diagnose download failures without enabling OS-level packet capture.
  4. **Error body from non-200 download response was discarded.** `checkHTTPStatus(httpResponse, data: nil)` was called for downloads. For a `.download(for:)` call, the response body goes to a temp file, not a `Data` object. The error message from the server was never read, so error logs showed only the HTTP status code.
  5. **Temp file from failed download never cleaned up.** When `checkHTTPStatus` threw for a non-200 response, the temp file containing the error body was leaked.
**Fix applied:**
  - `fetchContents` now defaults `isInteractive = true` on macOS < 13 (can't distinguish interactive from background). For interactive requests, uses `allowsInteractiveDownload` (`tier != .offline`) instead of `allowsBackgroundDownload` (`tier == .unrestricted`). Background hydration still requires `.unrestricted`.
  - Changed `Accept` header in `downloadBlob` from `application/octet-stream` to `*/*`.
  - Added `TrafficLog.shared.log(...)` entries to `downloadBlob` for both the outgoing GET and the response status/headers.
  - For non-200 download responses, the error body is read from the temp file and passed to the log and `checkHTTPStatus` before the temp file is cleaned up.
  - Temp file is now explicitly removed in all error paths in `downloadBlob`.
  - Added `logger.error(...)` in the `fetchContents` catch block so the full error (before mapping) appears in the unified log.
**Lesson:** Interactive file-open and background hydration have different latency and bandwidth requirements. Use `allowsInteractiveDownload` for the former and `allowsBackgroundDownload` for the latter — they are not interchangeable. When adding a bandwidth gate, always check that the fallback default for old OS versions errs on the side of user experience (allow), not caution (block). And every HTTP request/response pair must appear in the traffic log — a blind spot in logging is a blind spot in diagnosis.

---

## BUG-016 — No-trash accounts cause `itemMismatch` and "Your device couldn't connect to the server" error

**Status:** Fixed
**Symptom:** Opening a file in Finder shows "Your device couldn't connect to the server. There may be a problem with the server or network." The error appears shortly after a successful download. `fileproviderd` logs show `itemMismatch(expected: nil, received: .root)` for a `create-item` job targeting `.trashContainer`.
**Root cause:** When the JMAP account has no trash node (`trashId == nil`), three code paths fell back to using `homeId` as a substitute for `trashId`:
  1. `lookupItem(.trashContainer)` resolved to `homeId`, then returned a `FileProviderItem` with `itemIdentifier = .rootContainer` (because `nodeId == homeNodeId`). The system expected `.trashContainer` but received `.rootContainer` → `itemMismatch` error.
  2. `enumerateFolder(.trashContainer)` called `resolveParentId(.trashContainer)` which returned `homeId`. This caused the trash container enumerator to enumerate the home folder's children — showing all user files as contents of the trash container.
  3. `modifyItem` move-to-trash path: `resolveNodeId(.trashContainer)` returned `homeId`. Since `homeId != trashId` (nil), the `if newParentId == nodes.trashId` check was false, so the item was moved to the home folder instead of being deleted.
  4. `createItem` create-in-trash path: same `resolveNodeId` fallback would create the item in the home folder.
**Fix applied:**
  - Added `isSyntheticTrash: Bool` flag to `FileProviderItem`. When set, `itemIdentifier` always returns `.trashContainer` regardless of `nodeId`.
  - `lookupItem(.trashContainer)` now returns a synthetic read-only empty trash container (`FileNodeRights(mayRead: true, mayWrite: false)`) when `trashId == nil`, instead of falling back to the home node.
  - `enumerateFolder` returns immediately with zero items when the container is `.trashContainer` and `trashId == nil`.
  - `modifyItem` move section: when `parentItemIdentifier == .trashContainer` and `trashId == nil`, permanently deletes the node on the server (same as `deleteItem` already did) and calls `completionHandler(nil, [], false, .noSuchItem)`.
  - `createItem`: when `parentItemIdentifier == .trashContainer` and `trashId == nil`, returns `.noSuchItem` immediately.
**Lesson:** The `?? fallback` pattern for optional node IDs (e.g. `nodes.trashId ?? nodes.homeId`) looks safe but is deceptive — it silently routes operations to the wrong node with no indication that the fallback fired. Guard at the call site instead: when the optional is nil, explicitly handle the no-trash case. Never use the home node as a substitute for a missing trash node.

---

## BUG-017 — Permanent errors (e.g. name collision) misreported as `cannotSynchronize` after threshold

**Status:** Fixed
**Symptom:** If a user renames a file to a name that case-collides with a sibling (triggering `alreadyExists` → `filenameCollision`), Finder shows a generic sync error banner instead of a rename conflict dialog after a few failed attempts.
**Root cause:** `createItem` and `modifyItem` both have a failure-counter path: once the same operation fails `uploadFailureThreshold` times, they substitute `NSFileProviderError(.cannotSynchronize)` for the real mapped error. This was meant for transient errors exhausting their retry budget, but it also swallowed permanent errors like `alreadyExists` → `.filenameCollision`. The system needs the exact `.filenameCollision` code to show the user a rename dialog; `.cannotSynchronize` shows a generic error instead.
**Fix:** Added `isPermanentError(_:)` helper (delegates to `JmapError.isRetriable`). Permanent errors skip the failure counter entirely and are returned via `mapError` immediately.
**Lesson:** The threshold-to-cannotSynchronize substitution must only apply to retriable (transient) errors. Permanent errors carry semantic meaning that the system acts on (rename dialog, quota dialog, etc.) — substituting a generic sync error destroys that signal.

---

## Recurring mistakes to watch for

- **`privacy: .public` omitted** — every interpolated value in a logger call needs it
- **`await` inside `&&`/`||`** — always hoist to `let` first
- **Extension process is transient** — never rely on in-memory state surviving between requests; persist to disk. The extension is killed and restarted roughly every 60 seconds (visible as repeated SSE "cancelled" errors in the log with new PIDs). Every extension restart triggers a change poll. Design accordingly.
- **Two caches, one truth** — our DB and the system's FileProvider metadata cache can diverge; state token changes must keep both in sync
- **Retry paths need backoff** — any error handler that triggers a network call needs a cap
- **`try? removeItem` is not a reliable state reset** — silently fails when the target file is open by another process. Always reset persistent state via its own API (e.g. write `""` to the DB) rather than relying on file deletion.
- **Status should reflect user-visible work only** — don't set "Syncing" before knowing there is something to sync. Routine no-op polls are implementation details; showing them as "Syncing" creates noise and erodes trust in the status indicator.
- **`URLSession.default` is not a background session** — calling it `backgroundSession` doesn't make it one. Any upload in a FileProvider extension that takes more than a few seconds needs `URLSessionConfiguration.background(withIdentifier:)` or it will be cancelled when fileproviderd kills the extension.
- **Unset delegates are silent failures** — no compiler warning, no runtime error, no log entry. If a delegate is not assigned, the feature simply doesn't work. Every protocol-delegate wiring path needs a test that verifies the delegate actually receives a call.
- **Concurrency guards around `await` are TOCTOU races** — any flag checked before an `await` can change during the suspension. Re-check after every suspension point or take a snapshot before the first suspension.
- **`defer` inside a loop body is scoped to the iteration** — if the process is killed mid-loop, the current iteration's `defer` does not run. For files that must be cleaned up across process kills, write them to a location that can be swept at next startup, not to a process-scoped temp directory.
- **APFS returns NFD, JMAP requires NFC** — names from macOS FileProvider (`item.filename`) are in NFD-like form. The JMAP spec (Net-Unicode, RFC 5198) requires NFC. Always call `FilenameUtils.desanitize` (which NFC-normalizes) before sending a name to the server. Failing to do so causes round-trip mismatches: the server stores the NFC-normalized version; the client then computes a different ETag/hash because its in-memory name is NFD.
- **`caseInsensitiveNames: false` means the server is case-sensitive** — macOS APFS is case-insensitive. The client must send `compareCaseInsensitively: true` on all `FileNode/set` requests and must visually deduplicate case-variant siblings (via `FilenameUtils.caseCollisionSuffixes`) before presenting them to FileProvider. Never assume the server and macOS agree on what "same name" means.
