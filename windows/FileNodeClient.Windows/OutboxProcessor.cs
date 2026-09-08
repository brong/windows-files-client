using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using FileNodeClient.Logging;
using FileNodeClient.Jmap;
using FileNodeClient.Jmap.Models;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FileNodeClient.Windows;

/// <summary>
/// How to resolve a content conflict — the server copy diverged from the version a
/// local edit was based on. See DESIGN §6.
/// </summary>
public enum ConflictResolution
{
    /// <summary>Always keep both versions: the local edit becomes a server-renamed
    /// copy ("doc (2).txt"); the original reverts to the server's content. Never loses data.</summary>
    ConflictCopy,

    /// <summary>Atomically overwrite the server only if the local file is newer
    /// (onExists:"newest"); otherwise fall back to a conflict copy.</summary>
    NewestWins,
}

public class OutboxProcessor : IDisposable
{
    private const int MaxConcurrency = 4;

    /// <summary>
    /// Strategy used when a content conflict is detected. Defaults to the always-safe
    /// conflict-copy; can be set to NewestWins for fewer duplicate files on single-user
    /// multi-device setups.
    /// </summary>
    public ConflictResolution ConflictStrategy { get; set; } = ConflictResolution.ConflictCopy;


    private readonly SyncOutbox _outbox;
    private readonly SyncEngine _engine;
    private readonly IJmapClient _jmapClient;
    private readonly JmapQueue _queue;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _online = true;
    private volatile string? _trashNodeId;
    private readonly SemaphoreSlim _workerSlots = new(MaxConcurrency, MaxConcurrency);
    private readonly object _workerLock = new();
    private readonly List<Task> _workerTasks = new();

    // Recycle Bin restore support: track recently trashed items so we can
    // restore from server trash instead of re-uploading.
    private record TrashedInfo(string NodeId, string? BlobId);
    private readonly ConcurrentDictionary<string, TrashedInfo> _recentlyTrashed
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _trashedPathByNodeId = new();

    private readonly string _logPrefix;

    private void MarkRejectedAndNotInSync(PendingChange change, string reason)
    {
        _outbox.MarkRejected(change.Id, reason);
        if (change.LocalPath != null)
        {
            try { SyncEngine.SetNotInSync(change.LocalPath); }
            catch (Exception ex) { Log.Debug($"{_logPrefix} SetNotInSync failed for {change.LocalPath}: {ex.Message}"); }
        }
    }

    public OutboxProcessor(SyncOutbox outbox, SyncEngine engine, IJmapClient jmapClient, JmapQueue queue, string logPrefix)
    {
        _outbox = outbox;
        _engine = engine;
        _jmapClient = jmapClient;
        _queue = queue;
        _logPrefix = logPrefix;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => DispatchLoop(_cts.Token));
    }

    public void SetOnline(bool online)
    {
        _online = online;
    }

    public void SetTrashNodeId(string? trashNodeId)
    {
        _trashNodeId = trashNodeId;
    }

    /// <summary>
    /// Record that a node was trashed locally (sent to Recycle Bin).
    /// Called from SyncEngine.HandleDeleteRequestAsync so we can restore
    /// from server trash instead of re-uploading if the user restores.
    /// </summary>
    public void RecordTrashed(string localPath, string nodeId)
    {
        _recentlyTrashed[localPath] = new TrashedInfo(nodeId, null);
        _trashedPathByNodeId[nodeId] = localPath;
    }

    private async Task DispatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_online)
                {
                    await Task.Delay(5000, ct);
                    continue;
                }

                // Wait for a worker slot to be available
                await _workerSlots.WaitAsync(ct);

                var change = _outbox.DequeueNext();
                if (change == null)
                {
                    _workerSlots.Release();
                    try { _outbox.WaitForWork(TimeSpan.FromSeconds(10), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _outbox.MarkProcessing(change.Id);
                var task = ProcessWorkerAsync(change, ct);
                lock (_workerLock)
                    _workerTasks.Add(task);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error($"{_logPrefix} Outbox dispatch error: {ex}");
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessWorkerAsync(PendingChange change, CancellationToken ct)
    {
        try
        {
            var completed = await ProcessChangeAsync(change, ct);
            if (completed)
            {
                _outbox.MarkCompleted(change.Id);
                _engine.OnOutboxEntryCompleted();
            }
            else
                _outbox.MarkRetry(change.Id);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HTTP timeout or other non-shutdown cancellation — treat as transient failure
            Log.Error($"{_logPrefix} Outbox timeout for {change.LocalPath ?? change.NodeId}");
            _outbox.MarkFailed(change.Id, "Operation timed out");
        }
        catch (OperationCanceledException)
        {
            // App shutdown — remove from processing so state is clean
            _outbox.MarkFailed(change.Id, "Cancelled");
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException && change.IsDirtyContent)
        {
            // File was still being written when we opened the stream — retry silently
            Log.Info($"{_logPrefix} Outbox: file not ready for {Path.GetFileName(change.LocalPath)}, will retry ({ex.InnerException.Message})");
            _outbox.MarkRetry(change.Id);
        }
        catch (HttpRequestException ex) when (change.IsDirtyContent)
        {
            Log.Error($"{_logPrefix} Outbox: upload failed for {Path.GetFileName(change.LocalPath)}: {ex.Message}");
            if (ex.InnerException != null)
                Log.Error($"{_logPrefix}   Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");

            var code = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : 0;

            // Only reject permanently when the file itself is the problem
            // (RFC 8620 §6.1: upload errors return RFC 7807 problem details)
            if (code == 413) // Payload Too Large — file won't ever fit
            {
                MarkRejectedAndNotInSync(change, $"File too large for server ({code})");
            }
            else
            {
                // Everything else is retriable: 400 (rate limit), 403 (bad token),
                // 404 (account not found), 5xx, network errors, timeouts
                _outbox.MarkFailed(change.Id, $"HTTP {code}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        catch (IOException ex) when (change.IsDirtyContent)
        {
            // File locked or still being copied — always retry (backoff caps at 60s)
            Log.Info($"{_logPrefix} Outbox: file not ready for {Path.GetFileName(change.LocalPath)}, will retry ({ex.Message})");
            _outbox.MarkFailed(change.Id, ex.Message);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested
            && ex.Message.Contains("maxSizeBlobSet"))
        {
            Log.Error($"{_logPrefix} Outbox: file too large for {Path.GetFileName(change.LocalPath)}: {ex.Message}");
            MarkRejectedAndNotInSync(change, "File too large for server");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex.Message.Contains("forbidden") || ex.Message.Contains("Forbidden")))
        {
            // Forbidden usually means bad/expired token — retriable after re-auth
            Log.Error($"{_logPrefix} Outbox: permission denied for {change.LocalPath ?? change.NodeId}: {ex.Message}");
            _outbox.MarkFailed(change.Id, ex.Message);
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // Shutdown — resource already disposed, nothing to do
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Transient/unknown errors — always retry (backoff caps at 60s)
            Log.Error($"{_logPrefix} Outbox process error for {change.LocalPath ?? change.NodeId}: {ex.Message}");
            _outbox.MarkFailed(change.Id, ex.Message);
        }
        finally
        {
            try { _workerSlots.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private const FileAttributes DehydratedFlag = (FileAttributes)0x00400000;

    private async Task<bool> ProcessChangeAsync(PendingChange change, CancellationToken ct)
    {
        if (change.IsDeleted)
        {
            await ProcessDeleteAsync(change, ct);
            return true;
        }

        // Skip upload for dehydrated placeholders — these are server-side files,
        // not local changes (e.g. stale outbox entry surviving a clean/re-register)
        if (change.IsDirtyContent && change.LocalPath != null && !change.IsFolder)
        {
            try
            {
                var attrs = File.GetAttributes(change.LocalPath);
                if ((attrs & DehydratedFlag) != 0)
                {
                    Log.Info($"{_logPrefix} Outbox: skipping dehydrated placeholder {Path.GetFileName(change.LocalPath)}");
                    return true; // Treat as completed — remove from outbox
                }
            }
            catch (FileNotFoundException)
            {
                return true; // File gone — nothing to upload
            }
            catch { /* proceed with normal upload attempt */ }
        }

        if (change.IsFolder && change.NodeId == null)
            return await ProcessFolderCreateAsync(change, ct);

        if (change.IsDirtyContent)
            return await ProcessUploadAsync(change, ct);

        if (change.IsDirtyLocation)
            return await ProcessMoveAsync(change, ct);

        return true;
    }

    private async Task ProcessDeleteAsync(PendingChange change, CancellationToken ct)
    {
        if (change.NodeId == null)
            return; // Nothing to delete on server

        var name = change.LocalPath != null ? PlaceholderManager.DesanitizeName(Path.GetFileName(change.LocalPath)) : change.NodeId;

        // Fetch blobId before trashing so we can use it for restore from Recycle Bin
        string? blobId = null;
        try
        {
            var nodes = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.GetFileNodesAsync([change.NodeId], ct), ct);
            if (nodes.Length > 0)
                blobId = nodes[0].BlobId;
        }
        catch { /* non-critical — best effort for restore support */ }

        try
        {
            if (_trashNodeId != null)
            {
                Log.Info($"{_logPrefix} Outbox: trashing node {change.NodeId}");
                await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.MoveFileNodeAsync(change.NodeId, _trashNodeId, name, "rename", ct: ct), ct);
            }
            else
            {
                Log.Info($"{_logPrefix} Outbox: destroying node {change.NodeId}");
                await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.DestroyFileNodeAsync(change.NodeId, ct), ct);
            }
        }
        catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
        {
            Log.Info($"{_logPrefix} Outbox: node {change.NodeId} already gone on server");
        }

        // Update _recentlyTrashed with the blobId we fetched
        if (blobId != null && _trashedPathByNodeId.TryGetValue(change.NodeId, out var originalPath))
        {
            _recentlyTrashed.AddOrUpdate(originalPath,
                new TrashedInfo(change.NodeId, blobId),
                (_, old) => old with { BlobId = blobId });
        }
    }

    private async Task<bool> ProcessFolderCreateAsync(PendingChange change, CancellationToken ct)
    {
        if (change.LocalPath == null || !Directory.Exists(change.LocalPath))
            return true; // Folder no longer exists — nothing to do

        var folderName = PlaceholderManager.DesanitizeName(Path.GetFileName(change.LocalPath));
        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var parentId = _engine.ResolveParentNodeId(parentDir);
        if (parentId == null)
        {
            Log.Info($"{_logPrefix} Outbox: parent not yet available for {folderName}, will retry");
            return false;
        }

        Log.Info($"{_logPrefix} Outbox: creating folder {folderName}");
        FileNode node;
        try
        {
            node = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.CreateFileNodeAsync(parentId, null, folderName, ct: ct), ct);
        }
        catch (Exception ex) when (ex.Message.Contains("alreadyExists"))
        {
            // Server already has an entry with this name under this parent. Look up
            // what it is: if it's a folder, adopt it (self-heal stale outbox). If it's
            // a file, we have a type-mismatch conflict that the client can't silently
            // resolve — mark the outbox entry rejected and surface to the user.
            Log.Info($"{_logPrefix} Outbox: name {folderName} already exists on server, resolving");
            var children = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.GetChildrenAsync(parentId, ct), ct);
            var existing = children.FirstOrDefault(c =>
                string.Equals(c.Name, folderName, StringComparison.Ordinal));
            if (existing == null)
            {
                Log.Error($"{_logPrefix} Outbox: server rejected create as alreadyExists but no matching child found for {folderName} under {parentId}");
                throw;
            }
            if (existing.BlobId != null)
            {
                // Server has a FILE where we want to create a FOLDER — genuine conflict.
                Log.Error($"{_logPrefix} Outbox: cannot create folder {folderName}: server has a file with the same name (node {existing.Id})");
                MarkRejectedAndNotInSync(change, $"Server already has a file named '{folderName}' in this folder");
                return true;
            }
            Log.Info($"{_logPrefix} Outbox: adopting existing folder {folderName} → node {existing.Id}");
            node = existing;
        }

        SyncEngine.EnsurePlaceholder(change.LocalPath, node.Id, isDirectory: true);
        _engine.UpdateMappings(change.LocalPath, null, node.Id);
        Log.Info($"{_logPrefix} Outbox: created folder {folderName} → node {node.Id}");
        return true;
    }

    private async Task<bool> ProcessUploadAsync(PendingChange change, CancellationToken ct)
    {
        if (change.LocalPath == null || !File.Exists(change.LocalPath))
        {
            Log.Info($"{_logPrefix} Outbox: skipping upload, file no longer exists: {change.LocalPath}");
            return true; // File gone — delete entry will handle server cleanup
        }

        var fileName = PlaceholderManager.DesanitizeName(Path.GetFileName(change.LocalPath));
        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var contentType = change.ContentType ?? "application/octet-stream";

        if (change.NodeId != null)
        {
            // Modified existing file
            var parentId = _engine.ResolveParentNodeId(parentDir);
            if (parentId == null)
            {
                Log.Info($"{_logPrefix} Outbox: parent not yet available for {fileName}, will retry");
                return false;
            }

            // Fetch current server state — used both to skip no-op uploads and to detect
            // whether the server copy diverged from the version this edit was based on.
            var existingNodes = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.GetFileNodesAsync([change.NodeId], ct), ct);
            var serverNode = existingNodes.Length > 0 ? existingNodes[0] : null;
            var serverBlobId = serverNode?.BlobId;

            // blobId is the content SHA1 (hex). Hash the local file once, for both the
            // no-op check and conflict detection.
            string localSha1Hex;
            using (var sha1Stream = OpenFileForUpload(change.LocalPath))
            {
                var hashBytes = await SHA1.HashDataAsync(sha1Stream, ct);
                localSha1Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // Content already matches the server — nothing to upload.
            if (serverBlobId != null && string.Equals(localSha1Hex, serverBlobId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"{_logPrefix} Outbox: content unchanged for {fileName} (digest:sha matches), skipping upload");
                if (change.IsDirtyLocation)
                {
                    await _queue.EnqueueAsync(QueuePriority.Background,
                        () => _jmapClient.MoveFileNodeAsync(change.NodeId, parentId, fileName, ct: ct), ct);
                }
                // File may have been replaced with a non-placeholder copy (e.g. local
                // "copy over existing") — convert back to placeholder before SetInSync.
                SyncEngine.EnsurePlaceholder(change.LocalPath, change.NodeId);
                SyncEngine.SetInSync(change.LocalPath);
                return true;
            }

            // Conflict: the server's content changed (to something other than our edit)
            // since the version this edit started from. Without this guard the in-place
            // update below would silently clobber the other device's change (DESIGN §6, #39).
            bool serverDiverged = serverBlobId != null
                && change.BaseBlobId != null
                && !string.Equals(serverBlobId, change.BaseBlobId, StringComparison.OrdinalIgnoreCase);

            Log.Info($"{_logPrefix} Outbox: uploading modified file {fileName}");
            var localCtime = File.GetCreationTimeUtc(change.LocalPath);
            var localMtime = File.GetLastWriteTimeUtc(change.LocalPath);
            string blobId;
            using (var fileStream = OpenFileForUpload(change.LocalPath))
            {
                blobId = await UploadFileContentAsync(change, fileStream, contentType, ct);
            }

            if (serverDiverged)
            {
                Log.Info($"{_logPrefix} Outbox: CONFLICT on {fileName} — server diverged from base (server={serverBlobId}, base={change.BaseBlobId}); strategy={ConflictStrategy}");
                await ResolveContentConflictAsync(change, parentId, fileName, contentType, blobId, localCtime, localMtime, serverNode!, ct);
                return true;
            }

            // No conflict — update content in place (v10 mutable blobId; node ID is stable).
            FileNode newNode;
            try
            {
                newNode = await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.ReplaceFileNodeBlobAsync(change.NodeId, parentId, fileName, blobId, contentType, localCtime, localMtime, ct: ct), ct);
            }
            catch (Exception ex) when (ex.Message.Contains("notFound"))
            {
                // Server delete + local edit: the local file becomes a new create (DESIGN §6).
                // The blob is already uploaded; onExists:"rename" so we never clobber a
                // same-named node another device created meanwhile. The engine's deferred
                // destroy for the old node then finds its mapping gone and does nothing.
                Log.Info($"{_logPrefix} Outbox: node {change.NodeId} gone from server under local edit; re-creating {fileName} as a new node");
                newNode = await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.CreateFileNodeAsync(parentId, blobId, fileName, contentType, "rename", localCtime, localMtime, ct), ct);
            }

            try
            {
                // Edited while we were down? The write stripped the reparse point, so the
                // file may be a plain file now — convert it back (or just update identity).
                SyncEngine.EnsurePlaceholder(change.LocalPath, newNode.Id);
                SyncEngine.StripZoneIdentifier(change.LocalPath);
                SyncEngine.SetInSync(change.LocalPath);
            }
            catch (Exception ex)
            {
                Log.Info($"{_logPrefix} Outbox: placeholder update deferred for {fileName}: {ex.Message}");
            }
            _engine.RecordRecentUpload(change.LocalPath);
            _engine.UpdateMappings(change.LocalPath, change.NodeId, newNode.Id, newNode.BlobId);

            // Also process move if location is dirty
            if (change.IsDirtyLocation)
            {
                var newParentId = _engine.ResolveParentNodeId(parentDir);
                if (newParentId != null)
                {
                    await _queue.EnqueueAsync(QueuePriority.Background,
                        () => _jmapClient.MoveFileNodeAsync(newNode.Id, newParentId, fileName, ct: ct), ct);
                }
            }
            Log.Info($"{_logPrefix} Outbox: updated {fileName} → node {newNode.Id}");
        }
        else
        {
            // New file — check if this is a Recycle Bin restore
            if (_recentlyTrashed.TryRemove(change.LocalPath, out var trashedInfo))
            {
                _trashedPathByNodeId.TryRemove(trashedInfo.NodeId, out _);
                Log.Info($"{_logPrefix} Outbox: detected restore from Recycle Bin for {fileName} (node {trashedInfo.NodeId})");

                var restoreParentId = _engine.ResolveParentNodeId(parentDir);
                if (restoreParentId == null)
                {
                    Log.Info($"{_logPrefix} Outbox: parent not yet available for {fileName}, will retry");
                    // Put the trashed info back so retry finds it
                    _recentlyTrashed[change.LocalPath] = trashedInfo;
                    _trashedPathByNodeId[trashedInfo.NodeId] = change.LocalPath;
                    return false;
                }

                // Step 1: Quick undo — cancel pending delete if it hasn't started processing
                if (_outbox.TryCancelDelete(trashedInfo.NodeId))
                {
                    Log.Info($"{_logPrefix} Outbox: cancelled pending delete for {trashedInfo.NodeId}, restoring mappings");
                    SyncEngine.EnsurePlaceholder(change.LocalPath, trashedInfo.NodeId);
                    _engine.UpdateMappings(change.LocalPath, null, trashedInfo.NodeId);
                    SyncEngine.SetInSync(change.LocalPath);
                    return true;
                }

                // Step 2: Move back from server trash
                try
                {
                    await _queue.EnqueueAsync(QueuePriority.Background,
                        () => _jmapClient.MoveFileNodeAsync(trashedInfo.NodeId, restoreParentId, fileName, ct: ct), ct);
                    Log.Info($"{_logPrefix} Outbox: restored {fileName} from server trash (node {trashedInfo.NodeId})");
                    SyncEngine.EnsurePlaceholder(change.LocalPath, trashedInfo.NodeId);
                    _engine.UpdateMappings(change.LocalPath, null, trashedInfo.NodeId);
                    SyncEngine.SetInSync(change.LocalPath);
                    return true;
                }
                catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
                {
                    Log.Info($"{_logPrefix} Outbox: node {trashedInfo.NodeId} not found in trash, trying blobId create");
                }

                // Step 3: Create with existing blobId (node destroyed but blob may survive)
                if (trashedInfo.BlobId != null)
                {
                    try
                    {
                        var restoreCtime = File.GetCreationTimeUtc(change.LocalPath);
                        var restoreMtime = File.GetLastWriteTimeUtc(change.LocalPath);
                        var restoredNode = await _queue.EnqueueAsync(QueuePriority.Background,
                            () => _jmapClient.CreateFileNodeAsync(restoreParentId, trashedInfo.BlobId, fileName, contentType, "replace", restoreCtime, restoreMtime, ct), ct);
                        Log.Info($"{_logPrefix} Outbox: recreated {fileName} with existing blobId → node {restoredNode.Id}");
                        SyncEngine.EnsurePlaceholder(change.LocalPath, restoredNode.Id);
                        _engine.UpdateMappings(change.LocalPath, null, restoredNode.Id);
                        _engine.RecordRecentUpload(change.LocalPath);
                        SyncEngine.SetInSync(change.LocalPath);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"{_logPrefix} Outbox: blobId create failed ({ex.Message}), falling back to upload");
                    }
                }

                // Step 4: Fall through to normal upload
                Log.Info($"{_logPrefix} Outbox: falling back to full upload for {fileName}");
            }

            // New file — normal upload path
            var parentId = _engine.ResolveParentNodeId(parentDir);
            if (parentId == null)
            {
                Log.Info($"{_logPrefix} Outbox: parent not yet available for {fileName}, will retry");
                return false;
            }

            Log.Info($"{_logPrefix} Outbox: uploading new file {fileName}");
            var localCtime = File.GetCreationTimeUtc(change.LocalPath);
            var localMtime = File.GetLastWriteTimeUtc(change.LocalPath);
            using var fileStream = OpenFileForUpload(change.LocalPath);
            var blobId = await UploadFileContentAsync(change, fileStream, contentType, ct);
            FileNode node;
            try
            {
                node = await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.CreateFileNodeAsync(parentId, blobId, fileName, contentType, "replace", localCtime, localMtime, ct), ct);
            }
            catch (Exception ex) when (ex.Message.Contains("alreadyExists"))
            {
                // onExists:"replace" should handle file-over-file, so this typically means
                // server has a FOLDER with the same name — a type-mismatch conflict.
                var children = await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.GetChildrenAsync(parentId, ct), ct);
                var existing = children.FirstOrDefault(c =>
                    string.Equals(c.Name, fileName, StringComparison.Ordinal));
                if (existing != null && existing.BlobId == null)
                {
                    Log.Error($"{_logPrefix} Outbox: cannot upload file {fileName}: server has a folder with the same name (node {existing.Id})");
                    MarkRejectedAndNotInSync(change, $"Server already has a folder named '{fileName}' in this folder");
                    return true;
                }
                throw;
            }

            Log.Info($"{_logPrefix} Outbox: EnsurePlaceholder {change.LocalPath} nodeId={node.Id}");
            try
            {
                SyncEngine.EnsurePlaceholder(change.LocalPath, node.Id);
                SyncEngine.StripZoneIdentifier(change.LocalPath);
                SyncEngine.SetInSync(change.LocalPath);
            }
            catch (Exception ex)
            {
                // Non-fatal: file uploaded successfully but cfapi placeholder
                // conversion failed (e.g. sync root was re-registered after file
                // was copied). Next sync cycle will adopt it.
                Log.Info($"{_logPrefix} Outbox: placeholder conversion deferred for {fileName}: {ex.Message}");
            }
            _engine.UpdateMappings(change.LocalPath, null, node.Id);
            _engine.RecordRecentUpload(change.LocalPath);
            Log.Info($"{_logPrefix} Outbox: created {fileName} → node {node.Id}");
        }

        return true;
    }

    /// <summary>
    /// Resolve a content conflict — the server copy diverged from the version this edit was
    /// based on. The user's edited content has already been uploaded as <paramref name="blobId"/>.
    /// With <see cref="ConflictResolution.NewestWins"/> we first try an atomic onExists:"newest"
    /// update; if the server copy is actually newer (alreadyExists) we fall back to a conflict
    /// copy. The conflict copy keeps both versions: the local edit is moved to a server-chosen
    /// non-colliding name, and the original file is re-materialized with the server's content
    /// (DESIGN §6). Never loses either edit.
    /// </summary>
    private async Task ResolveContentConflictAsync(
        PendingChange change, string parentId, string fileName, string contentType,
        string blobId, DateTime localCtime, DateTime localMtime, FileNode serverNode,
        CancellationToken ct)
    {
        var localPath = change.LocalPath!;
        var nodeId = change.NodeId!;
        var parentDir = Path.GetDirectoryName(localPath)!;

        // Newest-wins: atomic conditional overwrite. The server applies the update only if our
        // modified time is newer; otherwise it returns alreadyExists and we make a conflict copy.
        if (ConflictStrategy == ConflictResolution.NewestWins)
        {
            try
            {
                var winner = await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.ReplaceFileNodeBlobAsync(nodeId, parentId, fileName, blobId, contentType, localCtime, localMtime, onExists: "newest", ct: ct), ct);
                Log.Info($"{_logPrefix} Outbox: conflict resolved (newest-wins, local newer) for {fileName} → node {winner.Id}");
                try
                {
                    SyncEngine.EnsurePlaceholder(localPath, winner.Id);
                    SyncEngine.StripZoneIdentifier(localPath);
                    SyncEngine.SetInSync(localPath);
                }
                catch (Exception ex) { Log.Info($"{_logPrefix} Outbox: placeholder update deferred for {fileName}: {ex.Message}"); }
                _engine.RecordRecentUpload(localPath);
                _engine.UpdateMappings(localPath, nodeId, winner.Id, winner.BlobId);
                return;
            }
            catch (Exception ex) when (ex.Message.Contains("alreadyExists"))
            {
                Log.Info($"{_logPrefix} Outbox: newest-wins declined (server copy newer) for {fileName}, making conflict copy");
            }
        }

        // Conflict copy: create a new node holding the user's content; the server assigns a
        // non-colliding name (e.g. "doc (2).txt") and returns it.
        var conflictNode = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.CreateFileNodeAsync(parentId, blobId, fileName, contentType, "rename", localCtime, localMtime, ct), ct);
        if (string.IsNullOrEmpty(conflictNode.Name))
        {
            // Server didn't echo the chosen name — can't safely rename the local file.
            // The edit is preserved on the server as conflictNode; the next sync will
            // materialize it locally. Leave the original file untouched.
            Log.Error($"{_logPrefix} Outbox: conflict-copy create returned no name for {fileName} (node {conflictNode.Id}); next sync will reconcile");
            return;
        }
        var conflictPath = Path.Combine(parentDir, PlaceholderManager.SanitizeName(conflictNode.Name));
        Log.Info($"{_logPrefix} Outbox: conflict copy for {fileName} → '{conflictNode.Name}' (node {conflictNode.Id})");

        try
        {
            // Re-point the local placeholder (which holds the user's edit) at the conflict node
            // and pre-map the target BEFORE moving, so the blocking NOTIFY_RENAME callback sees
            // this as our own echo (mapped node → target path) and does not issue a server move.
            SyncEngine.EnsurePlaceholder(localPath, conflictNode.Id);
            _engine.UpdateMappings(conflictPath, null, conflictNode.Id, conflictNode.BlobId);
            File.Move(localPath, conflictPath);
            _engine.RecordRecentUpload(conflictPath);
            SyncEngine.StripZoneIdentifier(conflictPath);
            SyncEngine.SetInSync(conflictPath);

            // Re-create the original name with the server's (other device's) content as a
            // fresh dehydrated placeholder pointing at the original node.
            _engine.MaterializeServerNode(parentDir, serverNode);
        }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} Outbox: failed to finalise conflict copy for {fileName}: {ex.Message}. Both versions exist on the server; next sync will reconcile.");
        }
    }

    private const int StallTimeoutSeconds = 30;

    private async Task<string> UploadFileContentAsync(
        PendingChange change, FileStream fileStream, string contentType, CancellationToken ct)
    {
        var fileLength = fileStream.Length;
        var chunkSize = _jmapClient.ChunkSize;
        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Stall timer: cancel if no progress for StallTimeoutSeconds.
        // Starts unarmed (Infinite) so queue wait time doesn't count.
        // Armed on first byte read, reset on every progress callback.
        var stallTimeout = TimeSpan.FromSeconds(StallTimeoutSeconds);
        using var stallTimer = new Timer(_ =>
        {
            Log.Info($"{_logPrefix} Outbox: upload stalled for {Path.GetFileName(change.LocalPath)}, cancelling");
            try { uploadCts.Cancel(); } catch { }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        void ResetStall() { try { stallTimer.Change(stallTimeout, Timeout.InfiniteTimeSpan); } catch { } }

        void OnProgress(long bytesUploaded)
        {
            _outbox.UpdateProgress(change.Id, bytesUploaded);
            ResetStall();
        }

        // Use chunked upload whenever blob2 capability is available. Blob/set
        // with data array (combining blobId references) is part of the blob2
        // capability (https://www.fastmail.com/dev/blob2).
        if (_jmapClient.HasBlob2)
        {
            // Convert persisted chunks to JmapClient format for resume
            List<JmapClient.UploadedChunkInfo>? previousChunks = null;
            if (change.UploadedChunks?.Count > 0)
            {
                previousChunks = change.UploadedChunks
                    .Select(c => new JmapClient.UploadedChunkInfo(c.BlobId, c.Sha1Base64, c.Offset, c.Length))
                    .ToList();
                Log.Info($"{_logPrefix} Outbox: resuming upload of {Path.GetFileName(change.LocalPath)} with {previousChunks.Count} cached chunks");
            }

            void OnChunkUploaded(JmapClient.UploadedChunkInfo chunk)
            {
                _outbox.AddUploadedChunk(change.Id, new UploadedChunk
                {
                    BlobId = chunk.BlobId,
                    Sha1Base64 = chunk.Sha1Base64,
                    Offset = chunk.Offset,
                    Length = chunk.Length,
                });
                ResetStall();
            }

            var blobId = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.UploadBlobChunkedAsync(
                    fileStream, contentType, fileLength,
                    OnProgress, OnChunkUploaded, previousChunks,
                    uploadCts.Token), ct);
            // Upload complete — clear persisted chunks
            _outbox.ClearUploadedChunks(change.Id);
            return blobId;
        }

        using var stream = new ProgressStream(fileStream, fileLength, OnProgress, ResetStall);
        return await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.UploadBlobAsync(stream, contentType, uploadCts.Token), ct);
    }

    private async Task<bool> ProcessMoveAsync(PendingChange change, CancellationToken ct)
    {
        if (change.NodeId == null || change.LocalPath == null)
            return true;

        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var parentId = _engine.ResolveParentNodeId(parentDir);
        if (parentId == null)
        {
            Log.Info($"{_logPrefix} Outbox: parent not yet available for {Path.GetFileName(change.LocalPath)}, will retry");
            return false;
        }

        var newName = PlaceholderManager.DesanitizeName(Path.GetFileName(change.LocalPath));
        Log.Info($"{_logPrefix} Outbox: moving node {change.NodeId} → {parentId}/{newName}");

        try
        {
            await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.MoveFileNodeAsync(change.NodeId, parentId, newName, ct: ct), ct);
            SyncEngine.SetInSync(change.LocalPath);
        }
        catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
        {
            // Node no longer exists on server — treat as success
            Log.Info($"{_logPrefix} Outbox: node {change.NodeId} not found on server during move");
        }

        return true;
    }

    /// <summary>
    /// Open a file for reading using CfOpenFileWithOplock when available.
    /// Falls back to a regular FileStream if the oplock open fails (e.g. file
    /// is open for writing by another process, or not a placeholder).
    /// The caller must dispose the returned stream.
    /// </summary>
    private unsafe FileStream OpenFileForUpload(string path)
    {
        if (CfApiCapabilities.HasBlockSelfHydration)
        {
            SafeHandle? protectedHandle = null;
            try
            {
                var hr = PInvoke.CfOpenFileWithOplock(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE, out var opened);
                protectedHandle = opened;
                hr.ThrowOnFailure();

                // The oplock handle is a cfapi *protected* handle, not a Win32 file handle:
                // FileStream rejects it ("The handle is invalid"). Ask cfapi for the Win32
                // handle it wraps (owned by the protected handle — not closed separately).
                var win32 = PInvoke.CfGetWin32HandleFromProtectedHandle(
                    new global::Windows.Win32.Foundation.HANDLE(protectedHandle.DangerousGetHandle()));
                var safeHandle = new SafeFileHandle((IntPtr)win32.Value, ownsHandle: false);
                return new CfOplockFileStream(safeHandle, protectedHandle);
            }
            catch (Exception ex)
            {
                // Release the oplock: a leaked protected handle keeps the file open (and
                // undeletable) until the process exits — one per upload.
                protectedHandle?.Dispose();
                Log.Info($"{_logPrefix} CfOpenFileWithOplock failed for {Path.GetFileName(path)}, using FileStream: {ex.Message}");
            }
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>
    /// FileStream wrapper that also disposes the CfCloseHandleSafeHandle from CfOpenFileWithOplock.
    /// </summary>
    private sealed class CfOplockFileStream : FileStream
    {
        private readonly SafeHandle _cfHandle;

        public CfOplockFileStream(SafeFileHandle fileHandle, SafeHandle cfHandle)
            : base(fileHandle, FileAccess.Read)
        {
            _cfHandle = cfHandle;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                _cfHandle.Dispose();
        }
    }

    public void Dispose()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            try { _loopTask?.Wait(3000); }
            catch { /* shutdown */ }

            // Wait for in-flight workers to observe cancellation
            Task[] workers;
            lock (_workerLock)
                workers = _workerTasks.ToArray();
            try { Task.WaitAll(workers, 5000); }
            catch { /* shutdown */ }

            _cts.Dispose();
        }
        _workerSlots.Dispose();
    }
}
