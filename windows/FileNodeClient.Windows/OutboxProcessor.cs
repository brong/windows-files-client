using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using FileNodeClient.Logging;
using FileNodeClient.Jmap;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FileNodeClient.Windows;

public class OutboxProcessor : IDisposable
{
    private const int MaxConcurrency = 4;


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
                _outbox.MarkCompleted(change.Id);
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

        var name = change.LocalPath != null ? Path.GetFileName(change.LocalPath) : change.NodeId;

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
                    () => _jmapClient.MoveFileNodeAsync(change.NodeId, _trashNodeId, name, "rename", ct), ct);
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

        var folderName = Path.GetFileName(change.LocalPath);
        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var parentId = _engine.ResolveParentNodeId(parentDir);
        if (parentId == null)
        {
            Log.Info($"{_logPrefix} Outbox: parent not yet available for {folderName}, will retry");
            return false;
        }

        Log.Info($"{_logPrefix} Outbox: creating folder {folderName}");
        var node = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.CreateFileNodeAsync(parentId, null, folderName, ct: ct), ct);

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

        var fileName = Path.GetFileName(change.LocalPath);
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

            // Check if content actually changed by comparing local SHA1 with server blobId
            var existingNodes = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.GetFileNodesAsync([change.NodeId], ct), ct);
            if (existingNodes.Length > 0 && existingNodes[0].BlobId != null)
            {
                using var sha1Stream = OpenFileForUpload(change.LocalPath);
                var hashBytes = await SHA1.HashDataAsync(sha1Stream, ct);
                var localSha1Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
                if (string.Equals(localSha1Hex, existingNodes[0].BlobId, StringComparison.OrdinalIgnoreCase))
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
            }

            Log.Info($"{_logPrefix} Outbox: uploading modified file {fileName}");
            var localCtime = File.GetCreationTimeUtc(change.LocalPath);
            var localMtime = File.GetLastWriteTimeUtc(change.LocalPath);
            using var fileStream = OpenFileForUpload(change.LocalPath);
            var blobId = await UploadFileContentAsync(change, fileStream, contentType, ct);
            var newNode = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.ReplaceFileNodeBlobAsync(change.NodeId, parentId, fileName, blobId, contentType, localCtime, localMtime, ct), ct);

            SyncEngine.UpdatePlaceholderIdentity(change.LocalPath, newNode.Id);
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

            SyncEngine.StripZoneIdentifier(change.LocalPath);
            SyncEngine.SetInSync(change.LocalPath);
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
            var node = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.CreateFileNodeAsync(parentId, blobId, fileName, contentType, "replace", localCtime, localMtime, ct), ct);

            Log.Info($"{_logPrefix} Outbox: EnsurePlaceholder {change.LocalPath} nodeId={node.Id}");
            SyncEngine.EnsurePlaceholder(change.LocalPath, node.Id);
            _engine.UpdateMappings(change.LocalPath, null, node.Id);
            _engine.RecordRecentUpload(change.LocalPath);
            SyncEngine.StripZoneIdentifier(change.LocalPath);
            Log.Info($"{_logPrefix} Outbox: SetInSync {change.LocalPath}");
            SyncEngine.SetInSync(change.LocalPath);
            Log.Info($"{_logPrefix} Outbox: created {fileName} → node {node.Id}");
        }

        return true;
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

        if (chunkSize.HasValue && fileLength > chunkSize.Value)
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

        var newName = Path.GetFileName(change.LocalPath);
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
    private FileStream OpenFileForUpload(string path)
    {
        if (CfApiCapabilities.HasBlockSelfHydration)
        {
            try
            {
                PInvoke.CfOpenFileWithOplock(
                    path,
                    CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE,
                    out var handle).ThrowOnFailure();

                // CsWin32 returns CfCloseHandleSafeHandle — wrap in FileStream for reading.
                // Transfer ownership: create a non-owning SafeFileHandle so FileStream
                // disposes it, then dispose the CfCloseHandleSafeHandle separately.
                var safeHandle = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
                return new CfOplockFileStream(safeHandle, handle);
            }
            catch (Exception ex)
            {
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
