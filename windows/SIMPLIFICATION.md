# Windows simplification — handoff

**Read this before `RELIABILITY.md`.** The reliability port should land on the
simplified code, not the old one.

Goal of the whole exercise: as little code as possible while staying correct.
The Windows client was 19,436 lines of C# with no tests; the same product on
Apple is ~12,800 (plus 2,500 of tests). Most of the gap was structural
duplication, not essential complexity.

## State of the tree (as synced from the Mac)

Phases 1–3 below are **written but never compiled** — the Mac has no `dotnet`
and these projects only build on Windows anyway. Everything is staged in git
(not committed). Your first job is to make it build and run, then commit it as
one change:

```
refactor(windows): collapse JMAP account scoping, merge Service into App, dedupe LoginManager
```

Windows is now 15,206 lines (−4,230) and 5 projects (−2).

### Phase 1 — `AccountScopedJmapClient` folded into `JmapClient` (done)

`AccountScopedJmapClient.cs` (1,067 lines) was a copy of `JmapClient.cs` with
`AccountId` swapped for `_accountId`; only 121 lines differed, and the two had
drifted (one paged concurrently, one sequentially; one piggybacked `accessed`
updates on multi-call requests, one didn't). Now:

- `JmapClient` owns a private `Connection` (HttpClient, session, call-id
  counter, quota-forbidden flag) shared by every per-account view;
  `ForAccount(id)` returns another `JmapClient` on the same connection and
  only the owner disposes the HttpClient.
- One `CallBatchAsync` does serialize → POST → parse → map-by-callId for every
  request; `Batch.Get<T>(callId, method)` validates and deserializes.
  `QueryAndGetAsync` covers the three query→get chains.
- Shared `PostBlobAsync` (all raw uploads, HTTP/1.1 forced), `CombineChunksAsync`
  (chunked + delta), `UpdateFileNodeAsync` (replace-blob + move).
- Capability properties are computed straight from `JmapSession`
  (no `_xResolved` flag pairs); `JmapSession` has one
  `AccountCapabilityProperty` helper instead of seven hand-rolled lookups.

`IJmapClient` is unchanged, so `SyncEngine`/`OutboxProcessor`/`SyncCallbacks`
didn't move.

### Phase 2 — one process (done)

Deleted: `FileNodeClient.Ipc` (pipes, JSON-RPC envelope, serializer),
`FileNodeClient.Service` (`Program`, `SyncHostedService`, `IpcCommandHandler`,
its `AppLogger`), `ServiceClient`, `ServiceLauncher`. Moved into
`FileNodeClient.App`: `LoginManager`, `AccountSupervisor`, `CredentialStore`,
`NetworkMonitor`, `AccountCache`. New in App:

- `SyncController.cs` — the UI's view of the sync side. Replaces
  `ServiceClient` + `IpcCommandHandler` + `SyncHostedService`'s broadcast
  throttle: builds `AccountInfo`/`ActivitySnapshot` from supervisors, coalesces
  activity events (100 ms), tracks rejected counts and recently-completed items.
- `Models.cs` — the UI records that used to be the IPC wire types
  (`AccountInfo`, `OutboxEntry`, `ActivitySnapshot`, … and `VersionInfo`).
- `Program.cs` — merged entry point: arg parsing, logger, unhandled-exception
  nets, `ComServerHost.Register()`, `ThumbnailPipeServer`, `LoginManager`,
  `SyncController`, `TrayIcon`.

The reason this was safe: the first week of the client (commits Feb 13–18,
before `fe4ae30`) ran cfapi, hydration, uploads and nav-pane registration
inside the tray process. The split commit cites no API requirement, and the
COM handlers were added three weeks later and simply attached to the
long-lived process. See `DESIGN.md` §1.

Behaviour changes to expect:
- No Start/Stop/Restart Service UI; **Exit stops sync** (dialog text updated).
- One log file `%LOCALAPPDATA%\Fastmail\FileNodeClient\debug.log` (no `app.log`).
- `Detach` now disconnects cfapi *before* unregistering (same order as `Clean`).
- MSIX manifest: `Service` application removed; `com:ExeServer` and the
  `cloudFiles` extension now hang off `FileNodeClient.App.exe`; one startup task.
- `Program.cs` accepts `-Embedding` (COM launching us for the URI-source
  class) and a second instance launched that way exits silently.

### Phase 3 — `LoginManager` deduplicated (done)

1,577 → 954 lines. Six copy-pasted "stop → dispose → new supervisor → wire
events → start → unwind on failure" blocks became three primitives:
`StartSupervisorAsync`, `StopSupervisorAsync`, `PersistSession`. Refresh/Clean
are `RestartSupervisorAsync` with a different `afterStop`; CleanUp/Detach are
`RemoveAccountAsync` with a different sync-root action. `LoginCredential`
(in `CredentialStore.cs`) replaces the four OAuth args threaded through every
signature; `LoginSession` carries it and OAuth refreshes update it in place.
`UpdateLoginAsync` now connects with the new credentials *before* tearing the
old session down, so a bad token changes nothing. `ConfigureLoginAsync` was
removed (nothing called it). `AccountSupervisor.ScopeKey` replaces the three
hand-built `$"{username}/{accountId}"` strings.

## Phase 0 for you — build, fix, smoke-test, commit

```
dotnet.exe build windows/FileNodeClient.sln
```

Things I could not verify and would check first if it fails:

- **Implicit usings.** `FileNodeClient.App` has `UseWindowsForms`, so
  `System.Windows.Forms` and `System.Drawing` are global usings. `Timer` is
  qualified as `System.Threading.Timer` in `LoginManager`/`SyncController`;
  watch for any other collision in the moved files (`Label`, `Padding`, …).
- **Collection expressions / tuple conversions** in `JmapClient.CallBatchAsync`
  callers (`[(method, args, callId)]` targeting
  `IEnumerable<(string, object, string)>`), and `capabilities = [.. capabilities, FileNodeCapability]`.
- **Named-before-positional args** (`StartSupervisorAsync(session, account, clean: false, ct)`) —
  legal only because each named arg is in its own position.
- `Batch` and `Connection` are C# 12 primary-constructor classes.
- `SyncController.BuildActivitySnapshot` uses `supervisor.SyncProgress is { } sp` on a nullable tuple.

Runtime smoke test (dev inner loop in `CLAUDE.md`, or `dotnet.exe run --project FileNodeClient.App -- --debug --token … --clean`):

1. Tray icon appears; with no accounts the Manage window opens itself.
2. Add login (OAuth and app-password paths) → sync root registered, nav-pane
   entry, placeholders appear. `debug.log` shows `COM server registered` and
   `Thumbnail pipe server started` from the App process.
3. Explorer: thumbnails on dehydrated images; "View online" works (this is the
   `com:ExeServer` on `App.exe` — if it fails, check `-Embedding` handling and
   that the registered manifest is the new one).
4. Activity pane updates during an upload (progress bytes) and shows
   recently-completed; rejected-file context menu (Retry / Dismiss / Delete).
5. Pause / Resume / Sync now; Refresh; Clean; Detach; Remove login; Enable a
   second account; Update credentials; Re-authenticate. After each, the
   credential in Credential Manager reflects the enabled-account set.
6. OAuth token refresh persists (`PersistSession` path) — force by setting a
   short expiry or waiting.
7. Kill the app; relaunch: cached accounts show "Connecting…" instantly, then
   go live.

Then commit.

## Phase 4 — `SyncEngine` (complete 2026-09-08)

**Status.** Steps 1–3 landed as 1.0.79.0 (`EnsureInSync`/`EnsureDirectoryFull`,
`WalkFromHome`, then `ApplyServerNode`/`MoveLocalItem`/`RemoveNode`/`ApplyTreeLevel`/
`ApplyChangedNodeAsync`); step 4 as 1.0.80.0 (I3 deferred server changes + server-delete-
under-edit fallback, R3 page loop, D2 consistent enumeration + re-confirm, D1 warm-start
verify). The ACL-ordering invariant is documented above the apply path in `SyncEngine.cs`.
Step 5 landed 2026-09-08: the sixteen static cfapi helpers (convert / identity /
in-sync / always-full / read identity / hydrate / dehydrate / pin / open) live in
`CfApi.cs`; `SyncEngine` is 2,457 lines (from 2,620) and holds sync logic only.
Smaller follow-ups also done: `JmapContext` replaced by `IJmapClient.ScopeKey`,
`GetStateAsync` removed, ROADMAP's Service references fixed. Still open:
folding `SYNCING.md` into `DESIGN.md` (content judgement, not mechanical) and
the `ManageAccountsForm` button-handler helper. The original plan follows.


`FileNodeClient.Windows/SyncEngine.cs` is 2,620 lines and contains **four
independent implementations of "make the local tree match these server
nodes"**:

| Path | Lines (approx) | Input | Own copy of… |
|---|---|---|---|
| `BuildTreeAndCreatePlaceholders` | 584–716 | all nodes, cold start | BFS tree, dedupe sanitized names, create placeholders, map, `TrackFolderPermissions`, SetInSync-or-Convert fallback, `MarkDirectoryAlwaysFull` fallback chain, `ApplyWriteProtections`, pinned-dir detection |
| `PopulateFromCacheAsync` | 758–869 | cache snapshot | restore mappings, existence/size/mtime check, the same `MarkDirectoryAlwaysFull` fallback chain |
| `ReconcileFromServerAsync` | 869–1057 | all nodes vs. mappings | prune gone ids, the same BFS, rename-if-moved, create-if-missing, the same SetInSync-or-Convert and `MarkDirectoryAlwaysFull` chains |
| `PollChangesAsync` apply loops | 1057–1285 | changes (updated / created / destroyed) | remove-if-left-tree, rename, create-if-missing, map, SetInSync, protection scopes, pinned auto-hydrate, delete |

The reliability gaps `RELIABILITY.md` lists for Windows — I3 (dirty-vs-server
conflict silently dropped), D2 (prune on unstable enumeration), R3
(state-token ordering), D1 (warm-start verification) — are all "these four
paths disagree" bugs. Porting each Apple fix into four places is the wrong
move; collapse first, then port once. Apple's equivalent is a single
`NodeDatabase.upsertFromServer` write.

Target shape (names are suggestions):

```csharp
// One node, one decision. Idempotent. Safe to call from every path.
void ApplyServerNode(FileNode node, string parentLocalPath)
//   expectedPath = parentLocalPath + Sanitize(node.Name)
//   if outbox has pending for node → conflict policy (I3), not silent skip
//   if mapped at a different path → rename on disk (+descendant mappings)
//   if not on disk → CreatePlaceholders(parent, [node])
//   map; blobId; TrackFolderPermissions; EnsureInSync(expectedPath, node)
//   if folder → MarkDirectoryAlwaysFull; ApplyWriteProtection
//   if file under pinned dir and newly created → hydrate

void RemoveNode(string nodeId)                      // destroyed / left tree / pruned
//   outbox checks; descendant mappings; SuspendFolderProtection; DeleteLocalItem

void EnsureInSync(string path, FileNode node)       // the SetInSync → ReadPlaceholderNodeId → Convert fallback, once
void EnsureDirectoryFull(string path, string nodeId) // the MarkDirectoryAlwaysFull fallback chain, once

IEnumerable<(FileNode node, string parentLocalPath)> WalkFromHome(FileNode[] all) // the BFS, once (cycle-safe, orphan-excluding — cf. Apple TreeBuilder)
```

Then the four paths become:

- cold start: `foreach (n, p) in WalkFromHome(all) ApplyServerNode(n, p)`
- reconcile: `RemoveNode` for `cached − server` (only after a **consistent**
  enumeration — D2: capture `queryState`, restart if it changes, cross-check
  `total`), then the cold-start loop
- poll: `RemoveNode` for destroyed, `ApplyServerNode` for created ∪ updated
  (resolve parent path via `ResolveLocalPathAsync`; nodes whose parent isn't
  in the tree → `RemoveNode`)
- warm start: restore mappings, then poll (or reconcile on `cannotCalculateChanges`)
  — plus D1's disk↔cache walk feeding the outbox

Do it in this order, building and running after each step:

1. Extract `EnsureInSync` and `EnsureDirectoryFull` and replace the ~6 inline
   copies of those fallback chains. Pure refactor, no behaviour change.
2. Extract `WalkFromHome`; use it in `BuildTreeAndCreatePlaceholders` and
   `ReconcileFromServerAsync`. Pure refactor.
3. Write `ApplyServerNode`/`RemoveNode` from the `PollChangesAsync` versions
   (they're the most complete: protection scopes, pinned hydrate), then switch
   cold start and reconcile onto them. Behaviour converges on the poll path's.
4. Now port I3 (conflict copy) into `ApplyServerNode` — one place. Then R3
   (persist state token after the batch; make `hasMoreChanges` a loop, not
   recursion). Then D2 in `ReconcileFromServerAsync`. Then D1 in
   `PopulateFromCacheAsync`.
5. Move the ~300 lines of `static unsafe` cfapi helpers at the bottom of
   `SyncEngine` (`ConvertToPlaceholder*`, `UpdatePlaceholderIdentity`,
   `MarkDirectoryAlwaysFull`, `SetSyncState`, `ReadPlaceholderNodeId`,
   `Open*`, `DehydratePlaceholder`, `HydratePlaceholder`, `ClearPinState`)
   into one `CfApi` static class alongside `PlaceholderManager`/`SyncRoot`, so
   cfapi P/Invoke isn't spread over eight files.

Realistic outcome: 2,620 → ~1,200 lines, and the reliability items become
one-site changes.

Invariants to keep while doing it (all learned the hard way — `DESIGN.md` §6/§18):

- Update `_pathToNodeId`/`_nodeIdToPath` **before** touching the disk on a
  server-driven rename, so the FSW echo finds the new path and doesn't
  round-trip to the server.
- Echo suppression is mtime-based, never key-presence.
- Wrap disk writes in read-only folders with `SuspendFolderProtection`.
- Dehydrate + mark-in-sync atomically; pin propagation is async.
- Never read a dehydrated file (`GENERIC_READ` triggers your own hydration).

## Phase 5 — tests (harness + first 18 scenarios landed 2026-09-08)

**Status.** `FileNodeClient.Tests` exists: `FakeJmapClient` (in-memory FileNode server with
state/changes paging, cannotCalculateChanges, inconsistent enumeration, onExists semantics,
notFound, a `Hold` gate for in-flight uploads/moves) + `SyncRootFixture` (real temporary
sync root, warm restart, log capture). Covered: populate, server rename/move/delete/create
via poll, R3 paging, I3 deferred change (edit and delete behind a pending local rename),
D1 warm-start (missing file/folder re-created, offline edit uploaded, dehydrated mtime
ignored), D2 reconcile (inconsistent → no prune; alive → re-confirmed; gone → pruned).
Second tranche (same day): conflict copy / newest-wins both directions / no-conflict
in-place; Clean with read-only + hydrated files; JmapClient against `FakeJmapHttpHandler`
(stable paging, restart on queryState change, never-stable → inconsistent, short page vs
total, error responses, changes+get batching with result references); SyncOutbox
(coalescing, base blobId freeze, create+delete, rename of pending create, move on pending
edit, dequeue ordering, rejected-stays-pending, persistence); PlaceholderManager name
round-trips; `WalkFromHome` (static core); `OAuthTokenHandler` (proactive refresh, 401 →
refresh+retry, single refresh under concurrency, rejected refresh). 64 tests, ~1 minute.
Found on the way: revoked-but-unexpired tokens were unrefreshable (TryRefreshAsync's
"already refreshed" check only looked at expiry) — fixed.


There is no Windows test project. After Phase 4 the pure-logic surface is
small enough to cover cheaply; mirror the Apple tests where one exists:

- `JmapClient` against a fake `HttpMessageHandler`: batch response mapping,
  `error` method responses, quota 403 fallback, chunk-size computation,
  delta-upload chunk reuse, SSE `state` parsing.
- `SyncOutbox`: coalescing (edit×N → one upload; create+delete → nothing;
  create+rename → one create), priority ordering, retry/rejection state,
  persistence round-trip.
- `PlaceholderManager.SanitizeName`/`DesanitizeName` round-trips (reserved
  names, trailing dots, fullwidth mapping).
- `WalkFromHome` (cycles, orphans, duplicate sanitized names).
- `LoginManager` with a fake `IJmapClient` factory: add/remove/enable/update
  and the credential-store contents after each.

`dotnet new xunit -n FileNodeClient.Tests` (net9.0-windows, reference Jmap +
Windows + App). `FileNodeClient.Windows` needs Windows to compile, so the
tests are Windows-only too; that's fine.

## Smaller follow-ups (any order, each < 1 hour)

- `ManageAccountsForm.cs` (2,100 lines): the per-button `On*Clicked` handlers
  share a "confirm → `SetAllButtonsEnabled(false)` → await → snapshot → render"
  skeleton; one helper would remove ~300 lines. Don't rewrite the form.
- `IJmapClient.Context`: only `ScopeKey`/`AccountId` are used; expose
  `ScopeKey` directly and drop `JmapContext`.
- `JmapClient.GetStateAsync` vs `GetCurrentStateAsync` — one is enough.
- `ROADMAP.md` still says the COM handlers are "on FileNodeClient.Service.exe".
- `windows/SYNCING.md` (859 lines) overlaps `DESIGN.md`; fold what's still
  true into `DESIGN.md` and delete it.

## Don't

- Don't reintroduce a service process or an IPC layer.
- Don't port a reliability fix into more than one of the four sync paths —
  collapse first.
- Don't add a "watchdog" process for R2; a crash-restart is a startup-task /
  Task Scheduler concern, not more in-process code.
