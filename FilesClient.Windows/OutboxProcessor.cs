using System.Collections.Concurrent;
using FilesClient.Jmap;

namespace FilesClient.Windows;

public class OutboxProcessor : IDisposable
{
    private const int MaxConcurrency = 4;
    private const int MaxAttempts = 10;

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

    public OutboxProcessor(SyncOutbox outbox, SyncEngine engine, IJmapClient jmapClient, JmapQueue queue)
    {
        _outbox = outbox;
        _engine = engine;
        _jmapClient = jmapClient;
        _queue = queue;
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
                Console.Error.WriteLine($"Outbox dispatch error: {ex.Message}");
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
            Console.Error.WriteLine($"Outbox timeout for {change.LocalPath ?? change.NodeId}");
            _outbox.MarkFailed(change.Id, "Operation timed out");
        }
        catch (OperationCanceledException)
        {
            // App shutdown — remove from processing so state is clean
            _outbox.MarkFailed(change.Id, "Cancelled");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("copying content to a stream"))
        {
            // File was still being written when we opened the stream — retry silently
            Console.WriteLine($"Outbox: file still being written for {Path.GetFileName(change.LocalPath)}, will retry");
            _outbox.MarkRetry(change.Id);
        }
        catch (IOException ex) when (change.IsDirtyContent)
        {
            // File locked or still being copied — retry silently
            Console.WriteLine($"Outbox: file not ready for {Path.GetFileName(change.LocalPath)}, will retry ({ex.Message})");
            _outbox.MarkRetry(change.Id);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && (ex.Message.Contains("forbidden") || ex.Message.Contains("Forbidden")))
        {
            Console.Error.WriteLine($"Outbox: permission denied for {change.LocalPath ?? change.NodeId}: {ex.Message}");
            _outbox.MarkRejected(change.Id, ex.Message);

            // Clean up local file that can't be synced (only new untracked files)
            if (change.LocalPath != null && change.NodeId == null)
            {
                try
                {
                    if (change.IsFolder && Directory.Exists(change.LocalPath))
                        Directory.Delete(change.LocalPath, recursive: true);
                    else if (File.Exists(change.LocalPath))
                        File.Delete(change.LocalPath);
                }
                catch { }
            }
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            // Shutdown — resource already disposed, nothing to do
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Outbox process error for {change.LocalPath ?? change.NodeId}: {ex.Message}");
            if (change.AttemptCount + 1 >= MaxAttempts)
            {
                Console.Error.WriteLine($"Outbox: giving up after {change.AttemptCount + 1} attempts: {change.LocalPath ?? change.NodeId}");
                _outbox.MarkRejected(change.Id, $"Gave up after {change.AttemptCount + 1} attempts: {ex.Message}");
            }
            else
            {
                _outbox.MarkFailed(change.Id, ex.Message);
            }
        }
        finally
        {
            try { _workerSlots.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task<bool> ProcessChangeAsync(PendingChange change, CancellationToken ct)
    {
        if (change.IsDeleted)
        {
            await ProcessDeleteAsync(change, ct);
            return true;
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
                Console.WriteLine($"Outbox: trashing node {change.NodeId}");
                await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.MoveFileNodeAsync(change.NodeId, _trashNodeId, name, "rename", ct), ct);
            }
            else
            {
                Console.WriteLine($"Outbox: destroying node {change.NodeId}");
                await _queue.EnqueueAsync(QueuePriority.Background,
                    () => _jmapClient.DestroyFileNodeAsync(change.NodeId, ct), ct);
            }
        }
        catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
        {
            Console.WriteLine($"Outbox: node {change.NodeId} already gone on server");
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
            Console.WriteLine($"Outbox: parent not yet available for {folderName}, will retry");
            return false;
        }

        Console.WriteLine($"Outbox: creating folder {folderName}");
        var node = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.CreateFileNodeAsync(parentId, null, folderName, null, null, ct), ct);

        SyncEngine.ConvertToPlaceholder(change.LocalPath, node.Id, isDirectory: true);
        _engine.UpdateMappings(change.LocalPath, null, node.Id);
        Console.WriteLine($"Outbox: created folder {folderName} → node {node.Id}");
        return true;
    }

    private async Task<bool> ProcessUploadAsync(PendingChange change, CancellationToken ct)
    {
        if (change.LocalPath == null || !File.Exists(change.LocalPath))
        {
            Console.WriteLine($"Outbox: skipping upload, file no longer exists: {change.LocalPath}");
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
                Console.WriteLine($"Outbox: parent not yet available for {fileName}, will retry");
                return false;
            }

            Console.WriteLine($"Outbox: uploading modified file {fileName}");
            using var fileStream = new FileStream(change.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var stream = new ProgressStream(fileStream, fileStream.Length,
                percent => _outbox.UpdateProgress(change.Id, percent));
            // Size-based timeout: 120s base + 1s per 25KB (~25KB/s minimum throughput)
            // Activity-based timeouts don't work because HTTP/2 buffers all reads upfront
            var timeout1 = (int)(fileStream.Length / 25_000) + 120;
            using var uploadCts1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            uploadCts1.CancelAfter(TimeSpan.FromSeconds(timeout1));
            var blobId = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.UploadBlobAsync(stream, contentType, uploadCts1.Token), ct);
            var newNode = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.ReplaceFileNodeBlobAsync(change.NodeId, parentId, fileName, blobId, contentType, ct), ct);

            SyncEngine.UpdatePlaceholderIdentity(change.LocalPath, newNode.Id);
            _engine.RecordRecentUpload(change.LocalPath);
            _engine.UpdateMappings(change.LocalPath, change.NodeId, newNode.Id);

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
            Console.WriteLine($"Outbox: updated {fileName} → node {newNode.Id}");
        }
        else
        {
            // New file — check if this is a Recycle Bin restore
            if (_recentlyTrashed.TryRemove(change.LocalPath, out var trashedInfo))
            {
                _trashedPathByNodeId.TryRemove(trashedInfo.NodeId, out _);
                Console.WriteLine($"Outbox: detected restore from Recycle Bin for {fileName} (node {trashedInfo.NodeId})");

                var restoreParentId = _engine.ResolveParentNodeId(parentDir);
                if (restoreParentId == null)
                {
                    Console.WriteLine($"Outbox: parent not yet available for {fileName}, will retry");
                    // Put the trashed info back so retry finds it
                    _recentlyTrashed[change.LocalPath] = trashedInfo;
                    _trashedPathByNodeId[trashedInfo.NodeId] = change.LocalPath;
                    return false;
                }

                // Step 1: Quick undo — cancel pending delete if it hasn't started processing
                if (_outbox.TryCancelDelete(trashedInfo.NodeId))
                {
                    Console.WriteLine($"Outbox: cancelled pending delete for {trashedInfo.NodeId}, restoring mappings");
                    SyncEngine.ConvertToPlaceholder(change.LocalPath, trashedInfo.NodeId);
                    _engine.UpdateMappings(change.LocalPath, null, trashedInfo.NodeId);
                    SyncEngine.SetInSync(change.LocalPath);
                    return true;
                }

                // Step 2: Move back from server trash
                try
                {
                    await _queue.EnqueueAsync(QueuePriority.Background,
                        () => _jmapClient.MoveFileNodeAsync(trashedInfo.NodeId, restoreParentId, fileName, ct: ct), ct);
                    Console.WriteLine($"Outbox: restored {fileName} from server trash (node {trashedInfo.NodeId})");
                    SyncEngine.ConvertToPlaceholder(change.LocalPath, trashedInfo.NodeId);
                    _engine.UpdateMappings(change.LocalPath, null, trashedInfo.NodeId);
                    SyncEngine.SetInSync(change.LocalPath);
                    return true;
                }
                catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
                {
                    Console.WriteLine($"Outbox: node {trashedInfo.NodeId} not found in trash, trying blobId create");
                }

                // Step 3: Create with existing blobId (node destroyed but blob may survive)
                if (trashedInfo.BlobId != null)
                {
                    try
                    {
                        var restoredNode = await _queue.EnqueueAsync(QueuePriority.Background,
                            () => _jmapClient.CreateFileNodeAsync(restoreParentId, trashedInfo.BlobId, fileName, contentType, "replace", ct), ct);
                        Console.WriteLine($"Outbox: recreated {fileName} with existing blobId → node {restoredNode.Id}");
                        SyncEngine.ConvertToPlaceholder(change.LocalPath, restoredNode.Id);
                        _engine.UpdateMappings(change.LocalPath, null, restoredNode.Id);
                        _engine.RecordRecentUpload(change.LocalPath);
                        SyncEngine.SetInSync(change.LocalPath);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Outbox: blobId create failed ({ex.Message}), falling back to upload");
                    }
                }

                // Step 4: Fall through to normal upload
                Console.WriteLine($"Outbox: falling back to full upload for {fileName}");
            }

            // New file — normal upload path
            var parentId = _engine.ResolveParentNodeId(parentDir);
            if (parentId == null)
            {
                Console.WriteLine($"Outbox: parent not yet available for {fileName}, will retry");
                return false;
            }

            Console.WriteLine($"Outbox: uploading new file {fileName}");
            using var fileStream = new FileStream(change.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var stream = new ProgressStream(fileStream, fileStream.Length,
                percent => _outbox.UpdateProgress(change.Id, percent));
            var timeout2 = (int)(fileStream.Length / 25_000) + 120;
            using var uploadCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            uploadCts2.CancelAfter(TimeSpan.FromSeconds(timeout2));
            var blobId = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.UploadBlobAsync(stream, contentType, uploadCts2.Token), ct);
            var node = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.CreateFileNodeAsync(parentId, blobId, fileName, contentType, "replace", ct), ct);

            SyncEngine.ConvertToPlaceholder(change.LocalPath, node.Id);
            _engine.UpdateMappings(change.LocalPath, null, node.Id);
            _engine.RecordRecentUpload(change.LocalPath);
            SyncEngine.StripZoneIdentifier(change.LocalPath);
            SyncEngine.SetInSync(change.LocalPath);
            Console.WriteLine($"Outbox: created {fileName} → node {node.Id}");
        }

        return true;
    }

    private async Task<bool> ProcessMoveAsync(PendingChange change, CancellationToken ct)
    {
        if (change.NodeId == null || change.LocalPath == null)
            return true;

        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var parentId = _engine.ResolveParentNodeId(parentDir);
        if (parentId == null)
        {
            Console.WriteLine($"Outbox: parent not yet available for {Path.GetFileName(change.LocalPath)}, will retry");
            return false;
        }

        var newName = Path.GetFileName(change.LocalPath);
        Console.WriteLine($"Outbox: moving node {change.NodeId} → {parentId}/{newName}");

        try
        {
            await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.MoveFileNodeAsync(change.NodeId, parentId, newName, ct: ct), ct);
            SyncEngine.SetInSync(change.LocalPath);
        }
        catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
        {
            // Node no longer exists on server — treat as success
            Console.WriteLine($"Outbox: node {change.NodeId} not found on server during move");
        }

        return true;
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
