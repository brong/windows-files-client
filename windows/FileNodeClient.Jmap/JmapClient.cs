using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FileNodeClient.Logging;
using FileNodeClient.Jmap.Auth;
using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Jmap;

public class JmapClient : IJmapClient
{
    private readonly HttpClient _http;
    private JmapSession? _session;
    private JmapContext? _context;
    private int _nextCallId;

    public const string CoreCapability = "urn:ietf:params:jmap:core";
    public const string FileNodeCapability = "https://www.fastmail.com/dev/filenode";
    public const string BlobCapability = "urn:ietf:params:jmap:blob";
    public const string BlobExtCapability = "https://www.fastmail.com/dev/blobext";
    public const string QuotaCapability = "urn:ietf:params:jmap:quota";
    private static readonly string[] FileNodeUsing = [CoreCapability, FileNodeCapability];
    private static readonly string[] BlobUsing = [CoreCapability, BlobCapability];
    private static readonly string[] BlobExtUsing = [CoreCapability, BlobCapability, BlobExtCapability];
    private static readonly string[] QuotaUsing = [CoreCapability, QuotaCapability];
    /// <summary>Properties to request in FileNode/get calls — includes myRights for permission enforcement.</summary>
    internal static readonly string[] FileNodeProperties =
        ["id", "parentId", "blobId", "name", "type", "size", "created", "modified", "role", "myRights", "shareWith"];
    private static readonly HashSet<string> SupportedDigests = ["sha", "sha-256"];
    private string? _preferredDigestAlgorithm;
    private bool _preferredDigestResolved;
    private long? _chunkSize;
    private bool _chunkSizeResolved;
    private int? _maxDataSources;
    private bool _maxDataSourcesResolved;
    private long? _maxSizeBlobSet;
    private bool _maxSizeBlobSetResolved;
    private string? _trashUrl;
    private bool _trashUrlResolved;
    private string? _webUrlTemplate;
    private bool _webUrlTemplateResolved;

    public JmapSession Session => _session
        ?? throw new InvalidOperationException("Session not initialised — call ConnectAsync first");

    public JmapContext Context => _context
        ?? throw new InvalidOperationException("Context not initialised — call ConnectAsync first");

    public string AccountId => Context.AccountId;
    public string Username => Context.Username;

    public string? PreferredDigestAlgorithm
    {
        get
        {
            if (!_preferredDigestResolved)
            {
                var algos = Session.GetSupportedDigestAlgorithms(AccountId);
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
                _chunkSize = Session.GetChunkSize(AccountId);
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
                _maxDataSources = Session.GetMaxDataSources(AccountId);
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
                _maxSizeBlobSet = Session.GetMaxSizeBlobSet(AccountId);
                _maxSizeBlobSetResolved = true;
            }
            return _maxSizeBlobSet;
        }
    }

    public bool HasBlobConvert => ChunkSize != null;

    public string? TrashUrl
    {
        get
        {
            if (!_trashUrlResolved)
            {
                _trashUrl = Session.GetTrashUrl(AccountId);
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
                _webUrlTemplate = Session.GetWebUrlTemplate(AccountId);
                _webUrlTemplateResolved = true;
            }
            return _webUrlTemplate;
        }
    }

    public JmapClient(string token, bool debug = false)
        : this(new TokenAuth(token), debug) { }

    public JmapClient(HttpMessageHandler handler, bool debug = false)
    {
        if (debug)
        {
            Log.Debug("[JMAP] Debug logging enabled");
            handler = new DebugLoggingHandler(handler);
        }
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task ConnectAsync(string sessionUrl, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(sessionUrl, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        _session = JsonSerializer.Deserialize<JmapSession>(json)
            ?? throw new InvalidOperationException("Failed to parse JMAP session");

        var accountId = _session.GetPrimaryAccount(FileNodeCapability);
        _context = new JmapContext(_session.Username, accountId);
    }

    private async Task<JsonElement> CallAsync(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var callId = "c" + Interlocked.Increment(ref _nextCallId);
        var request = JmapRequest.Create(capabilities, (method, args, callId));
        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await _http.PostAsync(Session.ApiUrl, content, ct);
        httpResponse.EnsureSuccessStatusCode();
        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");

        if (response.MethodResponses.Length == 0)
            throw new InvalidOperationException($"No response for {method} call");

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

    public async Task<string> FindHomeNodeIdAsync(CancellationToken ct = default)
    {
        var queryCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var getCallId = "c" + Interlocked.Increment(ref _nextCallId);

        var request = JmapRequest.Create(FileNodeUsing,
            ("FileNode/query", new
            {
                accountId = AccountId,
                filter = new { hasRole = "home" },
            }, queryCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = queryCallId, name = "FileNode/query", path = "/ids" },
            }, getCallId));

        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await _http.PostAsync(Session.ApiUrl, content, ct);
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
        var queryCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var getCallId = "c" + Interlocked.Increment(ref _nextCallId);

        var request = JmapRequest.Create(FileNodeUsing,
            ("FileNode/query", new
            {
                accountId = AccountId,
                filter = new { hasRole = "trash" },
            }, queryCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = queryCallId, name = "FileNode/query", path = "/ids" },
            }, getCallId));

        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await _http.PostAsync(Session.ApiUrl, content, ct);
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
            FileNodeUsing, "FileNode/get", new { accountId = AccountId, ids, properties = FileNodeProperties }, ct);
        return result.List;
    }

    public async Task<FileNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var queryCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var getCallId = "c" + Interlocked.Increment(ref _nextCallId);

        var request = JmapRequest.Create(FileNodeUsing,
            ("FileNode/query", new
            {
                accountId = AccountId,
                filter = new { parentId },
                sort = new[] { new { property = "name", isAscending = true } },
            }, queryCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = queryCallId, name = "FileNode/query", path = "/ids" },
                ["properties"] = FileNodeProperties,
            }, getCallId));

        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await _http.PostAsync(Session.ApiUrl, content, ct);
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
            FileNodeUsing, "FileNode/changes", new { accountId = AccountId, sinceState }, ct);
    }

    public async Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated, Quota[]? Quotas)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        var changesCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var createdCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var updatedCallId = "c" + Interlocked.Increment(ref _nextCallId);

        var calls = new List<(string method, object args, string callId)>
        {
            ("FileNode/changes", new { accountId = AccountId, sinceState }, changesCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = changesCallId, name = "FileNode/changes", path = "/created" },
                ["properties"] = FileNodeProperties,
            }, createdCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = changesCallId, name = "FileNode/changes", path = "/updated" },
                ["properties"] = FileNodeProperties,
            }, updatedCallId),
        };

        // Batch Quota/get into the same request when the capability is available
        string? quotaCallId = null;
        string[] capabilities = FileNodeUsing;
        if (Session.HasCapability(QuotaCapability))
        {
            quotaCallId = "c" + Interlocked.Increment(ref _nextCallId);
            capabilities = [CoreCapability, FileNodeCapability, QuotaCapability];
            calls.Add(("Quota/get", new Dictionary<string, JsonElement>
            {
                ["accountId"] = JsonSerializer.SerializeToElement(AccountId),
                ["ids"] = JsonSerializer.SerializeToElement<string[]?>(null),
            }, quotaCallId));
        }

        var request = JmapRequest.Create(capabilities, calls.ToArray());
        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await _http.PostAsync(Session.ApiUrl, content, ct);
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

    public async Task<string> GetStateAsync(string homeNodeId, CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<FileNode>>(
            FileNodeUsing, "FileNode/get", new { accountId = AccountId, ids = new[] { homeNodeId } }, ct);
        return result.State;
    }

    public async Task<string> GetCurrentStateAsync(CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<FileNode>>(
            FileNodeUsing, "FileNode/get", new { accountId = AccountId, ids = Array.Empty<string>() }, ct);
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
                FileNodeUsing, "FileNode/query", new { accountId = AccountId, position, limit }, ct);

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
                FileNodeUsing, "FileNode/get", new { accountId = AccountId, ids = chunk, properties = FileNodeProperties }, ct);
            allNodes.AddRange(result.List);
            state = result.State;
        }

        return (allNodes.ToArray(), state);
    }

    public async Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default)
    {
        var url = Session.GetDownloadUrl(AccountId, blobId, type, name);
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default)
    {
        var url = Session.GetDownloadUrl(AccountId, blobId, type, name);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        bool isPartial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        return (stream, isPartial);
    }

    public async Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default)
    {
        var url = Session.GetUploadUrl(AccountId);
        var content = new StreamContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content, Version = System.Net.HttpVersion.Version11 };
        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var upload = JsonSerializer.Deserialize<UploadResponse>(json, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse upload response");
        return upload.BlobId;
    }

    /// <summary>
    /// Represents a previously uploaded chunk that can be reused on resume.
    /// </summary>
    public record UploadedChunkInfo(string BlobId, string Sha1Base64, long Offset, int Length);

    internal const long MinChunkSize = 1_048_576; // 1 MB minimum
    internal const long MaxChunkSize = 67_108_864; // 64 MB maximum

    public Task<string> UploadBlobChunkedAsync(Stream data, string contentType, long totalSize,
        Action<int>? onProgress = null, Action<UploadedChunkInfo>? onChunkUploaded = null,
        List<UploadedChunkInfo>? previousChunks = null,
        CancellationToken ct = default)
    {
        var baseChunkSize = ChunkSize ?? throw new InvalidOperationException("ChunkSize not available");

        // Reject files that exceed the server's max combined blob size
        var maxSize = MaxSizeBlobSet;
        if (maxSize.HasValue && totalSize > maxSize.Value)
            throw new InvalidOperationException(
                $"File size {totalSize} exceeds server maxSizeBlobSet {maxSize.Value}");

        // Start with the server's chunk size (already a power of 2), enforce floor of 1 MB
        var effectiveChunkSize = Math.Max(baseChunkSize, MinChunkSize);

        // If maxDataSources limits how many chunks we can combine, keep doubling
        // until the file fits within maxDataSources chunks.
        var maxSources = MaxDataSources;
        if (maxSources.HasValue && maxSources.Value > 0 && totalSize > 0)
        {
            while ((totalSize + effectiveChunkSize - 1) / effectiveChunkSize > maxSources.Value
                   && effectiveChunkSize < MaxChunkSize)
                effectiveChunkSize *= 2;
        }

        if (effectiveChunkSize > MaxChunkSize)
            effectiveChunkSize = MaxChunkSize;

        return UploadBlobChunkedInternalAsync(_http, Session.GetUploadUrl(AccountId), AccountId,
            effectiveChunkSize,
            (caps, method, args) => CallAsync(caps, method, args, ct),
            data, contentType, totalSize, onProgress, onChunkUploaded, previousChunks, ct);
    }

    internal static async Task<string> UploadBlobChunkedInternalAsync(
        HttpClient http, string uploadUrl, string accountId, long chunkSize,
        Func<string[], string, object, Task<JsonElement>> callAsync,
        Stream data, string contentType, long totalSize,
        Action<int>? onProgress, Action<UploadedChunkInfo>? onChunkUploaded,
        List<UploadedChunkInfo>? previousChunks,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent((int)chunkSize);
        try
        {
            var chunkBlobIds = new List<(string BlobId, string Sha1Base64)>();
            using var overallHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long totalUploaded = 0;

            // Restore previously uploaded chunks — verify they still exist on
            // the server via Blob/get, then skip their bytes (just hash for the
            // overall digest) and add their blobIds to the combine list.
            if (previousChunks != null && previousChunks.Count > 0)
            {
                // Verify chunk blobIds still exist on server
                var blobIds = previousChunks.Select(c => c.BlobId).ToArray();
                int validCount = previousChunks.Count;
                try
                {
                    var blobCheck = await callAsync(BlobUsing, "Blob/get", new
                    {
                        accountId,
                        ids = blobIds,
                        properties = new[] { "id", "size" },
                    });
                    var blobResponse = blobCheck.Deserialize<BlobGetResponse>(JmapSerializerOptions.Default);
                    if (blobResponse != null)
                    {
                        var notFound = new HashSet<string>(blobResponse.NotFound);
                        if (notFound.Count > 0)
                        {
                            // Find the first expired chunk — discard it and all subsequent
                            validCount = 0;
                            for (int i = 0; i < previousChunks.Count; i++)
                            {
                                if (notFound.Contains(previousChunks[i].BlobId))
                                    break;
                                validCount = i + 1;
                            }
                        }
                    }
                }
                catch
                {
                    // Blob/get failed — start fresh to be safe
                    validCount = 0;
                }

                if (validCount == 0)
                {
                    // All chunks expired — start from scratch
                    data.Position = 0;
                    return await UploadBlobChunkedInternalAsync(http, uploadUrl, accountId,
                        chunkSize, callAsync, data, contentType, totalSize,
                        onProgress, onChunkUploaded, null, ct);
                }

                for (int i = 0; i < validCount; i++)
                {
                    var prev = previousChunks[i];
                    // Read and hash the chunk data (needed for overall SHA1)
                    // but don't re-upload it
                    var toRead = prev.Length;
                    var offset = 0;
                    while (offset < toRead)
                    {
                        var read = await data.ReadAsync(buffer.AsMemory(offset, toRead - offset), ct);
                        if (read == 0)
                            break;
                        offset += read;
                    }

                    // Verify the chunk data still matches (file hasn't changed)
                    var chunkSpan = buffer.AsSpan(0, offset);
                    var chunkSha1 = SHA1.HashData(chunkSpan);
                    var chunkSha1Base64 = Convert.ToBase64String(chunkSha1);

                    if (chunkSha1Base64 != prev.Sha1Base64)
                    {
                        // File has changed since chunks were uploaded — start over
                        data.Position = 0;
                        return await UploadBlobChunkedInternalAsync(http, uploadUrl, accountId,
                            chunkSize, callAsync, data, contentType, totalSize,
                            onProgress, onChunkUploaded, null, ct);
                    }

                    overallHash.AppendData(chunkSpan);
                    chunkBlobIds.Add((prev.BlobId, prev.Sha1Base64));
                    totalUploaded += offset;
                }
                onProgress?.Invoke((int)(totalUploaded * 100 / totalSize));
            }

            while (totalUploaded < totalSize)
            {
                // Read one full chunk (or remainder)
                var toRead = (int)Math.Min(chunkSize, totalSize - totalUploaded);
                var offset = 0;
                while (offset < toRead)
                {
                    var read = await data.ReadAsync(buffer.AsMemory(offset, toRead - offset), ct);
                    if (read == 0)
                        break;
                    offset += read;
                }

                var chunkSpan = buffer.AsSpan(0, offset);
                var chunkSha1 = SHA1.HashData(chunkSpan);
                overallHash.AppendData(chunkSpan);
                var chunkSha1Base64 = Convert.ToBase64String(chunkSha1);

                // Upload chunk via HTTP POST (chunks are raw bytes, not the final content type).
                // Force HTTP/1.1 so each upload gets its own TCP connection and doesn't
                // starve interactive downloads via HTTP/2 multiplexing contention.
                using var chunkStream = new MemoryStream(buffer, 0, offset, writable: false);
                var chunkContent = new StreamContent(chunkStream);
                chunkContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var chunkRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = chunkContent, Version = System.Net.HttpVersion.Version11 };
                var response = await http.SendAsync(chunkRequest, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                var upload = JsonSerializer.Deserialize<UploadResponse>(json, JmapSerializerOptions.Default)
                    ?? throw new InvalidOperationException("Failed to parse chunk upload response");

                chunkBlobIds.Add((upload.BlobId, chunkSha1Base64));
                totalUploaded += offset;
                onProgress?.Invoke((int)(totalUploaded * 100 / totalSize));
                onChunkUploaded?.Invoke(new UploadedChunkInfo(upload.BlobId, chunkSha1Base64, totalUploaded - offset, offset));

                // Yield between chunks so interactive work (downloads) can proceed.
                // Without this, rapid small-chunk uploads can starve the async scheduler.
                await Task.Yield();
            }

            // Compute overall SHA1
            var overallSha1 = overallHash.GetHashAndReset();
            var overallSha1Base64 = Convert.ToBase64String(overallSha1);

            // Combine chunks via Blob/upload
            var dataArray = chunkBlobIds.Select(c => new Dictionary<string, object?>
            {
                ["blobId"] = c.BlobId,
                ["digest:sha"] = c.Sha1Base64,
            }).ToArray();

            var createId = Guid.NewGuid().ToString("N")[..12];
            var createItem = new Dictionary<string, object>
            {
                ["data"] = dataArray,
                ["type"] = contentType,
                ["digest:sha"] = overallSha1Base64,
            };

            var result = await callAsync(BlobExtUsing, "Blob/upload", new
            {
                accountId,
                create = new Dictionary<string, object> { [createId] = createItem },
            });

            var blobUpload = result.Deserialize<BlobUploadResponse>(JmapSerializerOptions.Default)
                ?? throw new InvalidOperationException("Failed to parse Blob/upload response");

            if (blobUpload.NotCreated != null && blobUpload.NotCreated.TryGetValue(createId, out var err))
                throw new InvalidOperationException($"Blob/upload failed: {err.Type} — {err.Description}");

            if (blobUpload.Created == null || !blobUpload.Created.TryGetValue(createId, out var created))
                throw new InvalidOperationException("Blob/upload returned no result");

            return created.Id;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
            ["accountId"] = AccountId,
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
        // Content (blobId) is immutable — create a replacement with onExists:"replace"
        // so the server atomically replaces the existing node.
        var createObj = new Dictionary<string, object?>
        {
            ["parentId"] = parentId, ["blobId"] = blobId, ["name"] = name, ["type"] = type,
        };
        if (createdAt.HasValue)
            createObj["created"] = createdAt.Value.ToUniversalTime();
        if (modifiedAt.HasValue)
            createObj["modified"] = modifiedAt.Value.ToUniversalTime();

        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = AccountId,
                onExists = "replace",
                create = new Dictionary<string, object?> { ["c0"] = createObj },
            }, ct);

        if (setResponse.NotCreated != null && setResponse.NotCreated.TryGetValue("c0", out var createError))
            throw new InvalidOperationException($"FileNode/set create failed: {createError.Type} — {createError.Description}");

        if (setResponse.Created == null || !setResponse.Created.TryGetValue("c0", out var created))
            throw new InvalidOperationException("FileNode/set create returned no result");

        return created;
    }

    public async Task MoveFileNodeAsync(string nodeId, string parentId, string newName, string? onExists = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object>
        {
            ["accountId"] = AccountId,
            ["update"] = new Dictionary<string, object>
            {
                [nodeId] = new { parentId, name = newName },
            },
        };
        if (onExists != null)
            args["onExists"] = onExists;

        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", args, ct);

        if (setResponse.NotUpdated != null && setResponse.NotUpdated.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set move failed: {setError.Type} — {setError.Description}");
    }

    public async Task DestroyFileNodeAsync(string nodeId, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = AccountId,
                onDestroyRemoveChildren = true,
                destroy = new[] { nodeId },
            }, ct);

        if (setResponse.NotDestroyed != null && setResponse.NotDestroyed.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set destroy failed: {setError.Type} — {setError.Description}");
    }

    public async IAsyncEnumerable<string> WatchForChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (accountId, state) in WatchAllAccountChangesAsync(ct))
        {
            if (accountId == AccountId)
                yield return state;
        }
    }

    /// <summary>
    /// Single SSE connection that yields (accountId, state) for all accounts
    /// with FileNode capability in this session.
    /// </summary>
    public async IAsyncEnumerable<(string AccountId, string State)> WatchAllAccountChangesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = Session.GetEventSourceUrl("FileNode", "no", "60");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? eventType = null;
        string? dataBuffer = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
                break; // Stream ended

            if (line.StartsWith(':'))
                continue; // SSE comment / ping

            if (line.Length == 0)
            {
                // Blank line = end of event
                if (eventType == "state" && dataBuffer != null)
                {
                    foreach (var change in ParseAllStateChanges(dataBuffer))
                        yield return change;
                }
                eventType = null;
                dataBuffer = null;
                continue;
            }

            if (line.StartsWith("event:"))
                eventType = line.Substring(6).Trim();
            else if (line.StartsWith("data:"))
            {
                var data = line.Substring(5).Trim();
                dataBuffer = dataBuffer == null ? data : dataBuffer + "\n" + data;
            }
        }
    }

    private static List<(string AccountId, string State)> ParseAllStateChanges(string data)
    {
        var results = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("changed", out var changed))
            {
                foreach (var account in changed.EnumerateObject())
                {
                    if (account.Value.TryGetProperty("FileNode", out var state))
                    {
                        var s = state.GetString();
                        if (s != null)
                            results.Add((account.Name, s));
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"Failed to parse SSE state change: {ex.Message}");
        }
        return results;
    }

    public async Task<BlobDataItem> GetBlobAsync(string blobId, string[] properties,
        long? offset = null, long? length = null, CancellationToken ct = default)
    {
        var blobArgs = new Dictionary<string, object?>
        {
            ["accountId"] = AccountId,
            ["ids"] = new[] { blobId },
            ["properties"] = properties,
        };

        if (offset.HasValue)
            blobArgs["offset"] = offset.Value;
        if (length.HasValue)
            blobArgs["length"] = length.Value;

        var blobResponse = await CallAsync<BlobGetResponse>(BlobUsing, "Blob/get", blobArgs, ct);
        if (blobResponse.NotFound.Length > 0)
            throw new FileNotFoundException($"Blob not found: {blobId}");
        if (blobResponse.List.Length == 0)
            throw new InvalidOperationException($"Blob/get returned no results for {blobId}");

        return blobResponse.List[0];
    }

    public async Task<Quota[]> GetQuotasAsync(CancellationToken ct = default)
    {
        if (!Session.HasCapability(QuotaCapability))
            return [];

        var result = await CallAsync<GetResponse<Quota>>(
            QuotaUsing, "Quota/get", new Dictionary<string, JsonElement>
            {
                ["accountId"] = JsonSerializer.SerializeToElement(AccountId),
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
            accountId = AccountId,
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

        // Chunk into maxObjectsInSet-sized batches
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
                accountId = AccountId,
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
    /// Returns all accounts in this session that have the FileNode capability.
    /// Each entry contains the accountId, display name, and whether it's the
    /// primary account for FileNode.
    /// </summary>
    public List<(string AccountId, string Name, bool IsPrimary)> GetFileNodeAccounts()
    {
        var primary = Session.PrimaryAccounts.GetValueOrDefault(FileNodeCapability);
        var result = new List<(string, string, bool)>();
        foreach (var (accountId, account) in Session.Accounts)
        {
            if (account.AccountCapabilities.ContainsKey(FileNodeCapability))
                result.Add((accountId, account.Name, accountId == primary));
        }
        return result;
    }

    /// <summary>
    /// Returns an <see cref="AccountScopedJmapClient"/> that shares this client's
    /// HttpClient and session but targets a different account.
    /// </summary>
    public AccountScopedJmapClient ForAccount(string accountId)
    {
        if (!Session.Accounts.ContainsKey(accountId))
            throw new ArgumentException($"Account {accountId} not found in session");
        return new AccountScopedJmapClient(this, accountId);
    }

    // Expose internals needed by AccountScopedJmapClient
    internal HttpClient Http => _http;
    internal ref int NextCallIdRef => ref _nextCallId;

    public void Dispose()
    {
        _http.Dispose();
    }
}
