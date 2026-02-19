using System.Runtime.CompilerServices;
using System.Text.Json;
using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

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

    private HttpClient Http => _parent.Http;
    private JmapSession Session => _parent.Session;

    private static readonly string[] FileNodeUsing = [JmapClient.CoreCapability, JmapClient.FileNodeCapability];
    private static readonly string[] BlobUsing = [JmapClient.CoreCapability, JmapClient.BlobCapability];

    private string NextCallId() => "c" + Interlocked.Increment(ref _parent.NextCallIdRef);

    private async Task<JsonElement> CallAsync(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var callId = NextCallId();
        var request = JmapRequest.Create(capabilities, (method, args, callId));
        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await Http.PostAsync(Session.ApiUrl, content, ct);
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
                filter = new { hasRole = "home" },
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

    public async Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        var changesCallId = NextCallId();
        var createdCallId = NextCallId();
        var updatedCallId = NextCallId();

        var request = JmapRequest.Create(FileNodeUsing,
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
            }, updatedCallId));

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

        return (changes, created.List, updated.List);
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

    public async Task<(FileNode[] Nodes, string State)> GetFileNodesByIdsPagedAsync(string[] ids, int pageSize = 1024, CancellationToken ct = default)
    {
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
        var response = await Http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var upload = JsonSerializer.Deserialize<UploadResponse>(json, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse upload response");
        return upload.BlobId;
    }

    public async Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = _accountId,
                create = new Dictionary<string, object>
                {
                    ["c0"] = new { parentId, blobId, name, type },
                },
            }, ct);

        if (setResponse.NotCreated != null && setResponse.NotCreated.TryGetValue("c0", out var setError))
            throw new InvalidOperationException($"FileNode/set create failed: {setError.Type} — {setError.Description}");

        if (setResponse.Created == null || !setResponse.Created.TryGetValue("c0", out var created))
            throw new InvalidOperationException("FileNode/set create returned no result");

        return created;
    }

    public async Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default)
    {
        // Content (blobId) is immutable — create a replacement with onExists:"replace"
        // so the server atomically replaces the existing node.
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = _accountId,
                onExists = "replace",
                create = new Dictionary<string, object>
                {
                    ["c0"] = new { parentId, blobId, name, type },
                },
            }, ct);

        if (setResponse.NotCreated != null && setResponse.NotCreated.TryGetValue("c0", out var createError))
            throw new InvalidOperationException($"FileNode/set create failed: {createError.Type} — {createError.Description}");

        if (setResponse.Created == null || !setResponse.Created.TryGetValue("c0", out var created))
            throw new InvalidOperationException("FileNode/set create returned no result");

        return created;
    }

    public async Task MoveFileNodeAsync(string nodeId, string parentId, string newName, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = _accountId,
                update = new Dictionary<string, object>
                {
                    [nodeId] = new { parentId, name = newName },
                },
            }, ct);

        if (setResponse.NotUpdated != null && setResponse.NotUpdated.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set move failed: {setError.Type} — {setError.Description}");
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

    /// <summary>
    /// AccountScopedJmapClient does not own the parent's HttpClient.
    /// Dispose is a no-op.
    /// </summary>
    public void Dispose() { }
}
