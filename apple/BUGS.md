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
**Current mitigation:** On retry the server call is re-issued. For `modifyItem` (rename/move) the server returns success again and the DB is re-written — idempotent. For `createItem` see scenario 2.

### 2 — `createItem` retry creates a duplicate ⚠️ DANGEROUS
**Scenario:** `createItem` succeeds on the server and in our DB, extension dies before `completionHandler`. System retries → we call `FileNode/set create` again → a second node with the same name is created on the server (or an error if `onExists` rejects it).
**Result:** Two files on the server where the user intended one, or a spurious error on the retry. If the retry gets a new node ID, DB ends up with two entries for the same local file.
**Current mitigation:** `compareCaseInsensitively: true` and `onExists: "rename"` in some paths reduces collisions. Not fully resolved — idempotency key (e.g. include a client-generated UUID in the create and deduplicate on retry) would fix this properly.
**Status:** Open. Low frequency (extension must die in a specific 50–200 ms window), but possible.

### 3 — `deleteItem` (move to trash) succeeds on server, DB delete fails
**Scenario:** `client.updateNode(parentId: trashId)` succeeds. DB write (`upsertFromServer` / `remove`) then throws or extension dies.
**Result:** Node is in trash on server, DB still shows it at original location. Next `enumerateChanges` fetches the server change, re-deletes from DB, and signals the system — eventually self-correcting.
**Current mitigation:** Self-correcting within one sync cycle. Not dangerous.

### 4 — `modifyItem` content upload succeeds, `updateNodeContent` fails
**Scenario:** Blob is uploaded and stored on the server. `FileNode/set update { blobId }` then fails (network, auth, etc.).
**Result:** A blob exists on the server that nothing references. The file content on the server is stale (old blobId). Our DB still has the old blobId.
**Current mitigation:** On retry we re-upload the blob and re-call `updateNodeContent`. The orphaned blob is cleaned up by the server's GC. Not dangerous for user data.

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
**Current mitigation:** Harmless — `isPinned(id:)` returns false for unknown IDs. Could add a periodic cleanup (`DELETE FROM pinned_nodes WHERE nodeId NOT IN (SELECT id FROM nodes)`), but not urgent.

### 8 — `enumerateChanges` returns items not yet in DB
**Scenario:** Server reports a node as created/updated. We call `FileNode/get`, get the node back, write to DB, then call `observer.didUpdate(item)`. If `item(for:identifier:)` is called by the system before the DB write is committed (race with `database.save()`), it fetches a stale or missing entry.
**Result:** System gets a `noSuchItem` error for an item it just received as an update. Typically causes a brief Finder glitch; system retries.
**Current mitigation:** Low probability due to actor isolation. `database.save()` is called before `observer.finishEnumeratingChanges`. Not fully eliminated — a more robust fix would pass the fully-written items directly rather than relying on a subsequent `item(for:)` lookup.

---

## Recurring mistakes to watch for

- **`privacy: .public` omitted** — every interpolated value in a logger call needs it
- **`await` inside `&&`/`||`** — always hoist to `let` first
- **Extension process is transient** — never rely on in-memory state surviving between requests; persist to disk. The extension is killed and restarted roughly every 60 seconds (visible as repeated SSE "cancelled" errors in the log with new PIDs). Every extension restart triggers a change poll. Design accordingly.
- **Two caches, one truth** — our DB and the system's FileProvider metadata cache can diverge; state token changes must keep both in sync
- **Retry paths need backoff** — any error handler that triggers a network call needs a cap
- **`try? removeItem` is not a reliable state reset** — silently fails when the target file is open by another process. Always reset persistent state via its own API (e.g. write `""` to the DB) rather than relying on file deletion.
- **Status should reflect user-visible work only** — don't set "Syncing" before knowing there is something to sync. Routine no-op polls are implementation details; showing them as "Syncing" creates noise and erodes trust in the status indicator.
