using System.Text;
using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public class StubJmapClient : IJmapClient
{
    private const string FileId = "stub-file-1";
    private const string FileBlobId = "stub-blob-1";

    private static readonly StorageNode HelloFile = new()
    {
        Id = FileId,
        Name = "hello.txt",
        ParentId = "root",
        BlobId = FileBlobId,
        Type = "text/plain",
        Size = 5,
        Created = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public string AccountId => "stub-account";

    public Task<StorageNode[]> GetStorageNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var nodes = ids
            .Where(id => id == FileId)
            .Select(_ => HelloFile)
            .ToArray();
        return Task.FromResult(nodes);
    }

    public Task<StorageNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        if (parentId == "root")
            return Task.FromResult(new[] { HelloFile });
        return Task.FromResult(Array.Empty<StorageNode>());
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
        if (blobId == FileBlobId)
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("world")));
        throw new InvalidOperationException($"Unknown blob: {blobId}");
    }

    public void Dispose() { }
}
