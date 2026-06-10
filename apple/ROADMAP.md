# Fastmail Files — Apple Client Roadmap

Tracks progress against the requirements in `../use-cases.txt`. Status key:
- **Done** — shipped and working
- **Partial** — exists but incomplete or known gaps
- **Todo** — not started

---

## User-facing features

| Requirement | Status | Notes |
|---|---|---|
| Download and install (package/DMG) | Todo | No distribution pipeline yet |
| Add login via OAuth + PACC discovery | Partial | OAuth + PACC DNS discovery wired; falls back to Fastmail-hardcoded issuer only — full RFC multi-server discovery not implemented |
| Add account manually (session URL + bearer token) | Done | "Advanced: Use App Password" in Settings |
| Select which accounts to sync | Done | Per-account enable/disable toggle in Settings |
| Navigate files in Finder | Done | BUG-005 fixed: stale state token reset via DB API on domain re-add |
| Open a file (download on demand) | Done | |
| Select files/folders to stay hydrated | Done | `contentPolicy` returns `.downloadEagerlyAndKeepDownloaded` for pinned items; Finder shows "Keep Downloaded" context menu; pin state stored in `pinned_nodes` SQLite table (survives re-enumeration). Eager download gated on `bandwidthPolicy.allowsBackgroundDownload` — never on cellular/Low Data Mode |
| Dehydrate files to free space | Done | `contentPolicy: .downloadEligibleForEncryptedStorage` enables Finder's per-file "Remove Download"; "Free Up Space" button in Settings evicts all downloaded content without resetting sync state |
| Remove one account without removing login | Done | |
| Remove entire login (removes all accounts + credentials) | Done | |
| See server changes immediately (online) | Done | SSE push + enumerateChanges |
| Resync changes missed while offline | Done | `cannotCalculateChanges` → full re-enumeration |
| Access downloaded files offline | Done | FileProvider caches hydrated files |
| Copy new files in, upload when online | Done | `createItem` implemented |
| Edit existing files (online and offline) | Done | `modifyItem` implemented |
| Reset cache without destroying server data | Done | `cleanAccount` |
| View JMAP protocol traffic log | Done | `jmap-traffic.log` in shared container |
| See sync state of all accounts | Done | Status view; nodeCount correct (BUG-006 fixed) |
| See pending unsynced changes clearly | Done | ActivityView in Settings shows pending/active/error queue |
| OAuth access token auto-refresh (race-free) | Done | Single in-flight Task; all concurrent callers wait |
| Delete files on server | Done | `deleteItem` moves to trash |
| Rename files on server | Done | `modifyItem` handles rename |
| See quota usage | Done | Fetched via JMAP Quota/get; progress bar in Settings |
| Clear error messages on failure | Done | ExtensionStatus.error string shown in red below account status; auth failure shows "Authentication required" |
| Automatic updates (Sparkle) | Partial | `UpdateManager` instantiated at app startup; "Check for Updates…" menu item wired. Requires Sparkle package + Info.plist `SUFeedURL`/`SUPublicEDKey` to activate — see comments in UpdateManager.swift |
| Never serve/store corrupt file content | Done | Download (`downloadBlob`) and single-shot upload (`uploadBlob`/`directWrite`) verify `digest:sha`; mismatch is rejected + retried. Reliability I1/I2 (BUG-021/022) |
| Detect a silently-stalled sync | Done | SSE idle-timeout watchdog (150s) detects half-open push connections → reconnect + catch-up. Reliability D3 (BUG-023) |
| See when files last synced | Done | "Last synced …" line in the menu bar header. Reliability V1 |
| See *why* a file failed to sync | Done | Failure reasons shown in red in the menu-bar activity list (not just an aggregate count). Reliability V2/V3 (BUG-025) |
| Verify & Repair / force a clean re-sync | Done | Menu item: reconciles local state against the server (re-fetch missing, prune stale) and retries blocked uploads. Reliability R1 |

See `../windows/RELIABILITY.md` for the cross-platform reliability tracker. Items I3/D2/R3/R5 (conflict handling, server-delete pruning, crash-safe state token, DB corruption fallback) were already handled in earlier work.

---

## OS / platform requirements

| Requirement | Status | Notes |
|---|---|---|
| Disable background traffic on low bandwidth / cellular | Done | BandwidthPolicy (NWPathMonitor) wired into upload and download paths |
| Start sync service on OS startup | Done | FileProvider extension auto-started by framework |
| Clean shutdown | Done | FileProvider framework handles |
| Secure token storage (Keychain) | Done | |
| Clean up all artifacts on account remove | Done | DB, session cache, status, blobs, UserDefaults all cleared |
| Clean up all artifacts on uninstall | Todo | No uninstall script; FileProvider domains persist until manually removed |

---

## Server / protocol requirements

| Requirement | Status | Notes |
|---|---|---|
| Efficient queries (parentId / role) | Done | BFS by parentId; home/trash by role |
| Upload in chunks matching server chunk size | Done | Reads chunkSize and maxDataSources from blob2 cap; legacy blob cap removed |
| No re-upload / re-download of unchanged data | Done | State token + blob identity checks |
| Listen for changes via SSE, not polling | Done | PushWatcher with exponential backoff |
| No infinite loop / DoS on error | Done | BUG-007 fixed: 5-attempt cap on syncAnchorExpired; SSE backoff; extension process kills reset gracefully |
| Case-insensitive filename collision handling | Done | `compareCaseInsensitively: true` on all FileNode/set; `caseInsensitiveNames(accountId:)` reads server capability |
| Filename mangling safety (never rename unless user asked) | Done | Round-trip tests added; substitute chars (U+2215, U+A789) rejected at createItem/modifyItem |
| Never accidentally wipe / trash content due to confused state | Done | BFS generation counter prunes server-deleted nodes after full re-enumeration |

---

## Author / maintainability requirements

| Requirement | Status | Notes |
|---|---|---|
| Clean, maintainable code | Partial | Ongoing |
| Single source of truth, edge-triggered UI | Done | SQLite + Darwin notifications + UserDefaults KVO |
| Allow user to "refresh" / force re-sync | Done | `cleanAccount` |
| Clear bug reports and diagnostic log export | Partial | `DiagnosticsView` exports account/node stats + status JSONs + last 50 KB of JMAP traffic log as a shareable `.txt`; os_log capture requires Console.app (sandbox restriction) |
| Roadmap document | Done | This file |
| Bug log | Done | `BUGS.md` |
| Tests | Done | 135 tests passing (JmapClientTests, SyncEngineTests, NodeDatabaseTests, etc.) |
| Capability / design decision log | Partial | `DESIGN.md` covers protocol decisions |

---

## Architecture concern: accountId is not globally unique — RESOLVED

**Status: Fixed by DomainIdentity refactor (commits e9aaed2 / 3a484b4)**

All keys now use `domainId = loginId~accountId` where `loginId = email@serverHost` (deterministic, not a UUID). Every item in the original collision table is resolved — see those commits for details.

---

**Original concern (for reference):**

**Severity: correctness bug for multi-server users; latent for single-server users**

`accountId` values in JMAP are unique only within a single login session — i.e. per `(server, username)` pair. Two separate logins (even to the same Fastmail server with different email addresses, or to two completely different servers) can return identical `accountId` strings. Additionally, `nodeId` and `blobId` values are scoped per account, so they too are only meaningful within a `(server, accountId)` context.

### Current state (wrong)

All identifiers are keyed on bare `accountId`:

| Thing | Current key | Collision risk |
|---|---|---|
| `NSFileProviderDomain` identifier | `accountId` | Two logins same `accountId` → system treats as same domain |
| SQLite database file | `nodes-<accountId>.sqlite` | Same path → row collisions, corrupted state |
| Session cache file | `session-<loginId>.json` | `loginId` is UUID so this is OK currently, but see below |
| Status file | `status-<accountId>.json` | Shared container collision |
| UserDefaults sync-state key | `accountId` | Wrong state read |
| `NodeRecord` primary key | `id` (nodeId only) | nodeId N in account A ≠ nodeId N in account B, but stored in same rows |

### Required re-architecture

#### 1. Login identity: `(serverHost, username)` → stable `loginId`

`loginId` should be a deterministic string derived from `(serverHost, username)` — e.g. `"brong@fastmailteam.com@api.fastmail.com"` (double `@` is fine for an internal key; username is already an email address). Using a stable composite rather than an opaque UUID means the same login re-authenticated from scratch produces the same `loginId`, which is essential for database continuity and domain re-registration. This value is internal and not directly displayed to users.

#### 2. Account identity: `(loginId, accountId)` → composite key everywhere

Every place that currently uses bare `accountId` as a key must use a `(loginId, accountId)` composite:

- `NSFileProviderDomain` identifier → `"\(loginId):\(accountId)"` (colon is safe, not valid in email)
- Status file → `status-<loginId>-<accountId>.json`
- UserDefaults keys → `"\(loginId):\(accountId)"`

#### 3. NodeRecord database scoping — two options

**Option A: one DB per `(loginId, accountId)`**  
File named `nodes-<loginId>-<accountId>.sqlite`. Within that file `id` alone is sufficient as the PK (nodeIds are unique within an account). Closest to the current structure (`nodes-<accountId>.sqlite`); migration is a file rename. Extension already opens one account at a time, so no cross-DB joins needed.

**Option B: one DB per login**  
File named `nodes-<loginId>.sqlite`, all of that login's accounts share it. Requires `(accountId, id)` composite PK and `accountId` column on every table. More complex schema and migration, but enables cross-account queries if ever needed.

Option A is recommended: simpler schema, simpler migration, aligns with how the extension already works.

#### 4. Migration path

This is a breaking schema change. Migration requires:
1. Re-derive `loginId` from existing session data (sessionURL + stored username)
2. Rename database files
3. Re-register `NSFileProviderDomain` identifiers (system will see them as new domains and do a fresh enumeration)
4. Clear old status/UserDefaults keys

### Impact on existing code

- `AppViewModel`: `LoginInfo.loginId` should become `username@serverhost`, not UUID
- `NodeDatabase`: schema migration to add `loginId` column and update PK
- `FileProviderExtension`: `accountId` property should become `(loginId, accountId)` pair
- `ActivityTracker`, `PushWatcher`, `SyncEngine`: all pass `accountId` around — need to pass the pair
- `FileProviderItem.itemIdentifier`: already `nodeId`-based, but the domain it lives in needs the correct identifier

---

## Remaining work (priority order)

1. **accountId uniqueness re-architecture** — correctness fix for multi-server users; required before any public multi-server launch (see above)
2. **Sparkle package** — add Sparkle SPM dependency + `SUFeedURL`/`SUPublicEDKey` in Info.plist to fully activate auto-updates (wiring is done)
3. **PACC full RFC discovery** — remove Fastmail-hardcoded fallback; discover any server
4. **Distribution pipeline** — DMG / notarised package + appcast for Sparkle
5. **Uninstall cleanup** — script or helper to remove FileProvider domains + app group data
