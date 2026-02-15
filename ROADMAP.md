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

### 5. Thumbnail provider

A COM `IThumbnailProvider` that returns image thumbnails for *dehydrated* placeholders without hydrating them. Essential now that dehydration is enabled — otherwise image folders show generic icons.

**Requires spec support:** see [Spec Dependencies](#spec-dependencies) below.

### 6. Context menu extensions

COM `IExplorerCommand` handlers for custom right-click entries: "Share link", "View on web", "Manage sharing". These appear in the cloud files section of the context menu.

**Requires spec support:** "View on web" needs a web URL per node — see [Spec Dependencies](#spec-dependencies).

### ~~7. NOTIFY_RENAME / ACK_RENAME callback~~ ✓ Done

Implemented: blocking pre-rename callback with veto support and `TARGET_IN_SCOPE` detection.

### 8. RecycleBinUri

`StorageProviderSyncRootInfo.RecycleBinUri` — point to Fastmail's web trash so Explorer can show "Recover from cloud" after deletes.

**Requires spec support:** see [Spec Dependencies](#spec-dependencies) below.

### 9. Custom columns in Explorer

`StorageProviderItemPropertyDefinitions` + `IStorageProviderItemPropertySource` COM interface to show per-file metadata columns: sharing status, sync state, etc.

### 10. Process info

`CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO` makes callbacks include which app triggered the hydration (name, PID, path). Better logging and could power "App X is downloading files" notifications.

### 11. Block self-implicit hydration

`CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION` prevents our own process from accidentally triggering hydration callbacks (e.g., when our FileChangeWatcher reads attributes).

## Tier 3 — Nice to Have

### 12. Open/Close file notifications

`NOTIFY_FILE_OPEN_COMPLETION` / `NOTIFY_FILE_CLOSE_COMPLETION` — more reliable change detection than FileSystemWatcher. Close notification tells us exactly when a file is done being edited.

### 13. Share handler (Windows 11+)

Native "Share" button in Explorer creates a Fastmail sharing link.

### 14. URI Source / "View online"

COM handler that provides a URL for "View in browser" on cloud files.

**Requires spec support:** see [Spec Dependencies](#spec-dependencies) below.

### 15. CfOpenFileWithOplock

Opens files with proper opportunistic locks, preventing conflicts during sync operations. More robust than `File.OpenHandle`.

### 16. CfGetPlatformInfo

Feature detection for version-specific capabilities (integration number check).

### 17. Progressive hydration policy

Switch from `Full` to `Progressive` so large file opens complete as soon as the needed byte range arrives, with background download continuing.

**Requires spec support:** see [Spec Dependencies](#spec-dependencies) below.

### 18. Per-operation sync status

The `CF_OPERATION_INFO.SyncStatus` field (currently null in all our CfExecute calls) can provide specific error messages per failed operation.

## Tier 4 — Future/Niche

- **Search integration** (`IStorageProviderSearchHandler`) — cloud search results in Explorer, but requires Windows 11 24H2 Copilot+ PCs
- **Data validation** (`VALIDATE_DATA` callback + `VALIDATION_REQUIRED` modifier) — end-to-end integrity checks
- **Streaming mode** (`STREAMING_ALLOWED`) — for video/media playback without disk persistence
- **CfSetPinState** — programmatically pin/unpin files (we only *detect* pins currently)
- **Correlation vectors** — telemetry/request tracing
- **CfRevertPlaceholder** — "detach from cloud" / take offline permanently
- **Shell copy hook** (`IStorageProviderCopyHook`) — intercept folder copy/move/delete

## Spec Dependencies

Several cfapi features require capabilities beyond what the current JMAP FileNode spec provides. These are things the spec itself would need to support — the Windows client can't implement them with StorageNode as it stands today.

### Thumbnail downloads (blocks: Thumbnail provider)

The `IThumbnailProvider` COM handler needs to return image data for dehydrated files without hydrating them. The server must be able to serve a scaled-down version of a file's content. Two options:

- **Preferred:** size parameters on the download URL template (e.g. `{blobId}?maxWidth=256&maxHeight=256`), so the client can request thumbnails at the size Explorer asks for without downloading the full blob.
- **Alternative:** a `thumbnailBlobId` property on FileNode pointing to a pre-rendered thumbnail.

The StorageNode model already has `Width`/`Height`/`Orientation`, which indicates the server understands image dimensions — it just needs to serve resized versions.

### Web URL per node (blocks: URI Source, Context menus)

The "View online" URI handler (`IStorageProviderUriSource`) and "Open in browser" context menu both need a web URL for each file/folder. Options:

- A **`webUrl`** property on FileNode, or
- A **URL template** in the session capabilities (e.g. `https://www.fastmail.com/files/{nodeId}`) the client can construct from.

`SharedLinkUrl` already exists but is for public sharing, not the authenticated owner's view.

### RecycleBin URL (blocks: RecycleBinUri)

`StorageProviderSyncRootInfo.RecycleBinUri` needs a single URL pointing to the provider's trash view on the web. This belongs in the session capabilities rather than on individual nodes:

```json
"https://www.fastmail.com/dev/files": {
  "trashUrl": "https://www.fastmail.com/files/trash"
}
```

### Range downloads (blocks: Progressive hydration)

Switching from `Full` to `Progressive` hydration policy requires the server to support HTTP Range requests on the blob download endpoint — returning `206 Partial Content` for byte-range requests. This isn't a JMAP method per se, but the spec should mandate Range header support on the download URL.

### Content hash (improves: conflict detection)

A `blobHash` or `contentHash` property on FileNode would let the client compare local vs. server content without re-downloading the blob. Currently the client has no way to detect whether a local edit matches what's already on the server, so it re-uploads unconditionally. Not strictly blocking any cfapi feature, but important for robust sync.

### Quota (improves: Explorer storage display)

Explorer can display storage quota in the nav pane and folder properties. The spec needs a way to query total storage available and storage used. JMAP core defines `urn:ietf:params:jmap:quota` but it's unclear whether that's planned for FileNode accounts.

### Summary

| Spec need | Priority | Blocks |
|-----------|----------|--------|
| Thumbnail downloads | High | Thumbnail provider (#5) |
| Web URL per node | High | URI Source (#14), Context menus (#6) |
| RecycleBin URL | Medium | RecycleBinUri (#8) |
| Range downloads | Medium | Progressive hydration (#17) |
| Content hash | Medium | — (improves conflict detection) |
| Quota | Low | — (improves Explorer display) |

Sharing is already modeled in StorageNode (`ShareWith`, `SharedLinkUrl`, `IsSharedLinkEnabled`, `MyRights`) — the client just needs to wire up Set operations and the context menu/share handler UI. No spec changes needed for sharing features.

## Notes

The COM-based features (thumbnails, context menus, custom columns, share handler) all require Desktop Bridge packaging to register properly, so they'd likely come as a batch once we set up proper packaging.
