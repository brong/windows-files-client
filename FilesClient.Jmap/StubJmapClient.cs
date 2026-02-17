using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public class StubJmapClient : IJmapClient
{
    private const string FileBlobId = "stub-blob-1";

    private int _nextId = 2;
    private int _nextBlobId = 2;
    private readonly Dictionary<string, FileNode> _nodes = new();
    private readonly Dictionary<string, byte[]> _blobs = new();

    public StubJmapClient()
    {
        var homeNode = new FileNode
        {
            Id = "stub-home",
            Name = "",
            Role = "home",
        };
        _nodes["stub-home"] = homeNode;

        var helloFile = new FileNode
        {
            Id = "stub-file-1",
            Name = "hello.txt",
            ParentId = "stub-home",
            BlobId = FileBlobId,
            Type = "text/plain",
            Size = 5,
            Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _nodes[helloFile.Id] = helloFile;
        _blobs[FileBlobId] = Encoding.UTF8.GetBytes("world");
    }

    public JmapContext Context { get; } = new("stub@example.com", "stub-account");
    public string AccountId => Context.AccountId;
    public string Username => Context.Username;
    public string? PreferredDigestAlgorithm => "sha-256";

    public Task<string> FindHomeNodeIdAsync(CancellationToken ct = default)
        => Task.FromResult("stub-home");

    public Task<FileNode[]> GetFileNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var nodes = ids
            .Where(id => _nodes.ContainsKey(id))
            .Select(id => _nodes[id])
            .ToArray();
        return Task.FromResult(nodes);
    }

    public Task<FileNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var children = _nodes.Values.Where(n => n.ParentId == parentId).ToArray();
        return Task.FromResult(children);
    }

    public Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default)
    {
        return Task.FromResult(new ChangesResponse { NewState = sinceState });
    }

    public Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        return Task.FromResult((new ChangesResponse { NewState = sinceState }, Array.Empty<FileNode>(), Array.Empty<FileNode>()));
    }

    public Task<string> GetStateAsync(string homeNodeId, CancellationToken ct = default)
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

    public Task DestroyFileNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (_nodes.Remove(nodeId))
            Console.WriteLine($"[Stub] Destroyed FileNode {nodeId}");
        return Task.CompletedTask;
    }

    public Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, CancellationToken ct = default)
    {
        var id = $"stub-file-{_nextId++}";
        var size = blobId != null && _blobs.TryGetValue(blobId, out var data) ? data.Length : 0;
        var node = new FileNode
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
        Console.WriteLine($"[Stub] Created FileNode {id}: {name}");
        return Task.FromResult(node);
    }

    public Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default)
    {
        _nodes.Remove(nodeId);
        var newId = $"stub-file-{_nextId++}";
        var size = _blobs.TryGetValue(blobId, out var data) ? data.Length : 0;
        var newNode = new FileNode
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
        Console.WriteLine($"[Stub] Replaced FileNode {nodeId} → {newId}: {name} (blob {blobId})");
        return Task.FromResult(newNode);
    }

    public Task MoveFileNodeAsync(string nodeId, string parentId, string newName, CancellationToken ct = default)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            node.ParentId = parentId;
            node.Name = newName;
            node.Modified = DateTime.UtcNow;
            Console.WriteLine($"[Stub] Moved FileNode {nodeId} to {parentId}/{newName}");
        }
        return Task.CompletedTask;
    }

    public Task<BlobDataItem> GetBlobAsync(string blobId, string[] properties,
        long? offset = null, long? length = null, CancellationToken ct = default)
    {
        if (!_blobs.TryGetValue(blobId, out var data))
            throw new FileNotFoundException($"Unknown blob: {blobId}");

        byte[] slice = data;
        if (offset.HasValue || length.HasValue)
        {
            int start = (int)(offset ?? 0);
            int count = (int)(length ?? (data.Length - start));
            count = Math.Min(count, data.Length - start);
            slice = new byte[count];
            Array.Copy(data, start, slice, 0, count);
        }

        var item = new BlobDataItem
        {
            Id = blobId,
            Size = slice.Length,
        };

        foreach (var prop in properties)
        {
            if (prop == "data:asBase64")
                item.DataAsBase64 = Convert.ToBase64String(slice);
            else if (prop == "digest:sha")
                item.DigestSha = Convert.ToBase64String(SHA1.HashData(slice));
            else if (prop == "digest:sha-256")
                item.DigestSha256 = Convert.ToBase64String(SHA256.HashData(slice));
        }

        return Task.FromResult(item);
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
