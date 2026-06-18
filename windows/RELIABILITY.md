# Reliability & Recovery Roadmap

This is the companion to `ROADMAP.md`. Where `ROADMAP.md` tracks **cfapi
features** (thumbnails, columns, dehydration, context menus — nearly all done),
this document tracks the axis that turns the proof-of-concept into something we
can ship to non-technical customers: **correctness, integrity, and recovery.**

> **Goal.** Detect *every* case where synchronisation has failed or local and
> server data have drifted, and recover in a way a non-technical user can
> understand. The client should never silently lose or corrupt data, never
> appear "idle" when it is actually stalled, and never require a log file or a
> developer to get unstuck.

## How this was found

Four independent deep reviews of the sync engine, outbox, hydration, and
status-reporting paths. The highest-severity findings were verified directly
against the code (cited with ✓). Items marked *(reported)* are from review and
should be confirmed as the first step of their work item.

The through-line: the client is well-built for the happy path, but it **trusts
its own cache and the network, rarely verifies, and fails silently when
something goes wrong.** Bulletproofing is mostly about adding verification,
making failure visible, and providing understandable recovery.

## Platform status

The gaps below were found in the **Windows** client (the most mature), and the
`file:line` citations are all Windows C#. The **Apple** (Swift) client is a
different, DB-backed implementation and a separate review found it **already
handles several of the architectural gaps**, with tests:

| Gap | Windows | Apple |
|---|---|---|
| I3 conflict (dirty + server change) | gap | ✅ conflict-copy / `onExists:newest` (`FileProviderExtension.swift:689`) |
| D2 server-delete pruning | gap | ✅ generation-counter BFS, tested (`NodeDatabase.swift:240`) |
| R3 state-token crash window | gap | ✅ token-after-changes, idempotent replay (`SyncEngine.swift:65`) |
| R5 cache corruption | gap | ✅ SQLite WAL + temp-DB fallback (`NodeDatabase.swift:109`) |
| R2 watchdog | gap | ✅ N/A — OS manages the FileProvider extension lifecycle |

Apple progress (Mac fixes land first, since we build/test here; Windows follows
using this same roadmap):
- **I1 ✅ done** (`cff4881`) — `downloadBlob` verifies `digest:sha`; corrupt
  downloads are rejected, not served.
- **I2 ✅ done** (`4d32623`) — `uploadBlob`/`directWrite` re-verify the stored
  blob's digest (the chunked/delta paths were already server-validated).
- **V1 ✅ done** (`dbbc196`) — menu bar shows "Last synced …" (informational).
- **D3 ✅ done** (`9618a74`) — SSE idle-timeout watchdog detects half-open
  connections and reconnects + catches up. (No separate fallback poll needed:
  the existing reconnect→`signalEnumerator` path recovers missed changes.)
- **D4 ✅ done** (`c733227`) — permanent JMAP SetErrors (notFound, invalidProperties,
  invalidArguments, tooLarge) are classified non-retriable, so they surface
  promptly instead of burning the upload-retry budget. (The count-based
  threshold→`blockedUploadCount`→"N stuck — press Retry" escalation already
  existed.)
- **R1 ✅ done** (`a7ed026`) — "Verify & Repair" menu item: tested
  `SyncEngine.verifyAgainstServer()` reports DB↔server drift, and the action
  forces a full reconcile (re-fetch missing, prune stale) + retries blocked
  uploads, per account.
- **V2/V3 ✅ done** (`943b4f7`) — `OperationHint` carries an error; failed
  operations are forwarded with their reason and rendered in red in the menu
  bar. Failure reasons now reach the user instead of living only in the log.

- **D5 ✅ done** (`0328b70`, `910fa20`) — **push reconnect storm** (found in the
  field: a dev client hammered the server ~1 req/s/account, growing a 482 MB
  traffic log). Two root causes: (1) `handleEvent` polled whenever a push payload
  *contained* a FileNode key, so the server's per-reconnect `connect` event
  triggered a needless poll even when the state value was unchanged — now it
  signals only on a *changed* value; (2) `connect()` reset the reconnect backoff
  on every 200, so a connection that flapped within ~1s reconnected at 1 Hz —
  now backoff resets only after a connection stays up ≥10s, else grows
  exponentially. **Part 3** (`820f534`) fixed the structural trigger: the push
  endpoint is session-level but the client ran one extension process per
  account, so N accounts opened N redundant SSE connections that fought over the
  server's single push connection. A cross-process `flock` lease (`PushLease`)
  now elects one push owner per login that fans out per-account signals;
  non-owners stand down (and take over if the owner dies). (BUG-026.)

**Apple reliability tranche complete.** All gaps that apply to the Apple client
are resolved (I1/I2/V1/D3/D4/D5/R1/V2/V3 this pass; I3/D2/R3/R5/R2 already
handled). Next: port these fixes to the **Windows** C# client using this
roadmap (the push reconnect-storm lesson is now DESIGN.md pitfall #38), and F1
(conditional writes) once the draft + server land.

## For the Windows port — start here

The Apple client is the **reference implementation**: every item below is fixed
and unit-tested there. For each, read the Apple commit (the diff and its tests —
the tests encode the expected behavior), then port to the Windows file cited in
the pillars below. Work the phases in order (see "Suggested sequencing").

Mirror the Apple unit tests in the Windows test project where one exists; the
Apple `JmapClient`/`FuseMount` targets are unit-tested via `swift test`.

| Item | Apple reference (commit) | Windows target / note |
|---|---|---|
| 🔴 I1 download digest enforced | `cff4881` (`downloadBlob` + `serverBlobDigestSha`) | `SyncCallbacks.cs:737` — `VerifyDigest` currently logs-and-serves; make mismatch reject + retry |
| 🟠 I2 upload digest re-verified | `4d32623` (`verifyUploadedBlob`) | upload path (`OutboxProcessor`/`JmapClient`) — verify single-shot/direct-PUT results |
| 🔴 I3 conflict copy (dirty + server change) | already on Apple (`onExists:newest`/`rename`, `DECISIONS.md` #12) | `SyncEngine.cs:1247-1264` — currently drops one side silently |
| 🟠 D1 disk↔cache↔server verify | partial on Apple | `SyncEngine.cs` warm-start (`PopulateFromCache`) |
| 🟠 D2 stable enumeration / prune | Apple generation-counter BFS (`NodeDatabase`) | `SyncEngine.cs:875-901` — set-difference over unstable `position` paging |
| 🟠 D3 SSE idle-timeout | `9618a74` (`consumeWithIdleTimeout`) | `JmapClient.cs` SSE loop — no idle timeout |
| 🟠 D4 permanent-error classification | `c733227` (`JmapError.isRetriable`) | error tiers (`OutboxProcessor.cs`, DESIGN §12 table) |
| 🟠 D5 push: poll-on-change + backoff | `0328b70`, `910fa20` | Windows SSE handler — parts 1&2 apply. **Part 3 (per-login lease) likely N/A**: Windows runs one Service process, not one per account |
| 🟡 R1 Verify & Repair | `a7ed026` (`verifyAgainstServer` + force-reconcile) | add a user action: clear state token → full reconcile + retry rejected |
| 🟡 R2 service watchdog | N/A on Apple (OS-managed) | Windows-specific: auto-restart `Service.exe` |
| 🟡 R3 state-token crash window | already safe on Apple (token after changes) | `SyncEngine.cs:1280` — persist/apply ordering |
| 🟡 R5 cache corruption fallback | Apple SQLite WAL + temp-DB fallback | `NodeCache.cs` load — add integrity check + fallback |
| 🟡 V1 last-synced signal | `dbbc196` | surface `lastSyncTime` in tray/status |
| 🟠 V2/V3 surface failure reasons | `943b4f7` | per-file error + reason in Explorer/activity |
| ⚡ Bulk + hybrid first-paint populate | `ecd030f` (`reachableFromHome`) + DESIGN §Initial Populate | Windows already bulk-loads (`PopulateFullAsync`); add hybrid first-paint |

Cross-platform lessons are also distilled as **`DESIGN.md` pitfalls #34–39**.
F1 (server-adjudicated conditional writes, `draft-gondwana-jmap-conditional`) is
deferred until the draft + Fastmail server support land.

## Severity legend

| | Meaning |
|---|---|
| 🔴 **Critical** | Silent data loss or corruption. Fix before any customer ship. |
| 🟠 **High** | Sync can silently stall or drift; user cannot tell. |
| 🟡 **Medium** | Recoverable, but opaque or manual. |

---

## Pillar 1 — Integrity (no silent loss or corruption)

### 🔴 I1. Download digest mismatch is logged, not enforced ✓verified

`VerifyDigest` (`SyncCallbacks.cs:737`) logs `Log.Error` on a hash mismatch and
**returns the bytes anyway**; the placeholder is then marked in-sync. A
corrupted download is served to applications and looks fully synced.

**Fix.** Treat a digest mismatch as a hard failure: do **not** write the data,
do **not** mark in-sync. Call `TransferError` with a user-readable message
("File failed integrity check — will retry"), retry the download (it's
transient), and after N consecutive failures surface it as a per-file error
(see V2). Applies to all paths that call `VerifyDigest` (Blob/get, Range, full,
streaming — `SyncCallbacks.cs:779/819/840/877/962`).

### 🔴 I2. Upload content is never verified after combine *(reported)*

The client sends `digest:sha` with `Blob/set` but does not confirm the stored
blob's digest matches the local file after the node is updated. A blob
corrupted in transit that the server accepts is recorded as done.

**Fix.** After `Blob/set` + `FileNode/set`, compare the server's `digest:sha`
(already fetchable via `Blob/get`, no data transfer) against the local SHA
computed during upload. On mismatch, re-upload (transient); escalate after N.
This also gives us the local↔server content-hash comparison the design notes as
a cheap pre-upload optimisation.

### 🔴 I3. Server change to a locally-dirty file is silently dropped ✓verified

When the outbox has a pending change for a path/node, `PollChanges` **skips**
the server's `update`/`destroy` (`SyncEngine.cs:1247-1264`, and the update skip
~`:1089`). Local silently wins; the server side is lost with no record. This is
the conflict gap that started the conditional-write design.

**Fix (two-stage).**
- **Now (no spec dependency):** when a server change collides with a dirty
  local file, do not discard either side. Apply the server version to the
  original path and preserve the local edit as a **conflict copy**
  (`<name> (conflicted copy, this PC, <date>).<ext>`), then re-enqueue the
  conflict copy as a new file. Nothing is ever lost; the user sees both.
- **Later (Pillar 5 / F1):** replace the racy "skip if dirty" with a
  server-adjudicated conditional write (`ifUnchangedBy`), so the common case is
  a clean compare-and-swap and the conflict copy becomes the rare fallback when
  the write is genuinely rejected.

---

## Pillar 2 — Detection (notice drift and stalls)

### 🟠 D1. No disk ↔ cache ↔ server verification on warm start

Warm start checks only existence + size/mtime per cached entry, then trusts the
cache. A file edited while the service was down — if the post-`Connect`
`FileChangeWatcher` misses the event — is never re-uploaded. Files on disk with
no cache entry are only caught by a separate later pass. There is no three-way
reconciliation.

**Fix.** On warm start, walk the disk tree and the cache together and classify
every node: in-sync / locally-changed / locally-missing / untracked-on-disk /
cache-only. Feed discrepancies into the outbox or the repair queue. This is the
foundation the scrubber (A2) reuses.

### 🟠 D2. Reconciliation prunes by unstable pagination ✓verified (mechanism)

`ReconcileFromServerAsync` (`SyncEngine.cs:869`) deletes local nodes whose ids
are in the cache but absent from a single `QueryAllFileNodeIdsAsync` snapshot.
That snapshot uses **`position`-offset pagination with no `queryState`
stability check** (`AccountScopedJmapClient.cs:483`). A network failure throws
(safe — no partial delete), but **concurrent server mutation during pagination**
can shift a live node past a page boundary, drop it from the set, and **delete
it locally.**

**Fix.** Make the enumeration consistent: capture `queryState` on the first
page and **restart the enumeration if it changes** across pages (or use anchor
based paging); cross-check collected count against `total`. Then gate deletion
behind that consistent snapshot, and — belt and braces — re-confirm a node is
truly gone (single `FileNode/get`) before deleting local data. Never delete on
an inconsistent enumeration.

### 🟠 D3. SSE half-open connection → silent stall

The SSE read loop has no per-message/idle timeout. A half-open connection
(server stops sending state updates but keepalive pings continue, or TCP is
silently dead) leaves the loop "alive": no error, no fallback poll, status shows
Idle. Sync stops indefinitely with no signal.

**Fix.** Add an idle-timeout watchdog on the SSE stream (e.g. force-reconnect if
no event — including ping — within ~2× the ping interval). Independently, run a
low-frequency **safety poll** (e.g. every few minutes) regardless of push, so a
dead push degrades to slightly-delayed sync rather than no sync. Pair with V1 so
a genuine stall becomes visible.

### 🟠 D4. Stuck outbox entries never escalate *(reported)*

Permanent-but-misclassified failures — e.g. a parent permanently deleted
server-side, an orphaned node, `notFound` on a non-delete — are treated as
transient and retried forever at the 60s backoff cap. They never become
"rejected," so the user is never told.

**Fix.** Add an escalation policy: after K consecutive failures *or* T elapsed
with the same error class, promote the entry to a surfaced state ("Couldn't
sync — needs attention", with the reason). Distinguish "parent gone" (offer:
recreate path / discard) from generic retries. `attemptCount`/`lastError` must
reach the UI (see V2).

---

## Pillar 3 — Recovery (an understandable way back)

### 🟡 R1. No verify-and-repair / re-download action

There is no user-facing "something looks wrong — fix it." The only blunt tools
are "Clean" (wipes the cache, full re-sync) and "Restart Service."

**Fix.** A **Verify & Repair** action (per account) that runs the scrubber (A2)
on demand: re-checks digests of hydrated files, re-reconciles disk/cache/server,
re-downloads anything corrupt, re-enqueues anything unsynced, and reports "N
items checked, M repaired" in plain language. Plus a per-file "re-download"
context action for a single suspect file.

### 🟡 R3. State token persisted only after all changes applied ✓verified

`SaveNodeCache(changes.NewState)` runs at `SyncEngine.cs:1280`, after the
destroy/create/update loops and the `hasMoreChanges` recursion. A crash in
between reprocesses the batch on restart. Reapplication is mostly idempotent
today, but it is fragile and interacts badly with destroys.

**Fix.** Make change-application restartable: persist progress within a batch
(or persist the new state token only once the batch is durably applied, with
each applied change individually idempotent and safe to replay). Bound the
`hasMoreChanges` loop (convert recursion to iteration with a guard).

### 🟡 R2. No service watchdog

If `Service.exe` crashes it stays dead until the user manually restarts it from
the tray/UI; in-flight state is whatever was last persisted.

**Fix.** Supervise the service: auto-restart with backoff (the tray App already
detects the dead pipe — have it relaunch, or use a Windows recovery mechanism),
and surface "sync service restarted" rather than silent death. Ensure restart
recovers cleanly from persisted outbox + cache (depends on R3 durability).

### 🟡 R4. Orphaned sync-root registrations not auto-cleaned

Stale cfapi sync-root / shell registrations survive account removal and
reinstall (DESIGN pitfall #16), causing confusing startup failures.

**Fix.** Audit registered sync roots against known accounts on startup; unregister
orphans (or offer the user a one-click cleanup). Already partially detected in
`LoginManager`; complete the cleanup path.

### 🟡 R5. Cache corruption is undetected on load

Cache writes are atomic (temp+rename ✓), but load only checks a version int. A
truncated-but-parseable cache is trusted, silently dropping entries.

**Fix.** Add an integrity check (length/record-count or checksum) on load; on
failure, fall back to full reconciliation rather than trusting a partial cache.

---

## Pillar 4 — Visibility (the user can always tell)

### 🟠 V1. No "last successful sync" signal

A stalled account is indistinguishable from an up-to-date one — both show Idle.

**Fix.** Track `lastSuccessfulSyncUtc` per account (updated on every clean
poll/push round-trip) and surface it ("Up to date · just now" / "Last synced 3
days ago"). If it exceeds a threshold while the client believes it's connected,
show a warning state. This is the single most important honesty signal.

### 🟠 V2. Failures are log-only

Digest failures (I1), poll failures, quota-exceeded, push death (D3), and stuck
entries (D4) live only in the log file.

**Fix.** Route these to a small, plain-language **in-app problem list** ("3
files couldn't sync", each with a reason and an action). Tie into the existing
activity/rejected UI. Expose `attemptCount`/`lastError` over IPC.

### 🟡 V3. Error states don't say why or what to do

Improved recently for account-row errors (commit `275e9be`), but most states
still lack an actionable message.

**Fix.** Every surfaced error carries: what happened, whether it's retrying, and
what (if anything) the user should do. One consistent vocabulary across tray,
account row, and problem list.

---

## Pillar 5 — Cross-cutting architecture

These tie the point-fixes together; they are where most of the "bulletproof"
value lives.

### A1. Consistency invariants + self-repair

The in-memory mappings (`pathToNodeId` ↔ `nodeIdToPath`) can diverge (orphaned
entries, stale paths after failed renames). Add invariant checks (bidirectional
consistency; every mapping has a disk object; no orphaned ids) that run after
major operations and **repair** rather than just log.

### A2. Verification / repair scrubber

A single engine that reconciles **disk ↔ cache ↔ server**, treats the digest as
a hard gate, quarantines mismatches into visible conflict copies, and produces
one honest signal: *consistent* or *N items need attention*. Runs (a) on demand
(R1), (b) periodically in the background, (c) after any anomaly. This subsumes
D1, D2 verification, I1/I2 re-checks, and R1, and is the component that lets us
claim we detect *every* drift case.

### A3. Durability of outbox + state

Outbox is atomic but debounced ~1s (a crash loses the last <1s of mutations) and
not fsync'd. Combined with R3, define and test the durability guarantee: what
survives a power loss, and prove each persisted operation is idempotent on
replay.

---

## Future track (depends on the conditional-set draft + server)

### F1. Server-adjudicated conditional writes

Replace the racy "skip server change if local is dirty" (I3) with
`ifUnchangedBy` per-object compare-and-swap, per
`draft-gondwana-jmap-conditional-00`. Common case: clean CAS on `blobId`. Rare
case: server rejects with `stateMismatch` → fall back to the conflict-copy path
from I3. **Blocked on** IETF progress + Fastmail server support; the I3 interim
fix ships independently and remains the fallback afterward.

---

## Suggested sequencing

Severity-first, with detection/visibility brought forward because they *are* the
goal.

| Phase | Theme | Items |
|---|---|---|
| **0 — Stop silent data loss** | Surgical, high-severity | I1, I3 (interim conflict-copy), R3 (+ bound the loop) |
| **1 — Make failure visible** | Honesty signals | V1, V2, D3 (stall detection + safety poll), D4 (escalation) |
| **2 — Detect & repair drift** | The core "bulletproof" layer | D1, D2, R5, A1, A2, R1 (Verify & Repair), I2 |
| **3 — Resilience & cleanup** | Survive crashes cleanly | R2 (watchdog), R4 (orphan cleanup), A3 (durability), V3 |
| **4 — Conditional writes** | *Future / dependent* | F1 |

Phases 0–1 are the minimum bar for putting it in front of a non-technical user:
nothing is lost silently, and when something needs attention the user is told in
plain language. Phase 2 is what makes it genuinely trustworthy. Phase 3 hardens
the edges. Phase 4 lands when the protocol/server catch up.

## Validating "bulletproof"

A reliability roadmap is only as good as its tests. Each item above should land
with **fault-injection coverage**, not just unit tests:

- **Crash points:** kill the service at each step of upload, download, change
  application, and cache write; assert recovery with no loss/dupes/corruption.
- **Corruption injection:** flip bytes in a download/upload and assert I1/I2
  catch it; truncate the cache and assert R5 recovers.
- **Concurrency:** mutate the tree server-side during reconciliation and assert
  D2 never deletes a live node.
- **Stall simulation:** half-open the SSE socket and assert D3 detects and
  recovers, and V1 reflects the gap.
- **Drift seeding:** create disk/cache/server divergence and assert the
  scrubber (A2) detects and repairs every class from D1.
