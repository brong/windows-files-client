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
| Select files/folders to stay hydrated | Todo | NSFileProviderItem `keepDownloaded` not wired up |
| Dehydrate files to free space | Partial | Full-account eviction via "Reset Cache"; no per-file eviction UI |
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
| Automatic updates (Sparkle) | Partial | `UpdateManager` infrastructure exists but not instantiated or wired into any menu |

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
| Case-insensitive filename collision handling | Todo | |
| Filename mangling safety (never rename unless user asked) | Partial | `sanitizeFilename` exists; no round-trip verification |
| Never accidentally wipe / trash content due to confused state | Partial | Echo suppression exists; anchor-expiry re-enumeration clears DB before repopulate |

---

## Author / maintainability requirements

| Requirement | Status | Notes |
|---|---|---|
| Clean, maintainable code | Partial | Ongoing |
| Single source of truth, edge-triggered UI | Done | SQLite + Darwin notifications + UserDefaults KVO |
| Allow user to "refresh" / force re-sync | Done | `cleanAccount` |
| Clear bug reports and diagnostic log export | Partial | `DiagnosticsView` exports account/node text report; no os_log capture or traffic log ZIP bundle |
| Roadmap document | Done | This file |
| Bug log | Done | `BUGS.md` |
| Tests | Done | 135 tests passing (JmapClientTests, SyncEngineTests, NodeDatabaseTests, etc.) |
| Capability / design decision log | Partial | `DESIGN.md` covers protocol decisions |

---

## Remaining work (priority order)

1. **Sparkle auto-updates** — wire `UpdateManager` into app startup and add "Check for Updates…" menu item
2. **Per-file dehydration UI** — expose `evictItem` on individual files, not just full-account reset
3. **Diagnostics bundle** — capture os_log entries + traffic log into a ZIP for support
4. **PACC full RFC discovery** — remove Fastmail-hardcoded fallback; discover any server
5. **`keepDownloaded` pin** — let user mark files/folders to always stay hydrated
6. **Case-insensitive collision handling** — detect and handle name conflicts on case-insensitive volumes
7. **Distribution pipeline** — DMG / notarised package + appcast for Sparkle
8. **Uninstall cleanup** — script or helper to remove FileProvider domains + app group data
