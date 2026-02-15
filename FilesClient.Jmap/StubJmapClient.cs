using System.Runtime.CompilerServices;
using System.Text;
using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public class StubJmapClient : IJmapClient
{
    private const string FileBlobId = "stub-blob-1";

    private int _nextId = 2;
    private int _nextBlobId = 2;
    private readonly Dictionary<string, StorageNode> _nodes = new();
    private readonly Dictionary<string, byte[]> _blobs = new();

    public StubJmapClient()
    {
        var helloFile = new StorageNode
        {
            Id = "stub-file-1",
            Name = "hello.txt",
            ParentId = "root",
            BlobId = FileBlobId,
            Type = "text/plain",
            Size = 5,
            Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _nodes[helloFile.Id] = helloFile;
        _blobs[FileBlobId] = Encoding.UTF8.GetBytes("world");
    }

    public string AccountId => "stub-account";
    public string Username => "stub@example.com";

    public Task<StorageNode[]> GetStorageNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var nodes = ids
            .Where(id => _nodes.ContainsKey(id))
            .Select(id => _nodes[id])
            .ToArray();
        return Task.FromResult(nodes);
    }

    public Task<StorageNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var children = _nodes.Values.Where(n => n.ParentId == parentId).ToArray();
        return Task.FromResult(children);
    }

    public Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default)
    {
        return Task.FromResult(new ChangesResponse { NewState = sinceState });
    }

    public Task<string> GetStateAsync(CancellationToken ct = default)
    {
        return Task.FromResult("stub-state-1");
    }

    public Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default)
    {
        if (_blobs.TryGetValue(blobId, out var data))
            return Task.FromResult<Stream>(new MemoryStream(data));
        throw new InvalidOperationException($"Unknown blob: {blobId}");
    }

    public Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default)
    {
        if (_blobs.TryGetValue(blobId, out var data))
        {
            int start = (int)Math.Min(offset, data.Length);
            int count = (int)Math.Min(length, data.Length - start);
            var slice = new MemoryStream(data, start, count);
            return Task.FromResult<(Stream, bool)>((slice, true));
        }
        throw new InvalidOperationException($"Unknown blob: {blobId}");
    }

    public Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        data.CopyTo(ms);
        var blobId = $"stub-blob-{_nextBlobId++}";
        _blobs[blobId] = ms.ToArray();
        Console.WriteLine($"[Stub] Uploaded blob {blobId} ({ms.Length} bytes)");
        return Task.FromResult(blobId);
    }

    public Task DestroyStorageNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (_nodes.Remove(nodeId))
            Console.WriteLine($"[Stub] Destroyed StorageNode {nodeId}");
        return Task.CompletedTask;
    }

    public Task<StorageNode> CreateStorageNodeAsync(string parentId, string? blobId, string name, string? type = null, CancellationToken ct = default)
    {
        var id = $"stub-file-{_nextId++}";
        var size = blobId != null && _blobs.TryGetValue(blobId, out var data) ? data.Length : 0;
        var node = new StorageNode
        {
            Id = id,
            ParentId = parentId,
            BlobId = blobId,
            Name = name,
            Type = type,
            Size = size,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };
        _nodes[id] = node;
        Console.WriteLine($"[Stub] Created StorageNode {id}: {name}");
        return Task.FromResult(node);
    }

    public Task<StorageNode> ReplaceStorageNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default)
    {
        _nodes.Remove(nodeId);
        var newId = $"stub-file-{_nextId++}";
        var size = _blobs.TryGetValue(blobId, out var data) ? data.Length : 0;
        var newNode = new StorageNode
        {
            Id = newId,
            ParentId = parentId,
            BlobId = blobId,
            Name = name,
            Type = type,
            Size = size,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };
        _nodes[newId] = newNode;
        Console.WriteLine($"[Stub] Replaced StorageNode {nodeId} → {newId}: {name} (blob {blobId})");
        return Task.FromResult(newNode);
    }

    public Task MoveStorageNodeAsync(string nodeId, string parentId, string newName, CancellationToken ct = default)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            node.ParentId = parentId;
            node.Name = newName;
            node.Modified = DateTime.UtcNow;
            Console.WriteLine($"[Stub] Moved StorageNode {nodeId} to {parentId}/{newName}");
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> WatchForChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            yield return "stub-state";
        }
    }

    public void Dispose() { }
}
