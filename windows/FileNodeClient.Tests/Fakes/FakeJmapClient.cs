using System.Security.Cryptography;
using FileNodeClient.Jmap;
using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Tests.Fakes;

/// <summary>
/// An in-memory FileNode server behind <see cref="IJmapClient"/>. Tests script the
/// "other device" through the server-side methods (AddFile, Rename, Move, Delete,
/// SetContent); the engine under test sees the result through the JMAP-shaped ones.
///
/// Fidelity that matters to the engine and is therefore modelled: blobId is the
/// SHA-1 hex of the content (the outbox compares its local hash against it);
/// FileNode/changes classifies created/updated/destroyed relative to a state
/// counter and can page (R3) or refuse an old state (cannotCalculateChanges);
/// enumeration can be reported inconsistent (D2); onExists "rename"/"newest"/null
/// semantics on create and update; notFound on update/move/destroy of a missing
/// node. Everything the engine never calls throws NotImplementedException so a
/// new dependency is loud rather than silently green.
/// </summary>
public sealed class FakeJmapClient : IJmapClient
{
    public const string HomeId = "home";

    private readonly object _lock = new();
    private readonly Dictionary<string, FileNode> _nodes = new();
    private readonly Dictionary<string, byte[]> _blobs = new();
    private readonly List<(int Seq, string Kind, string Id)> _changes = new();
    private int _seq;
    private int _nextId = 1;

    // ---- Knobs ----

    /// <summary>FileNode/changes with a sinceState older than this throws cannotCalculateChanges.</summary>
    public int OldestCalculableState { get; set; } = 0;
    /// <summary>Max change records per FileNode/changes page (R3). Default: everything.</summary>
    public int ChangesPageSize { get; set; } = int.MaxValue;
    /// <summary>Report the id enumeration as inconsistent (D2).</summary>
    public bool EnumerationUnstable { get; set; }
    /// <summary>Ids to leave out of QueryAllFileNodeIds (a paging artefact) though they exist.</summary>
    public HashSet<string> OmitFromEnumeration { get; } = new();
    /// <summary>While set, UploadBlobAsync / MoveFileNodeAsync wait on it before completing.</summary>
    public TaskCompletionSource? Hold { get; set; }
    /// <summary>Advertise a digest algorithm (Blob/get digest:sha) so downloads are verified.</summary>
    public bool SupportsDigests { get; set; } = true;
    /// <summary>Blob ids whose Blob/get digest is deliberately wrong (simulated corruption).</summary>
    public HashSet<string> CorruptDigests { get; } = new();
    /// <summary>If set, DownloadBlobAsync streams are cut off after this many bytes.</summary>
    public int? TruncateDownloadsTo { get; set; }

    // ---- Observability ----

    public List<string> UploadedBlobIds { get; } = new();
    public int UploadCount => UploadedBlobIds.Count;
    public int MoveCount { get; private set; }
    public int CreateCount { get; private set; }
    public int ReplaceCount { get; private set; }
    public int DestroyCount { get; private set; }

    public FakeJmapClient(string accountId)
    {
        AccountId = accountId;
        Context = new JmapContext("tests", accountId);
        var now = DateTime.UtcNow;
        _nodes[HomeId] = new FileNode { Id = HomeId, Name = "", Role = "home", Created = now, Modified = now,
            MyRights = AllRights() };
    }

    private static FilesRights AllRights() => new()
    {
        MayRead = true, MayAddChildren = true, MayRename = true, MayDelete = true, MayModifyContent = true, MayShare = true,
    };

    private static FilesRights ReadOnlyRights() => new() { MayRead = true };

    public static string Sha1Hex(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    // ---- Server-side script API ("the other device") ----

    public string State { get { lock (_lock) return _seq.ToString(); } }

    public string AddFolder(string parentId, string name, bool readOnly = false)
    {
        lock (_lock)
        {
            var id = NewId();
            var now = DateTime.UtcNow;
            _nodes[id] = new FileNode { Id = id, ParentId = parentId, Name = name, Created = now, Modified = now,
                MyRights = readOnly ? ReadOnlyRights() : AllRights() };
            Record("created", id); Touch(parentId);
            return id;
        }
    }

    public string AddFile(string parentId, string name, byte[] content, string type = "application/octet-stream")
    {
        lock (_lock)
        {
            var id = NewId();
            var blobId = StoreBlob(content);
            var now = DateTime.UtcNow;
            _nodes[id] = new FileNode { Id = id, ParentId = parentId, Name = name, BlobId = blobId, Size = content.Length,
                Type = type, Created = now, Modified = now, MyRights = AllRights() };
            Record("created", id); Touch(parentId);
            return id;
        }
    }

    public string AddFile(string parentId, string name, string text) =>
        AddFile(parentId, name, System.Text.Encoding.UTF8.GetBytes(text), "text/plain");

    public void Rename(string id, string newName)
    {
        lock (_lock) { var n = Node(id); n.Name = newName; n.Modified = DateTime.UtcNow; Record("updated", id); Touch(n.ParentId); }
    }

    public void Move(string id, string newParentId)
    {
        lock (_lock)
        {
            var n = Node(id); var oldParent = n.ParentId;
            n.ParentId = newParentId; n.Modified = DateTime.UtcNow;
            Record("updated", id); Touch(oldParent); Touch(newParentId);
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            var n = Node(id);
            foreach (var child in _nodes.Values.Where(c => c.ParentId == id).Select(c => c.Id).ToList())
                Delete(child);
            _nodes.Remove(id);
            Record("destroyed", id); Touch(n.ParentId);
        }
    }

    public void SetContent(string id, byte[] content)
    {
        lock (_lock)
        {
            var n = Node(id); n.BlobId = StoreBlob(content); n.Size = content.Length; n.Modified = DateTime.UtcNow;
            Record("updated", id);
        }
    }

    public void SetContent(string id, string text) => SetContent(id, System.Text.Encoding.UTF8.GetBytes(text));

    public FileNode? TryGet(string id) { lock (_lock) return _nodes.TryGetValue(id, out var n) ? Clone(n) : null; }
    public FileNode Get(string id) => TryGet(id) ?? throw new KeyNotFoundException(id);
    public FileNode? FindByName(string parentId, string name)
    {
        lock (_lock) return _nodes.Values.Where(n => n.ParentId == parentId && n.Name == name).Select(Clone).FirstOrDefault();
    }
    public byte[] Content(string id) { lock (_lock) return _blobs[Node(id).BlobId!]; }
    public string ContentText(string id) => System.Text.Encoding.UTF8.GetString(Content(id));
    public IReadOnlyList<FileNode> Children(string parentId)
    {
        lock (_lock) return _nodes.Values.Where(n => n.ParentId == parentId).Select(Clone).ToList();
    }

    // ---- internals ----

    private string NewId() => $"n{_nextId++}";
    private FileNode Node(string id) => _nodes.TryGetValue(id, out var n) ? n
        : throw new JmapErrorException("FileNode/set update", "notFound", id);
    private void Record(string kind, string id) => _changes.Add((++_seq, kind, id));
    private void Touch(string? parentId)
    {
        if (parentId != null && _nodes.TryGetValue(parentId, out var p)) { p.Modified = DateTime.UtcNow; Record("updated", parentId); }
    }
    private string StoreBlob(byte[] content) { var id = Sha1Hex(content); _blobs[id] = content; return id; }
    private static FileNode Clone(FileNode n) => new()
    {
        Id = n.Id, ParentId = n.ParentId, BlobId = n.BlobId, Name = n.Name, Type = n.Type, Size = n.Size,
        Created = n.Created, Modified = n.Modified, Role = n.Role, MyRights = n.MyRights,
    };
    private string UniqueName(string parentId, string name, string? excludeId)
    {
        bool Taken(string candidate) => _nodes.Values.Any(n => n.ParentId == parentId && n.Id != excludeId
            && string.Equals(n.Name, candidate, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        var ext = Path.GetExtension(name); var stem = name[..^ext.Length];
        for (int i = 2; ; i++) { var c = $"{stem} ({i}){ext}"; if (!Taken(c)) return c; }
    }
    private async Task WaitIfHeldAsync(CancellationToken ct)
    {
        var hold = Hold;
        if (hold != null) await hold.Task.WaitAsync(ct);
    }

    // ---- IJmapClient: capabilities ----

    public JmapContext Context { get; }
    public string AccountId { get; }
    public string Username => "tests";
    public string? PreferredDigestAlgorithm => SupportsDigests ? "sha" : null;
    public long? ChunkSize => null;
    public int? MaxDataSources => null;
    public long? MaxSizeBlobSet => null;
    public bool HasBlob2 => false;                     // single-shot UploadBlobAsync path
    /// <summary>Enable to exercise ThumbnailService: Blob/convert yields a fake PNG per input blob.</summary>
    public bool SupportsBlobConvert { get; set; }
    public bool HasBlobConvert => SupportsBlobConvert;
    public int ConvertCount { get; private set; }
    public string? TrashUrl => null;
    public string? WebUrlTemplate => null;
    public string? WebWriteUrlTemplate => null;        // no Direct HTTP Write
    public bool CaseInsensitiveNames => true;

    // ---- IJmapClient: reads ----

    public Task<string> FindHomeNodeIdAsync(CancellationToken ct = default) => Task.FromResult(HomeId);
    public Task<string?> FindTrashNodeIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<FileNode[]> GetFileNodesAsync(string[] ids, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(ids.Where(_nodes.ContainsKey).Select(id => Clone(_nodes[id])).ToArray());
    }

    public Task<FileNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default) =>
        Task.FromResult(Children(parentId).ToArray());

    public async Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default) =>
        (await GetChangesAndNodesAsync(sinceState, ct)).Changes;

    public Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated, Quota[]? Quotas)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var since = int.Parse(sinceState);
            if (since < OldestCalculableState)
                throw new JmapErrorException("FileNode/changes", "cannotCalculateChanges", null);

            var pending = _changes.Where(c => c.Seq > since).OrderBy(c => c.Seq).ToList();
            var page = pending.Take(ChangesPageSize).ToList();
            var hasMore = pending.Count > page.Count;
            var newState = page.Count > 0 ? page[^1].Seq : _seq;

            var created = new List<string>(); var updated = new List<string>(); var destroyed = new List<string>();
            foreach (var group in page.GroupBy(c => c.Id))
            {
                var kinds = group.Select(c => c.Kind).ToHashSet();
                var exists = _nodes.ContainsKey(group.Key);
                if (kinds.Contains("created") && !exists) continue;          // born and died in the window
                if (!exists) destroyed.Add(group.Key);
                else if (kinds.Contains("created")) created.Add(group.Key);
                else updated.Add(group.Key);
            }

            var changes = new ChangesResponse
            {
                AccountId = AccountId, OldState = sinceState, NewState = newState.ToString(), HasMoreChanges = hasMore,
                Created = created.ToArray(), Updated = updated.ToArray(), Destroyed = destroyed.ToArray(),
            };
            FileNode[] Nodes(List<string> ids) => ids.Select(id => Clone(_nodes[id])).ToArray();
            return Task.FromResult((changes, Nodes(created), Nodes(updated), (Quota[]?)null));
        }
    }

    public Task<string> GetStateAsync(string homeNodeId, CancellationToken ct = default) => Task.FromResult(State);
    public Task<string> GetCurrentStateAsync(CancellationToken ct = default) => Task.FromResult(State);

    public Task<(string[] Ids, string QueryState, int Total, bool Consistent)> QueryAllFileNodeIdsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var ids = _nodes.Keys.Where(id => !OmitFromEnumeration.Contains(id)).ToArray();
            return Task.FromResult((ids, State, _nodes.Count, !EnumerationUnstable));
        }
    }

    public async Task<(FileNode[] Nodes, string State)> GetFileNodesByIdsPagedAsync(string[] ids, int pageSize = 0, CancellationToken ct = default) =>
        (await GetFileNodesAsync(ids, ct), State);

    // ---- IJmapClient: blobs ----

    public Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var bytes = _blobs[blobId];
            if (TruncateDownloadsTo is { } n && n < bytes.Length) bytes = bytes[..n];
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    public Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var blob = _blobs[blobId];
            var end = Math.Min(blob.Length, offset + length);
            var slice = blob.AsSpan((int)offset, (int)(end - offset)).ToArray();
            return Task.FromResult((new MemoryStream(slice, writable: false) as Stream, end < blob.Length));
        }
    }

    public async Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct);
        await WaitIfHeldAsync(ct);
        lock (_lock) { var id = StoreBlob(ms.ToArray()); UploadedBlobIds.Add(id); return id; }
    }

    public Task<BlobDataItem> GetBlobAsync(string blobId, string[] properties, long? offset = null, long? length = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var blob = _blobs[blobId];
            var digestOf = CorruptDigests.Contains(blobId) ? "not the real content"u8.ToArray() : blob;
            return Task.FromResult(new BlobDataItem
            {
                Id = blobId, Size = blob.Length,
                DigestSha = Convert.ToBase64String(SHA1.HashData(digestOf)),
                DataAsBase64 = properties.Contains("data:asBase64") ? Convert.ToBase64String(blob) : null,
            });
        }
    }

    // ---- IJmapClient: writes ----

    public async Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, string? onExists = null, DateTime? createdAt = null, DateTime? modifiedAt = null, CancellationToken ct = default)
    {
        await WaitIfHeldAsync(ct);
        lock (_lock)
        {
            if (!_nodes.ContainsKey(parentId))
                throw new JmapErrorException("FileNode/set create", "notFound", $"parent {parentId}");
            var existing = _nodes.Values.FirstOrDefault(n => n.ParentId == parentId
                && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                switch (onExists)
                {
                    case "replace": _nodes.Remove(existing.Id); Record("destroyed", existing.Id); break;
                    case "rename": name = UniqueName(parentId, name, null); break;
                    default: throw new JmapErrorException("FileNode/set create", "alreadyExists", "name in use");
                }
            }
            var id = NewId();
            var now = DateTime.UtcNow;
            var node = new FileNode
            {
                Id = id, ParentId = parentId, Name = name, BlobId = blobId, Type = type,
                Size = blobId != null && _blobs.TryGetValue(blobId, out var b) ? b.Length : null,
                Created = createdAt?.ToUniversalTime() ?? now, Modified = modifiedAt?.ToUniversalTime() ?? now, MyRights = AllRights(),
            };
            _nodes[id] = node; Record("created", id); Touch(parentId); CreateCount++;
            return Clone(node);
        }
    }

    public Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, DateTime? createdAt = null, DateTime? modifiedAt = null, string? onExists = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(nodeId, out var node))
                throw new JmapErrorException("FileNode/set update", "notFound", nodeId);
            if (onExists == "newest" && modifiedAt.HasValue && node.Modified >= modifiedAt.Value.ToUniversalTime())
                throw new JmapErrorException("FileNode/set update", "alreadyExists", "stored node is newer");
            node.BlobId = blobId;
            node.Size = _blobs.TryGetValue(blobId, out var b) ? b.Length : null;
            if (type != null) node.Type = type;
            node.Modified = modifiedAt?.ToUniversalTime() ?? DateTime.UtcNow;
            Record("updated", nodeId); ReplaceCount++;
            return Task.FromResult(Clone(node));
        }
    }

    public async Task MoveFileNodeAsync(string nodeId, string parentId, string newName, string? onExists = null, DateTime? modifiedAt = null, CancellationToken ct = default)
    {
        await WaitIfHeldAsync(ct);
        lock (_lock)
        {
            var node = Node(nodeId);
            var collision = _nodes.Values.Any(n => n.ParentId == parentId && n.Id != nodeId
                && string.Equals(n.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (collision)
            {
                if (onExists == "rename") newName = UniqueName(parentId, newName, nodeId);
                else throw new JmapErrorException("FileNode/set update", "alreadyExists", "name in use");
            }
            var oldParent = node.ParentId;
            node.ParentId = parentId; node.Name = newName;
            node.Modified = modifiedAt?.ToUniversalTime() ?? DateTime.UtcNow;
            Record("updated", nodeId); Touch(oldParent); if (oldParent != parentId) Touch(parentId);
            MoveCount++;
        }
    }

    public Task DestroyFileNodeAsync(string nodeId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Node(nodeId); // notFound if missing
            Delete(nodeId); DestroyCount++;
            return Task.CompletedTask;
        }
    }

    public Task BatchUpdateAccessedAsync(Dictionary<string, DateTime> accessed, CancellationToken ct = default) => Task.CompletedTask;
    public void RecordAccess(string nodeId) { }
    public Task<Quota[]> GetQuotasAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<Quota>());

    public async IAsyncEnumerable<string> WatchForChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    // ---- Not exercised by the engine paths under test ----

    public Task<string> UploadBlobChunkedAsync(Stream data, string contentType, long totalSize, Action<long>? onProgress = null,
        Action<JmapClient.UploadedChunkInfo>? onChunkUploaded = null, List<JmapClient.UploadedChunkInfo>? previousChunks = null,
        CancellationToken ct = default) => throw new NotImplementedException("chunked upload (HasBlob2 is false)");
    public Task<string> UploadBlobDeltaAsync(Stream data, string contentType, long totalSize, string? oldBlobId,
        Action<long>? onProgress = null, CancellationToken ct = default) => throw new NotImplementedException("delta upload (HasBlob2 is false)");
    public async Task<string> ConvertImageAsync(string blobId, uint width, uint height, string mimeType = "image/png", CancellationToken ct = default) =>
        (await ConvertImagesAsync([(blobId, width, height)], mimeType, ct))[blobId];

    public Task<Dictionary<string, string>> ConvertImagesAsync(IReadOnlyList<(string BlobId, uint Width, uint Height)> items,
        string mimeType = "image/png", CancellationToken ct = default)
    {
        if (!SupportsBlobConvert) throw new NotImplementedException("Blob/convert");
        lock (_lock)
        {
            ConvertCount++;
            var result = new Dictionary<string, string>();
            foreach (var (blobId, w, h) in items)
            {
                if (!_blobs.ContainsKey(blobId)) continue;
                var png = System.Text.Encoding.ASCII.GetBytes($"PNG:{blobId}:{w}x{h}");
                result[blobId] = StoreBlob(png);
            }
            return Task.FromResult(result);
        }
    }
    public Task<(string BlobId, long Size, string Type)> DirectWriteAsync(string nodeId, Stream data, string contentType, CancellationToken ct = default) =>
        throw new NotImplementedException("Direct HTTP Write (WebWriteUrlTemplate is null)");

    public void Dispose() { }
}
