# Fastmail Files — Apple Client Roadmap

Tracks progress against the requirements in `../use-cases.txt`. Status key:
- **Done** — shipped and working
- **Partial** — exists but incomplete or known gaps
- **Todo** — not started
- **Blocked** — waiting on something else

---

## User-facing features

| Requirement | Status | Notes |
|---|---|---|
| Download and install (package/DMG) | Todo | No distribution pipeline yet |
| Add login via OAuth + PACC discovery | Partial | OAuth works; PACC falls back to Fastmail-hardcoded issuer. Full RFC discovery not implemented. |
| Add account manually (session URL + bearer token) | Done | |
| Select which accounts to sync | Done | |
| Navigate files in Finder | Partial | BUG-005 mitigated: signalEnumerator after domain add; root cause not fully confirmed |
| Open a file (download on demand) | Done | |
| Select files/folders to stay hydrated | Todo | NSFileProviderItem `keepDownloaded` not wired up |
| Dehydrate files to free space | Todo | `evictItem` not exposed in UI |
| Remove one account without removing login | Done | |
| Remove entire login (removes all accounts + credentials) | Done | |
| See server changes immediately (online) | Done | SSE push + enumerateChanges |
| Resync changes missed while offline | Done | `cannotCalculateChanges` → full re-enumeration |
| Access downloaded files offline | Done | FileProvider caches hydrated files |
| Copy new files in, upload when online | Done | `createItem` implemented |
| Edit existing files (online and offline) | Done | `modifyItem` implemented |
| Reset cache without destroying server data | Done | `cleanAccount` |
| View JMAP protocol traffic log | Done | `jmap-traffic.log` in shared container |
| See sync state of all accounts | Partial | Status view exists; nodeCount sometimes shows 0 (BUG-006) |
| **See pending unsynced changes clearly** | **Todo** | **VERY IMPORTANT per spec. No pending-changes UI at all.** |
| OAuth access token auto-refresh (race-free) | Done | Single in-flight Task, all concurrent callers wait |
| Delete files on server | Done | `deleteItem` moves to trash |
| Rename files on server | Done | `modifyItem` handles rename |
| See quota usage | Todo | JMAP Quota capability not fetched or displayed |
| Clear error messages on failure | Partial | JmapError maps to NSFileProviderError; no user-visible error detail in UI |
| Automatic updates (Sparkle) | Todo | |

---

## OS / platform requirements

| Requirement | Status | Notes |
|---|---|---|
| Disable background traffic on low bandwidth / cellular | Todo | NWPathMonitor not wired up (BandwidthPolicy not implemented) |
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
| Upload in chunks matching server chunk size | Partial | Chunked upload implemented but doesn't negotiate chunk size from session |
| No re-upload / re-download of unchanged data | Done | State token + blob identity checks |
| Listen for changes via SSE, not polling | Done | PushWatcher with exponential backoff |
| **No infinite loop / DoS on error** | **Partial** | **BUG-007 fixed: 5-attempt cap on syncAnchorExpired loop. SSE has backoff. enumerateWorkingSet itself has no backoff yet.** |
| Case-insensitive filename collision handling | Todo | |
| Filename mangling safety (never rename unless user asked) | Partial | sanitizeFilename exists; no round-trip verification |
| Never accidentally wipe / trash content due to confused state | Partial | Echo suppression exists; anchor-expiry re-enumeration clears DB before repopulate |

---

## Author / maintainability requirements

| Requirement | Status | Notes |
|---|---|---|
| Clean, maintainable code | Partial | Ongoing |
| Single source of truth, edge-triggered UI | Done | SQLite + Darwin notifications + UserDefaults KVO |
| Allow user to "refresh" / force re-sync | Done | cleanAccount |
| Clear bug reports and diagnostic log export | Partial | DiagnosticsView stub; no log bundle export yet |
| Roadmap document | Done | This file |
| Bug log | Done | `BUGS.md` |
| Tests | Todo | Zero XCTest files |
| Capability / design decision log | Partial | DESIGN.md covers protocol decisions |

---

## Priority order for next work

1. **BUG-005** — Mitigated; needs real-world verification of the `signalEnumerator` fix
2. **BUG-007** — Fixed; needs real-world verification
3. **Pending changes visibility** (VERY IMPORTANT per use-cases.txt)
4. **Bandwidth / cellular policy** (NWPathMonitor)
5. **Quota display**
6. **Test suite** (zero tests is a shipping blocker for millions of users)
7. **Diagnostics bundle export**
8. **Automatic updates (Sparkle)**
9. **PACC full RFC discovery**
