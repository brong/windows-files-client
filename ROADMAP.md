# Cloud Files API Feature Roadmap

Comprehensive audit of Windows Cloud Files API (cfapi) features not yet used in the sync engine, organized by impact.

## Tier 1 — Highest Impact

### ~~1. Dehydration / "Free up space"~~ ✓ Done

Implemented: `AUTO_DEHYDRATION_ALLOWED` hydration policy modifier, `NOTIFY_DEHYDRATE` / `NOTIFY_DEHYDRATE_COMPLETION` callbacks, `CfDehydratePlaceholder` for user-initiated "Free up space", and `FILE_ATTRIBUTE_UNPINNED` detection via FileSystemWatcher.

### 2. Sync status reporting

~~`CfUpdateSyncProviderStatus(connectionKey, status)` — tells Explorer whether we're idle, syncing, disconnected, or errored. Status appears in system tray and file explorer.~~ ✓ Done

- `CfReportSyncStatus(syncRootPath, status)` — shows specific error messages ("Network lost", "Auth expired") in Explorer instead of cryptic OS errors.

### ~~3. CANCEL_FETCH_DATA callback~~ ✓ Done

Implemented: cancels in-flight blob downloads when the user or OS aborts a hydration.

### ~~4. NOTIFY_DELETE / ACK_DELETE callback~~ ✓ Done

Implemented: blocking pre-delete callback propagates deletes to the server before the local file disappears, with veto support.

## Tier 2 — Important UX Polish

### 5. Thumbnail provider ✅

~~A COM `IThumbnailProvider` that returns image thumbnails for *dehydrated* placeholders without hydrating them. Essential now that dehydration is enabled — otherwise image folders show generic icons.~~ ✓ Done — `ThumbnailHandler` COM class uses `Blob/convert` (resizeImage action) to fetch server-generated thumbnails at the requested size. Registered via MSIX manifest as an ExeServer on FileNodeClient.Service.exe. `ThumbnailService` static registry bridges the COM handler to running SyncEngine instances with an in-memory LRU cache.

### 6. Context menu extensions

COM `IExplorerCommand` handlers for custom right-click entries: "Share link", "View on web", "Manage sharing". These appear in the cloud files section of the context menu.

**Requires spec support:** "View on web" needs a web URL per node — see [Spec Dependencies](#spec-dependencies).

### ~~7. NOTIFY_RENAME / ACK_RENAME callback~~ ✓ Done

Implemented: blocking pre-rename callback with veto support and `TARGET_IN_SCOPE` detection.

### 8. RecycleBinUri ✅

~~Server-side trash: local deletes move nodes to the JMAP `role:"trash"` folder with `onExists:"rename"` instead of permanently destroying them. Remote trash moves (from web UI or other clients) are detected in PollChanges and cleaned up locally.~~ ✓ Done

~~`StorageProviderSyncRootInfo.RecycleBinUri` — point to Fastmail's web trash so Explorer can show "Recover from cloud" after deletes.~~ ✓ Done — reads `trashUrl` from the FileNode account capability and sets `RecycleBinUri` during sync root registration.

### 9. Custom columns in Explorer ✅

~~`StorageProviderItemPropertyDefinitions` + `IStorageProviderItemPropertySource` COM interface to show per-file metadata columns: sharing status, sync state, etc.~~

Implemented: `StatusColumnManager` push-based approach (no COM handler needed) with Status and Sharing columns. Column definitions registered in `SyncRoot.Register`, values pushed after initial populate, incremental sync (updates + creates), and on local file changes ("Pending" status while uploading).

### ~~10. Process info~~ ✓ Done

Implemented: `CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO` included in `CfConnectSyncRoot` flags. Callbacks include process name, PID, and path for logging.

### ~~11. Block self-implicit hydration~~ ✓ Done

Implemented: `CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION` included in `CfConnectSyncRoot` flags.

### ~~12. Open/Close file notifications~~ ✓ Done

Implemented: `NOTIFY_FILE_OPEN_COMPLETION` / `NOTIFY_FILE_CLOSE_COMPLETION` callbacks with per-path open count tracking. Close notification triggers immediate upload enqueue for modified tracked files, bypassing FileChangeWatcher's 1-second debounce. FileChangeWatcher remains as fallback for new files, directory copies, pin/unpin, and edge cases.

### ~~13. CfOpenFileWithOplock~~ ✓ Done

Implemented: `CfOpenFileWithOplock` used in all OutboxProcessor upload paths (SHA1 check + upload streams) with graceful fallback to regular `FileStream` when oplock open fails (e.g. file open for writing, not a placeholder).

### 14. Per-operation sync status ✅

The `CF_OPERATION_INFO.SyncStatus` field (currently null in all our CfExecute calls) can provide specific error messages per failed operation, shown to the user in Explorer.

Implemented: `TransferError` accepts an optional message that populates `CF_OPERATION_INFO.SyncStatus` with a `CF_SYNC_STATUS` struct. Error paths in `FetchDataCallback` (no identity, download failure) and `StreamBlobAsync` (streaming failure) now pass human-readable messages shown in Explorer.

## Tier 3 — Nice to Have

### 15. Share handler (Windows 11+)

Native "Share" button in Explorer creates a Fastmail sharing link.

### 16. URI Source / "View online" ✅

~~COM handler that provides a URL for "View in browser" on cloud files.~~ ✓ Done — `IStorageProviderUriSource` COM handler reads `webUrlTemplate` from the FileNode account capability, replaces `{nodeId}` with the placeholder's FileIdentity. Registered via MSIX manifest as an ExeServer on FileNodeClient.Service.exe.

### 17. CfGetPlatformInfo

Feature detection for version-specific capabilities (integration number check).

### ~~18. Progressive hydration policy~~ ✓ Done

Implemented: switched from `Full` to `Progressive` hydration policy with HTTP Range request support. `FetchDataCallback` now attempts byte-range downloads via `DownloadBlobRangeAsync` and falls back to full downloads if the server returns 200 instead of 206 or on error (session-level fallback).

### ~~19. Blob/get for small files + digest verification~~ ✓ Done

Implemented: files ≤16KB are fetched inline via RFC 9404 `Blob/get` (base64 data + digest in a single JMAP call), avoiding a separate HTTP round-trip. All download paths (Blob/get, HTTP Range, full HTTP) now verify content integrity using the server's preferred digest algorithm (`sha` or `sha-256`) fetched concurrently via `Blob/get`. Digest mismatches log warnings but don't block downloads. Gracefully degrades when the server lacks the `urn:ietf:params:jmap:blob` capability.

## Tier 4 — Future/Niche

- **Search integration** (`IStorageProviderSearchHandler`) — cloud search results in Explorer, but requires Windows 11 24H2 Copilot+ PCs
- **Data validation** (`VALIDATE_DATA` callback + `VALIDATION_REQUIRED` modifier) — cfapi-level integrity checks (application-level digest verification already done via Blob/get, see #19)
- **Streaming mode** (`STREAMING_ALLOWED`) — for video/media playback without disk persistence
- **CfSetPinState** — programmatically pin/unpin files (we only *detect* pins currently)
- **Correlation vectors** — telemetry/request tracing
- **CfRevertPlaceholder** — "detach from cloud" / take offline permanently
- **Shell copy hook** (`IStorageProviderCopyHook`) — intercept folder copy/move/delete

## Spec Dependencies

Several cfapi features require capabilities beyond what the current JMAP FileNode spec provides. These are things the spec itself would need to support — the Windows client can't implement them with StorageNode as it stands today.

### ~~Thumbnail downloads~~ ✓ Resolved (Blob/convert implemented)

~~The `IThumbnailProvider` COM handler needs to return image data for dehydrated files without hydrating them.~~ Implemented via `Blob/convert` with `resizeImage` action from the blobext capability. The client calls `ConvertImageAsync(blobId, width, height)` to get a server-generated thumbnail at the requested size, then downloads the resulting blob as PNG.

### ~~Web URL per node~~ ✓ Resolved (URI Source implemented)

Implemented via `webUrlTemplate` in the FileNode account capability. The client reads the template (e.g. `https://www.fastmail.com/files/{nodeId}`) and the `IStorageProviderUriSource` COM handler substitutes the placeholder's FileIdentity at runtime.

### ~~RecycleBin URL~~ ✓ Resolved (RecycleBinUri implemented)

Implemented via `webTrashUrl` in the FileNode account capability. Set on `StorageProviderSyncRootInfo.RecycleBinUri` during sync root registration.

### ~~Range downloads~~ ✓ Resolved (Progressive hydration implemented)

Client-side range request support is implemented with automatic fallback. The spec should still mandate Range header support on the download URL for optimal performance, but it's no longer blocking.

### ~~Content hash~~ ✓ Not needed (can use Blob/get)

~~A `blobHash` or `contentHash` property on FileNode would let the client compare local vs. server content without re-downloading the blob.~~ The client can already fetch `digest:sha` via `Blob/get` on the node's `blobId` and compare against a local SHA before uploading. This is a client-side optimization to wire up, not a spec dependency.

### Quota (improves: Explorer storage display)

Explorer can display storage quota in the nav pane and folder properties. The spec needs a way to query total storage available and storage used. JMAP core defines `urn:ietf:params:jmap:quota` but it's unclear whether that's planned for FileNode accounts.

### Summary

| Spec need | Priority | Blocks |
|-----------|----------|--------|
| ~~Thumbnail downloads~~ | ~~High~~ | ~~Thumbnail provider (#5)~~ ✓ |
| ~~Web URL per node~~ | ~~High~~ | ~~URI Source (#16), Context menus (#6)~~ ✓ |
| ~~RecycleBin URL~~ | ~~Medium~~ | ~~RecycleBinUri (#8)~~ ✓ |
| ~~Range downloads~~ | ~~Medium~~ | ~~Progressive hydration (#18)~~ ✓ |
| ~~Content hash~~ | ~~Medium~~ | ~~— (improves conflict detection)~~ ✓ Not needed |
| Quota | Low | — (improves Explorer display) |

Sharing is already modeled in StorageNode (`ShareWith`, `SharedLinkUrl`, `IsSharedLinkEnabled`, `MyRights`) — the client just needs to wire up Set operations and the context menu/share handler UI. No spec changes needed for sharing features.

## Notes

The COM-based features (thumbnails, context menus, custom columns, share handler) all require Desktop Bridge packaging to register properly, so they'd likely come as a batch once we set up proper packaging.
