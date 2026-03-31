using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Jmap;

/// <summary>
/// Thin wrapper around a parent <see cref="JmapClient"/> that targets a
/// different accountId. Shares the parent's HttpClient and JmapSession.
/// </summary>
public class AccountScopedJmapClient : IJmapClient
{
    private readonly JmapClient _parent;
    private readonly string _accountId;
    private readonly JmapContext _context;
    private bool _preferredDigestResolved;
    private string? _preferredDigestAlgorithm;
    private bool _chunkSizeResolved;
    private long? _chunkSize;
    private bool _maxDataSourcesResolved;
    private int? _maxDataSources;
    private bool _maxSizeBlobSetResolved;
    private long? _maxSizeBlobSet;
    private bool _trashUrlResolved;
    private string? _trashUrl;
    private bool _webUrlTemplateResolved;
    private string? _webUrlTemplate;
    private bool _webWriteUrlTemplateResolved;
    private string? _webWriteUrlTemplate;
    private readonly ConcurrentDictionary<string, DateTime> _pendingAccessed = new();
    private Timer? _accessedFlushTimer;
    private static readonly HashSet<string> SupportedDigests = ["sha", "sha-256"];

    public AccountScopedJmapClient(JmapClient parent, string accountId)
    {
        _parent = parent;
        _accountId = accountId;
        // Use the login username (not the account display name) so the scope key
        // is stable and consistent with the primary account's JmapContext.
        _context = new JmapContext(parent.Session.Username, accountId);
    }

    public JmapContext Context => _context;
    public string AccountId => _accountId;
    public string Username => _context.Username;

    public string? PreferredDigestAlgorithm
    {
        get
        {
            if (!_preferredDigestResolved)
            {
                var algos = _parent.Session.GetSupportedDigestAlgorithms(_accountId);
                _preferredDigestAlgorithm = algos.FirstOrDefault(a => SupportedDigests.Contains(a));
                _preferredDigestResolved = true;
            }
            return _preferredDigestAlgorithm;
        }
    }

    public long? ChunkSize
    {
        get
        {
            if (!_chunkSizeResolved)
            {
                _chunkSize = _parent.Session.GetChunkSize(_accountId);
                _chunkSizeResolved = true;
            }
            return _chunkSize;
        }
    }

    public int? MaxDataSources
    {
        get
        {
            if (!_maxDataSourcesResolved)
            {
                _maxDataSources = _parent.Session.GetMaxDataSources(_accountId);
                _maxDataSourcesResolved = true;
            }
            return _maxDataSources;
        }
    }

    public long? MaxSizeBlobSet
    {
        get
        {
            if (!_maxSizeBlobSetResolved)
            {
                _maxSizeBlobSet = _parent.Session.GetMaxSizeBlobSet(_accountId);
                _maxSizeBlobSetResolved = true;
            }
            return _maxSizeBlobSet;
        }
    }

    public bool HasBlob => _parent.Session.HasAccountCapability(_accountId, JmapClient.BlobCapability);
    public bool HasBlobExt => _parent.Session.HasAccountCapability(_accountId, JmapClient.BlobExtCapability);
    public bool HasBlobConvert => HasBlobExt;

    public string? TrashUrl
    {
        get
        {
            if (!_trashUrlResolved)
            {
                _trashUrl = _parent.Session.GetTrashUrl(_accountId);
                _trashUrlResolved = true;
            }
            return _trashUrl;
        }
    }

    public string? WebUrlTemplate
    {
        get
        {
            if (!_webUrlTemplateResolved)
            {
                _webUrlTemplate = _parent.Session.GetWebUrlTemplate(_accountId);
                _webUrlTemplateResolved = true;
            }
            return _webUrlTemplate;
        }
    }

    public string? WebWriteUrlTemplate
    {
        get
        {
            if (!_webWriteUrlTemplateResolved)
            {
                _webWriteUrlTemplate = _parent.Session.GetWebWriteUrlTemplate(_accountId);
                _webWriteUrlTemplateResolved = true;
            }
            return _webWriteUrlTemplate;
        }
    }

    private HttpClient Http => _parent.Http;
    private JmapSession Session => _parent.Session;

    private static readonly string[] FileNodeUsing = [JmapClient.CoreCapability, JmapClient.FileNodeCapability];
    private static readonly string[] BlobUsing = [JmapClient.CoreCapability, JmapClient.BlobCapability];
    private static readonly string[] BlobExtUsing = [JmapClient.CoreCapability, JmapClient.BlobCapability, JmapClient.BlobExtCapability];

    private string NextCallId() => "c" + Interlocked.Increment(ref _parent.NextCallIdRef);

    private async Task<JsonElement> CallAsync(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var callId = NextCallId();

        // Check for pending accessed timestamps to piggyback
        var accessedBatch = DrainPendingAccessed();

        JmapRequest request;
        if (accessedBatch.Count > 0)
        {
            var accessedCallId = "_accessed";
            var accessedUpdate = new Dictionary<string, object>();
            foreach (var (nodeId, time) in accessedBatch)
                accessedUpdate[nodeId] = new { accessed = time.ToUniversalTime() };

            // Ensure FileNode capability is included
            var caps = capabilities.Contains(JmapClient.FileNodeCapability)
                ? capabilities
                : capabilities.Append(JmapClient.FileNodeCapability).ToArray();

            request = JmapRequest.Create(caps,
                (method, args, callId),
                ("FileNode/set", new { accountId = _accountId, update = accessedUpdate }, accessedCallId));
        }
        else
        {
            request = JmapRequest.Create(capabilities, (method, args, callId));
        }

        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await Http.PostAsync(Session.ApiUrl, content, ct);
        httpResponse.EnsureSuccessStatusCode();
        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");

        if (response.MethodResponses.Length == 0)
            throw new InvalidOperationException($"No response for {method} call");

        // Return only the primary response (index 0), ignore the piggybacked accessed response
        var entry = response.MethodResponses[0];
        var respMethod = entry[0].GetString() ?? "";
        if (respMethod == "error")
            throw new InvalidOperationException($"JMAP error: {entry[1]}");
        if (respMethod != method)
            throw new InvalidOperationException($"JMAP method mismatch: expected {method}, got {respMethod}");

        return entry[1];
    }

    private async Task<T> CallAsync<T>(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var result = await CallAsync(capabilities, method, args, ct);
        return result.Deserialize<T>(JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize {method} response");
    }

    private static T GetValidatedResult<T>(
        Dictionary<string, (string method, JsonElement args)> responseMap,
        string callId, string expectedMethod)
    {
        if (!responseMap.TryGetValue(callId, out var resp))
            throw new InvalidOperationException($"No response for call ID {callId}");
        if (resp.method == "error")
            throw new InvalidOperationException($"JMAP error: {resp.args}");
        if (resp.method != expectedMethod)
            throw new InvalidOperationException(
                $"JMAP method mismatch: expected {expectedMethod}, got {resp.method}");
        return resp.args.Deserialize<T>(JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize {expectedMethod} response");
    }

    public async Task<string> FindHomeNodeIdAsync(CancellationToken ct = default)
    {
        var queryCallId = NextCallId();
        var getCallId = NextCallId();

        var request = JmapRequest.Create(FileNodeUsing,
            ("FileNode/query", new
            {
                accountId = _accountId,
                filter = new { role = "home" },
            }, queryCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = _accountId,
                ["#ids"] = new { resultOf = queryCallId, name = "FileNode/query", path = "/ids" },
            }, getCallId));

        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await Http.PostAsync(Session.ApiUrl, content, ct);
        httpResponse.EnsureSuccessStatusCode();
        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");

        var responseMap = new Dictionary<string, (string method, JsonElement args)>();
        foreach (var entry in response.MethodResponses)
        {
            var respCallId = entry[2].GetString() ?? "";
            var respMethod = entry[0].GetString() ?? "";
            responseMap[respCallId] = (respMethod, entry[1]);
        }

        GetValidatedResult<QueryResponse>(responseMap, queryCallId, "FileNode/query");
        var getResult = GetValidatedResult<GetResponse<FileNode>>(responseMap, getCallId, "FileNode/get");

        if (getResult.List.Length == 0)
            throw new InvalidOperationException("No FileNode with role 'home' found");

        return getResult.List[0].Id;
    }

    public async Task<string?> FindTrashNodeIdAsync(CancellationToken ct = default)
    {
        var queryCallId = NextCallId();
        var getCallId = NextCallId();

        var request = JmapRequest.Create(FileNodeUsing,
            ("FileNode/query", new
            {
                accountId = _accountId,
                filter = new { role = "trash" },
            }, queryCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = _accountId,
                ["#ids"] = new { resultOf = queryCallId, name = "FileNode/query", path = "/ids" },
            }, getCallId));

        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await Http.PostAsync(Session.ApiUrl, content, ct);
        httpResponse.EnsureSuccessStatusCode();
        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");

        var responseMap = new Dictionary<string, (string method, JsonElement args)>();
        foreach (var entry in response.MethodResponses)
        {
            var respCallId = entry[2].GetString() ?? "";
            var respMethod = entry[0].GetString() ?? "";
            responseMap[respCallId] = (respMethod, entry[1]);
        }

        GetValidatedResult<QueryResponse>(responseMap, queryCallId, "FileNode/query");
        var getResult = GetValidatedResult<GetResponse<FileNode>>(responseMap, getCallId, "FileNode/get");

        return getResult.List.Length > 0 ? getResult.List[0].Id : null;
    }

    public async Task<FileNode[]> GetFileNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<FileNode>>(
            FileNodeUsing, "FileNode/get", new { accountId = _accountId, ids, properties = JmapClient.FileNodeProperties }, ct);
        return result.List;
    }

    public async Task<FileNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var queryCallId = NextCallId();
        var getCallId = NextCallId();

        var request = JmapRequest.Create(FileNodeUsing,
            ("FileNode/query", new
            {
                accountId = _accountId,
                filter = new { parentId },
                sort = new[] { new { property = "name", isAscending = true } },
            }, queryCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = _accountId,
                ["#ids"] = new { resultOf = queryCallId, name = "FileNode/query", path = "/ids" },
                ["properties"] = JmapClient.FileNodeProperties,
            }, getCallId));

        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await Http.PostAsync(Session.ApiUrl, content, ct);
        httpResponse.EnsureSuccessStatusCode();
        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");

        var responseMap = new Dictionary<string, (string method, JsonElement args)>();
        foreach (var entry in response.MethodResponses)
        {
            var respCallId = entry[2].GetString() ?? "";
            var respMethod = entry[0].GetString() ?? "";
            responseMap[respCallId] = (respMethod, entry[1]);
        }

        GetValidatedResult<QueryResponse>(responseMap, queryCallId, "FileNode/query");
        var getResult = GetValidatedResult<GetResponse<FileNode>>(responseMap, getCallId, "FileNode/get");

        return getResult.List;
    }

    public async Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default)
    {
        return await CallAsync<ChangesResponse>(
            FileNodeUsing, "FileNode/changes", new { accountId = _accountId, sinceState }, ct);
    }

    public async Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated, Quota[]? Quotas)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        var changesCallId = NextCallId();
        var createdCallId = NextCallId();
        var updatedCallId = NextCallId();

        var calls = new List<(string method, object args, string callId)>
        {
            ("FileNode/changes", new { accountId = _accountId, sinceState }, changesCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = _accountId,
                ["#ids"] = new { resultOf = changesCallId, name = "FileNode/changes", path = "/created" },
                ["properties"] = JmapClient.FileNodeProperties,
            }, createdCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = _accountId,
                ["#ids"] = new { resultOf = changesCallId, name = "FileNode/changes", path = "/updated" },
                ["properties"] = JmapClient.FileNodeProperties,
            }, updatedCallId),
        };

        string? quotaCallId = null;
        string[] capabilities = FileNodeUsing;
        if (Session.HasCapability(JmapClient.QuotaCapability))
        {
            quotaCallId = NextCallId();
            capabilities = [JmapClient.CoreCapability, JmapClient.FileNodeCapability, JmapClient.QuotaCapability];
            calls.Add(("Quota/get", new Dictionary<string, JsonElement>
            {
                ["accountId"] = JsonSerializer.SerializeToElement(_accountId),
                ["ids"] = JsonSerializer.SerializeToElement<string[]?>(null),
            }, quotaCallId));
        }

        // Piggyback pending accessed timestamps
        var accessedBatch = DrainPendingAccessed();
        if (accessedBatch.Count > 0)
        {
            var accessedCallId = "_accessed";
            var accessedUpdate = new Dictionary<string, object>();
            foreach (var (nodeId, time) in accessedBatch)
                accessedUpdate[nodeId] = new { accessed = time.ToUniversalTime() };
            calls.Add(("FileNode/set", (object)new { accountId = _accountId, update = accessedUpdate }, accessedCallId));
        }

        var request = JmapRequest.Create(capabilities, calls.ToArray());
        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await Http.PostAsync(Session.ApiUrl, content, ct);
        httpResponse.EnsureSuccessStatusCode();
        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");

        var responseMap = new Dictionary<string, (string method, JsonElement args)>();
        foreach (var entry in response.MethodResponses)
        {
            var respCallId = entry[2].GetString() ?? "";
            var respMethod = entry[0].GetString() ?? "";
            responseMap[respCallId] = (respMethod, entry[1]);
        }

        var changes = GetValidatedResult<ChangesResponse>(responseMap, changesCallId, "FileNode/changes");
        var created = GetValidatedResult<GetResponse<FileNode>>(responseMap, createdCallId, "FileNode/get");
        var updated = GetValidatedResult<GetResponse<FileNode>>(responseMap, updatedCallId, "FileNode/get");

        Quota[]? quotas = null;
        if (quotaCallId != null && responseMap.TryGetValue(quotaCallId, out var quotaResp)
            && quotaResp.method == "Quota/get")
        {
            var quotaResult = quotaResp.args.Deserialize<GetResponse<Quota>>(JmapSerializerOptions.Default);
            if (quotaResult != null)
                quotas = quotaResult.List;
        }

        return (changes, created.List, updated.List, quotas);
    }

    public async Task<string> GetStateAsync(string homeNodeId, CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<FileNode>>(
            FileNodeUsing, "FileNode/get", new { accountId = _accountId, ids = new[] { homeNodeId } }, ct);
        return result.State;
    }

    public async Task<string> GetCurrentStateAsync(CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<FileNode>>(
            FileNodeUsing, "FileNode/get", new { accountId = _accountId, ids = Array.Empty<string>() }, ct);
        return result.State;
    }

    public async Task<(string[] Ids, string QueryState, int Total)> QueryAllFileNodeIdsAsync(CancellationToken ct = default)
    {
        var allIds = new List<string>();
        int position = 0;
        const int limit = 4096;
        string queryState = "";
        int total = 0;

        while (true)
        {
            var result = await CallAsync<QueryResponse>(
                FileNodeUsing, "FileNode/query", new { accountId = _accountId, position, limit }, ct);

            queryState = result.QueryState;
            if (result.Total.HasValue)
                total = result.Total.Value;

            allIds.AddRange(result.Ids);

            if (result.Ids.Length < limit || (result.Total.HasValue && allIds.Count >= result.Total.Value))
                break;

            position = allIds.Count;
        }

        return (allIds.ToArray(), queryState, total > 0 ? total : allIds.Count);
    }

    public async Task<(FileNode[] Nodes, string State)> GetFileNodesByIdsPagedAsync(string[] ids, int pageSize = 0, CancellationToken ct = default)
    {
        if (pageSize <= 0) pageSize = Session.MaxObjectsInGet;
        var allNodes = new List<FileNode>();
        string state = "";

        for (int i = 0; i < ids.Length; i += pageSize)
        {
            var chunk = ids.Skip(i).Take(pageSize).ToArray();
            var result = await CallAsync<GetResponse<FileNode>>(
                FileNodeUsing, "FileNode/get", new { accountId = _accountId, ids = chunk, properties = JmapClient.FileNodeProperties }, ct);
            allNodes.AddRange(result.List);
            state = result.State;
        }

        return (allNodes.ToArray(), state);
    }

    public async Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default)
    {
        var url = Session.GetDownloadUrl(_accountId, blobId, type, name);
        var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default)
    {
        var url = Session.GetDownloadUrl(_accountId, blobId, type, name);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);

        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        bool isPartial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        return (stream, isPartial);
    }

    public async Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default)
    {
        var url = Session.GetUploadUrl(_accountId);
        var content = new StreamContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content, Version = System.Net.HttpVersion.Version11 };
        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var upload = JsonSerializer.Deserialize<UploadResponse>(json, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse upload response");
        return upload.BlobId;
    }

    public Task<string> UploadBlobChunkedAsync(Stream data, string contentType, long totalSize,
        Action<long>? onProgress = null, Action<JmapClient.UploadedChunkInfo>? onChunkUploaded = null,
        List<JmapClient.UploadedChunkInfo>? previousChunks = null,
        CancellationToken ct = default)
    {
        var baseChunkSize = ChunkSize ?? JmapClient.MaxChunkSize;

        var maxSize = MaxSizeBlobSet;
        if (maxSize.HasValue && totalSize > maxSize.Value)
            throw new InvalidOperationException(
                $"File size {totalSize} exceeds server maxSizeBlobSet {maxSize.Value}");

        var effectiveChunkSize = Math.Max(baseChunkSize, JmapClient.MinChunkSize);

        var maxSources = MaxDataSources;
        if (maxSources.HasValue && maxSources.Value > 0 && totalSize > 0)
        {
            while ((totalSize + effectiveChunkSize - 1) / effectiveChunkSize > maxSources.Value
                   && effectiveChunkSize < JmapClient.MaxChunkSize)
                effectiveChunkSize *= 2;
        }

        if (effectiveChunkSize > JmapClient.MaxChunkSize)
            effectiveChunkSize = JmapClient.MaxChunkSize;

        var hasBlobExt = HasBlobExt;
        return JmapClient.UploadBlobChunkedInternalAsync(Http, Session.GetUploadUrl(_accountId), _accountId,
            effectiveChunkSize, hasBlobExt,
            (caps, method, args) => CallAsync(caps, method, args, ct),
            data, contentType, totalSize, onProgress, onChunkUploaded, previousChunks, ct);
    }

    public async Task<string> UploadBlobDeltaAsync(Stream data, string contentType, long totalSize,
        string? oldBlobId,
        Action<long>? onProgress = null, CancellationToken ct = default)
    {
        // No old blob or no BlobExt → fall back to full chunked upload
        if (oldBlobId == null || !HasBlobExt)
            return await UploadBlobChunkedAsync(data, contentType, totalSize, onProgress, ct: ct);

        // Query server for old blob's chunk structure
        List<(string blobId, long size, string? digestSha)>? serverChunks = null;
        try
        {
            var blobResponse = await CallAsync<BlobGetResponse>(BlobUsing, "Blob/get", new
            {
                accountId = _accountId,
                ids = new[] { oldBlobId },
                properties = new[] { "id", "size", "chunks" },
            }, ct);

            if (blobResponse.List.Length > 0 && blobResponse.List[0].Chunks != null)
            {
                serverChunks = blobResponse.List[0].Chunks!
                    .Select(c => (c.BlobId, c.Size, c.DigestSha))
                    .ToList();
            }
        }
        catch
        {
            // Fall back to full upload
        }

        if (serverChunks == null || serverChunks.Count == 0)
            return await UploadBlobChunkedAsync(data, contentType, totalSize, onProgress, ct: ct);

        // Delta upload: compare local chunks against server chunks
        var uploadUrl = Session.GetUploadUrl(_accountId);
        var hasBlobExt = HasBlobExt;
        var chunkBlobIds = new List<(string BlobId, string Sha1Base64)>();
        using var overallHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
        long totalUploaded = 0;
        int reusedChunks = 0;

        for (int i = 0; i < serverChunks.Count && totalUploaded < totalSize; i++)
        {
            var serverChunk = serverChunks[i];
            var thisChunkSize = (int)Math.Min(serverChunk.size, totalSize - totalUploaded);

            var chunkData = new byte[thisChunkSize];
            int bytesRead = 0;
            while (bytesRead < thisChunkSize)
            {
                var n = await data.ReadAsync(chunkData.AsMemory(bytesRead, thisChunkSize - bytesRead), ct);
                if (n == 0) break;
                bytesRead += n;
            }

            overallHash.AppendData(chunkData, 0, bytesRead);
            var localSha1 = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(chunkData.AsSpan(0, bytesRead)));

            if (serverChunk.digestSha != null && localSha1 == serverChunk.digestSha)
            {
                chunkBlobIds.Add((serverChunk.blobId, localSha1));
                reusedChunks++;
            }
            else
            {
                using var chunkStream = new MemoryStream(chunkData, 0, bytesRead);
                var chunkContent = new StreamContent(chunkStream);
                chunkContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var chunkRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = chunkContent, Version = System.Net.HttpVersion.Version11 };
                var response = await Http.SendAsync(chunkRequest, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                var upload = JsonSerializer.Deserialize<UploadResponse>(json, JmapSerializerOptions.Default)
                    ?? throw new InvalidOperationException("Failed to parse chunk upload response");
                chunkBlobIds.Add((upload.BlobId, localSha1));
            }

            totalUploaded += bytesRead;
            onProgress?.Invoke(totalUploaded);
        }

        // Upload any remaining data beyond server's chunk count
        while (totalUploaded < totalSize)
        {
            var remaining = (int)Math.Min(JmapClient.MaxChunkSize, totalSize - totalUploaded);
            var chunkData = new byte[remaining];
            int bytesRead = 0;
            while (bytesRead < remaining)
            {
                var n = await data.ReadAsync(chunkData.AsMemory(bytesRead, remaining - bytesRead), ct);
                if (n == 0) break;
                bytesRead += n;
            }

            overallHash.AppendData(chunkData, 0, bytesRead);
            var localSha1 = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(chunkData.AsSpan(0, bytesRead)));

            using var chunkStream = new MemoryStream(chunkData, 0, bytesRead);
            var chunkContent = new StreamContent(chunkStream);
            chunkContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var chunkRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = chunkContent, Version = System.Net.HttpVersion.Version11 };
            var uploadResp = await Http.SendAsync(chunkRequest, ct);
            uploadResp.EnsureSuccessStatusCode();
            var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);
            var upload = JsonSerializer.Deserialize<UploadResponse>(uploadJson, JmapSerializerOptions.Default)
                ?? throw new InvalidOperationException("Failed to parse chunk upload response");
            chunkBlobIds.Add((upload.BlobId, localSha1));
            totalUploaded += bytesRead;
            onProgress?.Invoke(totalUploaded);
        }

        if (chunkBlobIds.Count == 1)
            return chunkBlobIds[0].BlobId;

        // Combine
        var overallSha1Base64 = Convert.ToBase64String(overallHash.GetHashAndReset());
        var dataArray = chunkBlobIds.Select(c =>
        {
            var item = new Dictionary<string, object?> { ["blobId"] = c.BlobId };
            if (hasBlobExt) item["digest:sha"] = c.Sha1Base64;
            return item;
        }).ToArray();

        var createId = "delta0";
        var createItem = new Dictionary<string, object>
        {
            ["data"] = dataArray,
            ["type"] = contentType,
        };
        if (hasBlobExt) createItem["digest:sha"] = overallSha1Base64;

        var combineResult = await CallAsync(hasBlobExt ? BlobExtUsing : BlobUsing, "Blob/upload", new
        {
            accountId = _accountId,
            create = new Dictionary<string, object> { [createId] = createItem },
        }, ct);

        var blobUpload = combineResult.Deserialize<BlobUploadResponse>(JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse Blob/upload response");
        if (blobUpload.NotCreated != null && blobUpload.NotCreated.TryGetValue(createId, out var err))
            throw new InvalidOperationException($"Blob/upload failed: {err.Type} — {err.Description}");
        if (blobUpload.Created == null || !blobUpload.Created.TryGetValue(createId, out var created))
            throw new InvalidOperationException("Blob/upload returned no result");

        return created.Id;
    }

    public async Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, string? onExists = null, DateTime? createdAt = null, DateTime? modifiedAt = null, CancellationToken ct = default)
    {
        var createObj = new Dictionary<string, object?>
        {
            ["parentId"] = parentId, ["blobId"] = blobId, ["name"] = name, ["type"] = type,
        };
        if (createdAt.HasValue)
            createObj["created"] = createdAt.Value.ToUniversalTime();
        if (modifiedAt.HasValue)
            createObj["modified"] = modifiedAt.Value.ToUniversalTime();

        var args = new Dictionary<string, object?>
        {
            ["accountId"] = _accountId,
            ["create"] = new Dictionary<string, object?> { ["c0"] = createObj },
        };
        if (onExists != null)
            args["onExists"] = onExists;

        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", args, ct);

        if (setResponse.NotCreated != null && setResponse.NotCreated.TryGetValue("c0", out var setError))
            throw new InvalidOperationException($"FileNode/set create failed: {setError.Type} — {setError.Description}");

        if (setResponse.Created == null || !setResponse.Created.TryGetValue("c0", out var created))
            throw new InvalidOperationException("FileNode/set create returned no result");

        return created;
    }

    public async Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, DateTime? createdAt = null, DateTime? modifiedAt = null, CancellationToken ct = default)
    {
        // v10: blobId is now mutable — update directly via FileNode/set update.
        // Node ID stays the same (no destroy+create needed).
        var updateFields = new Dictionary<string, object?>
        {
            ["blobId"] = blobId,
        };
        if (type != null)
            updateFields["type"] = type;
        if (modifiedAt.HasValue)
            updateFields["modified"] = modifiedAt.Value.ToUniversalTime();

        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = _accountId,
                update = new Dictionary<string, object?> { [nodeId] = updateFields },
            }, ct);

        if (setResponse.NotUpdated != null && setResponse.NotUpdated.TryGetValue(nodeId, out var updateError))
            throw new InvalidOperationException($"FileNode/set update failed: {updateError.Type} — {updateError.Description}");

        // Return a FileNode with the known values since update response only has changed fields
        return new FileNode
        {
            Id = nodeId,
            ParentId = parentId,
            BlobId = blobId,
            Name = name,
            Type = type,
            Modified = modifiedAt?.ToUniversalTime(),
            Created = createdAt?.ToUniversalTime(),
        };
    }

    public async Task MoveFileNodeAsync(string nodeId, string parentId, string newName, string? onExists = null, DateTime? modifiedAt = null, CancellationToken ct = default)
    {
        var updateFields = new Dictionary<string, object?> { };
        updateFields["parentId"] = parentId;
        updateFields["name"] = newName;
        if (modifiedAt.HasValue)
            updateFields["modified"] = modifiedAt.Value.ToUniversalTime();
        else
            updateFields["modified"] = null; // Server sets current time

        var args = new Dictionary<string, object>
        {
            ["accountId"] = _accountId,
            ["update"] = new Dictionary<string, object?> { [nodeId] = updateFields },
        };
        if (onExists != null)
            args["onExists"] = onExists;

        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", args, ct);

        if (setResponse.NotUpdated != null && setResponse.NotUpdated.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set move failed: {setError.Type} — {setError.Description}");
    }

    public async Task BatchUpdateAccessedAsync(Dictionary<string, DateTime> accessed, CancellationToken ct = default)
    {
        if (accessed.Count == 0) return;

        var update = new Dictionary<string, object>();
        foreach (var (nodeId, time) in accessed)
        {
            update[nodeId] = new { accessed = time.ToUniversalTime() };
        }

        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = _accountId,
                update,
            }, ct);
        // Ignore individual notUpdated errors — node may have been deleted
    }

    public void RecordAccess(string nodeId)
    {
        _pendingAccessed[nodeId] = DateTime.UtcNow;
        ResetAccessedFlushTimer();
    }

    private void ResetAccessedFlushTimer()
    {
        _accessedFlushTimer?.Dispose();
        _accessedFlushTimer = new Timer(async _ =>
        {
            try { await FlushPendingAccessedAsync(); }
            catch { /* best effort */ }
        }, null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
    }

    private async Task FlushPendingAccessedAsync(CancellationToken ct = default)
    {
        var batch = DrainPendingAccessed();
        if (batch.Count == 0) return;
        try
        {
            await BatchUpdateAccessedAsync(batch, ct);
        }
        catch
        {
            // Re-add on failure
            foreach (var (nodeId, time) in batch)
                _pendingAccessed.TryAdd(nodeId, time);
        }
    }

    private Dictionary<string, DateTime> DrainPendingAccessed()
    {
        var batch = new Dictionary<string, DateTime>();
        foreach (var key in _pendingAccessed.Keys.ToArray())
        {
            if (_pendingAccessed.TryRemove(key, out var time))
                batch[key] = time;
        }
        if (batch.Count > 0)
            _accessedFlushTimer?.Dispose();
        return batch;
    }

    public async Task DestroyFileNodeAsync(string nodeId, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = _accountId,
                onDestroyRemoveChildren = true,
                destroy = new[] { nodeId },
            }, ct);

        if (setResponse.NotDestroyed != null && setResponse.NotDestroyed.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set destroy failed: {setError.Type} — {setError.Description}");
    }

    public async IAsyncEnumerable<string> WatchForChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Delegate to parent's shared SSE, filtering for this account
        await foreach (var (accountId, state) in _parent.WatchAllAccountChangesAsync(ct))
        {
            if (accountId == _accountId)
                yield return state;
        }
    }

    public async Task<BlobDataItem> GetBlobAsync(string blobId, string[] properties,
        long? offset = null, long? length = null, CancellationToken ct = default)
    {
        var blobArgs = new Dictionary<string, object?>
        {
            ["accountId"] = _accountId,
            ["ids"] = new[] { blobId },
            ["properties"] = properties,
        };

        if (offset.HasValue) blobArgs["offset"] = offset.Value;
        if (length.HasValue) blobArgs["length"] = length.Value;

        var blobResponse = await CallAsync<BlobGetResponse>(BlobUsing, "Blob/get", blobArgs, ct);
        if (blobResponse.NotFound.Length > 0)
            throw new FileNotFoundException($"Blob not found: {blobId}");
        if (blobResponse.List.Length == 0)
            throw new InvalidOperationException($"Blob/get returned no results for {blobId}");

        return blobResponse.List[0];
    }

    public async Task<Quota[]> GetQuotasAsync(CancellationToken ct = default)
    {
        if (!Session.HasCapability(JmapClient.QuotaCapability))
            return [];

        var result = await CallAsync<GetResponse<Quota>>(
            [JmapClient.CoreCapability, JmapClient.QuotaCapability],
            "Quota/get", new Dictionary<string, JsonElement>
            {
                ["accountId"] = JsonSerializer.SerializeToElement(_accountId),
                ["ids"] = JsonSerializer.SerializeToElement<string[]?>(null),
            }, ct);
        return result.List;
    }

    public async Task<string> ConvertImageAsync(string blobId, uint width, uint height,
        string mimeType = "image/png", CancellationToken ct = default)
    {
        var createId = "t0";
        var result = await CallAsync(BlobExtUsing, "Blob/convert", new
        {
            accountId = _accountId,
            create = new Dictionary<string, object>
            {
                [createId] = new
                {
                    imageConvert = new { blobId, width, height, type = mimeType, autoOrient = true },
                },
            },
        }, ct);

        var response = result.Deserialize<BlobUploadResponse>(JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse Blob/convert response");

        if (response.NotCreated != null && response.NotCreated.TryGetValue(createId, out var err))
            throw new InvalidOperationException($"Blob/convert failed: {err.Type} — {err.Description}");

        if (response.Created == null || !response.Created.TryGetValue(createId, out var created))
            throw new InvalidOperationException("Blob/convert returned no result");

        return created.Id;
    }

    public async Task<Dictionary<string, string>> ConvertImagesAsync(
        IReadOnlyList<(string BlobId, uint Width, uint Height)> items,
        string mimeType = "image/png", CancellationToken ct = default)
    {
        if (items.Count == 0)
            return new Dictionary<string, string>();

        var maxPerRequest = Session.MaxObjectsInSet;
        var allConverted = new Dictionary<string, string>();

        for (int offset = 0; offset < items.Count; offset += maxPerRequest)
        {
            var chunk = items.Skip(offset).Take(maxPerRequest).ToList();
            var create = new Dictionary<string, object>();
            var idToBlobId = new Dictionary<string, string>();
            for (int i = 0; i < chunk.Count; i++)
            {
                var createId = $"t{i}";
                var (blobId, width, height) = chunk[i];
                create[createId] = new
                {
                    imageConvert = new { blobId, width, height, type = mimeType, autoOrient = true },
                };
                idToBlobId[createId] = blobId;
            }

            var result = await CallAsync(BlobExtUsing, "Blob/convert", new
            {
                accountId = _accountId,
                create,
            }, ct);

            var response = result.Deserialize<BlobUploadResponse>(JmapSerializerOptions.Default)
                ?? throw new InvalidOperationException("Failed to parse Blob/convert response");

            if (response.Created != null)
            {
                foreach (var (createId, item) in response.Created)
                {
                    if (idToBlobId.TryGetValue(createId, out var blobId))
                        allConverted[blobId] = item.Id;
                }
            }
        }
        return allConverted;
    }

    /// <summary>
    /// Direct HTTP Write: PUT to webWriteUrlTemplate/{id} to replace file content.
    /// Only suitable for files under ~16 MB. Returns the new blobId, size, and type.
    /// </summary>
    public async Task<(string BlobId, long Size, string Type)> DirectWriteAsync(
        string nodeId, Stream data, string contentType, CancellationToken ct = default)
    {
        var template = WebWriteUrlTemplate
            ?? throw new InvalidOperationException("Server does not support direct HTTP write");
        var url = template.Replace("{id}", Uri.EscapeDataString(nodeId));
        var content = new StreamContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<DirectWriteResponse>(json, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse direct write response");
        return (result.BlobId, result.Size, result.Type);
    }

    /// <summary>
    /// AccountScopedJmapClient does not own the parent's HttpClient.
    /// Disposes the flush timer and best-effort flushes pending accessed timestamps.
    /// </summary>
    public void Dispose()
    {
        _accessedFlushTimer?.Dispose();
        // Best-effort flush
        try { FlushPendingAccessedAsync().GetAwaiter().GetResult(); } catch { }
    }
}
