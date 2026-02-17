using FilesClient.Jmap;

namespace FilesClient.Windows;

public class OutboxProcessor : IDisposable
{
    private readonly SyncOutbox _outbox;
    private readonly SyncEngine _engine;
    private readonly IJmapClient _jmapClient;
    private readonly JmapQueue _queue;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _online = true;

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
        _loopTask = Task.Run(() => ProcessLoop(_cts.Token));
    }

    public void SetOnline(bool online)
    {
        _online = online;
    }

    private async Task ProcessLoop(CancellationToken ct)
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

                var change = _outbox.DequeueNext();
                if (change == null)
                {
                    try { _outbox.WaitForWork(TimeSpan.FromSeconds(10), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                try
                {
                    await ProcessChangeAsync(change, ct);
                    _outbox.MarkCompleted(change.Id);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Outbox process error for {change.LocalPath ?? change.NodeId}: {ex.Message}");
                    _outbox.MarkFailed(change.Id, ex.Message);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Outbox loop error: {ex.Message}");
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
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
                () => _jmapClient.DestroyStorageNodeAsync(change.NodeId, ct), ct);
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
            () => _jmapClient.CreateStorageNodeAsync(parentId, null, folderName, null, ct), ct);

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
            using var stream = new FileStream(change.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var blobId = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.UploadBlobAsync(stream, contentType, ct), ct);
            var newNode = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.ReplaceStorageNodeBlobAsync(change.NodeId, parentId, fileName, blobId, contentType, ct), ct);

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
                        () => _jmapClient.MoveStorageNodeAsync(newNode.Id, newParentId, fileName, ct), ct);
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
            using var stream = new FileStream(change.LocalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var blobId = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.UploadBlobAsync(stream, contentType, ct), ct);
            var node = await _queue.EnqueueAsync(QueuePriority.Background,
                () => _jmapClient.CreateStorageNodeAsync(parentId, blobId, fileName, contentType, ct), ct);

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
                () => _jmapClient.MoveStorageNodeAsync(change.NodeId, parentId, newName, ct), ct);
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
    }
}
