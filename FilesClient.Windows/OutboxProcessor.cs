using FilesClient.Jmap;

namespace FilesClient.Windows;

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
    private readonly SemaphoreSlim _workerSlots = new(MaxConcurrency, MaxConcurrency);

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
                // Fire-and-forget — the worker releases its slot when done
                _ = ProcessWorkerAsync(change, ct);
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
            await ProcessChangeAsync(change, ct);
            _outbox.MarkCompleted(change.Id);
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Outbox process error for {change.LocalPath ?? change.NodeId}: {ex.Message}");
            _outbox.MarkFailed(change.Id, ex.Message);
        }
        finally
        {
            _workerSlots.Release();
        }
    }

    private async Task ProcessChangeAsync(PendingChange change, CancellationToken ct)
    {
        if (change.IsDeleted)
        {
            await ProcessDeleteAsync(change, ct);
            return;
        }

        if (change.IsFolder && change.NodeId == null)
        {
            await ProcessFolderCreateAsync(change, ct);
            return;
        }

        if (change.IsDirtyContent)
        {
            await ProcessUploadAsync(change, ct);
            return;
        }

        if (change.IsDirtyLocation)
        {
            await ProcessMoveAsync(change, ct);
            return;
        }
    }

    private async Task ProcessDeleteAsync(PendingChange change, CancellationToken ct)
    {
        if (change.NodeId == null)
            return; // Nothing to delete on server

        try
        {
            Console.WriteLine($"Outbox: destroying node {change.NodeId}");
            await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.DestroyFileNodeAsync(change.NodeId, ct), ct);
        }
        catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
        {
            // Already deleted on server — treat as success
            Console.WriteLine($"Outbox: node {change.NodeId} already deleted on server");
        }
    }

    private async Task ProcessFolderCreateAsync(PendingChange change, CancellationToken ct)
    {
        if (change.LocalPath == null || !Directory.Exists(change.LocalPath))
            return; // Folder no longer exists

        var folderName = Path.GetFileName(change.LocalPath);
        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var parentId = _engine.ResolveParentNodeId(parentDir);
        if (parentId == null)
        {
            throw new InvalidOperationException($"Cannot resolve parent for {change.LocalPath}");
        }

        Console.WriteLine($"Outbox: creating folder {folderName}");
        var node = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.CreateFileNodeAsync(parentId, null, folderName, null, ct), ct);

        SyncEngine.ConvertToPlaceholder(change.LocalPath, node.Id, isDirectory: true);
        _engine.UpdateMappings(change.LocalPath, null, node.Id);
        Console.WriteLine($"Outbox: created folder {folderName} → node {node.Id}");
    }

    private async Task ProcessUploadAsync(PendingChange change, CancellationToken ct)
    {
        if (change.LocalPath == null || !File.Exists(change.LocalPath))
        {
            Console.WriteLine($"Outbox: skipping upload, file no longer exists: {change.LocalPath}");
            return; // File gone — delete entry will handle server cleanup
        }

        var fileName = Path.GetFileName(change.LocalPath);
        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var contentType = change.ContentType ?? "application/octet-stream";

        if (change.NodeId != null)
        {
            // Modified existing file
            var parentId = _engine.ResolveParentNodeId(parentDir);
            if (parentId == null)
                throw new InvalidOperationException($"Cannot resolve parent for {change.LocalPath}");

            Console.WriteLine($"Outbox: uploading modified file {fileName}");
            using var fileStream = new FileStream(change.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var stream = new ProgressStream(fileStream, fileStream.Length,
                percent => _outbox.UpdateProgress(change.Id, percent));
            // Size-based timeout: 60s base + 1s per 50KB (~50KB/s minimum throughput)
            // Activity-based timeouts don't work because HTTP/2 buffers all reads upfront
            var timeout1 = Math.Max(60, (int)(fileStream.Length / 50_000) + 60);
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
                        () => _jmapClient.MoveFileNodeAsync(newNode.Id, newParentId, fileName, ct), ct);
                }
            }

            SyncEngine.StripZoneIdentifier(change.LocalPath);
            SyncEngine.SetInSync(change.LocalPath);
            Console.WriteLine($"Outbox: updated {fileName} → node {newNode.Id}");
        }
        else
        {
            // New file
            var parentId = _engine.ResolveParentNodeId(parentDir);
            if (parentId == null)
                throw new InvalidOperationException($"Cannot resolve parent for {change.LocalPath}");

            Console.WriteLine($"Outbox: uploading new file {fileName}");
            using var fileStream = new FileStream(change.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var stream = new ProgressStream(fileStream, fileStream.Length,
                percent => _outbox.UpdateProgress(change.Id, percent));
            var timeout2 = Math.Max(60, (int)(fileStream.Length / 50_000) + 60);
            using var uploadCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            uploadCts2.CancelAfter(TimeSpan.FromSeconds(timeout2));
            var blobId = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.UploadBlobAsync(stream, contentType, uploadCts2.Token), ct);
            var node = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.CreateFileNodeAsync(parentId, blobId, fileName, contentType, ct), ct);

            SyncEngine.ConvertToPlaceholder(change.LocalPath, node.Id);
            _engine.UpdateMappings(change.LocalPath, null, node.Id);
            _engine.RecordRecentUpload(change.LocalPath);
            SyncEngine.StripZoneIdentifier(change.LocalPath);
            SyncEngine.SetInSync(change.LocalPath);
            Console.WriteLine($"Outbox: created {fileName} → node {node.Id}");
        }
    }

    private async Task ProcessMoveAsync(PendingChange change, CancellationToken ct)
    {
        if (change.NodeId == null || change.LocalPath == null)
            return;

        var parentDir = Path.GetDirectoryName(change.LocalPath)!;
        var parentId = _engine.ResolveParentNodeId(parentDir);
        if (parentId == null)
            throw new InvalidOperationException($"Cannot resolve parent for {change.LocalPath}");

        var newName = Path.GetFileName(change.LocalPath);
        Console.WriteLine($"Outbox: moving node {change.NodeId} → {parentId}/{newName}");

        try
        {
            await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.MoveFileNodeAsync(change.NodeId, parentId, newName, ct), ct);
            SyncEngine.SetInSync(change.LocalPath);
        }
        catch (Exception ex) when (ex.Message.Contains("notFound") || ex.Message.Contains("404"))
        {
            // Node no longer exists on server — treat as success
            Console.WriteLine($"Outbox: node {change.NodeId} not found on server during move");
        }
    }

    public void Dispose()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            try { _loopTask?.Wait(3000); }
            catch { /* shutdown */ }
            _cts.Dispose();
        }
        _workerSlots.Dispose();
    }
}
