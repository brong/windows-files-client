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
| Clear error messages on failure | Partial | JmapError maps to NSFileProviderError; no user-visible error detail surfaced in UI |
| Automatic updates (Sparkle) | Partial | `UpdateManager` instantiated at app startup; "Check for Updates…" menu item wired. Requires Sparkle package + Info.plist `SUFeedURL`/`SUPublicEDKey` to activate — see comments in UpdateManager.swift |

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
| Upload in chunks matching server chunk size | Partial | Chunked upload implemented; does not negotiate chunk size from session caps |
| No re-upload / re-download of unchanged data | Done | State token + blob identity checks |
| Listen for changes via SSE, not polling | Done | PushWatcher with exponential backoff |
| No infinite loop / DoS on error | Done | BUG-007 fixed: 5-attempt cap on syncAnchorExpired; SSE backoff; extension process kills reset gracefully |
| Case-insensitive filename collision handling | Done | `compareCaseInsensitively: true` on all FileNode/set; `caseInsensitiveNames(accountId:)` reads server capability |
| Filename mangling safety (never rename unless user asked) | Partial | `sanitizeFilename` exists; no round-trip verification |
| Never accidentally wipe / trash content due to confused state | Partial | Echo suppression exists; anchor-expiry re-enumeration clears DB before repopulate |

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

## Remaining work (priority order)

1. **Sparkle package** — add Sparkle SPM dependency + `SUFeedURL`/`SUPublicEDKey` in Info.plist to fully activate auto-updates (wiring is done)
2. **PACC full RFC discovery** — remove Fastmail-hardcoded fallback; discover any server

4. **Distribution pipeline** — DMG / notarised package + appcast for Sparkle
5. **Uninstall cleanup** — script or helper to remove FileProvider domains + app group data
