using System.Runtime.CompilerServices;
using System.Text.Json;
using FilesClient.Jmap.Auth;
using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public class JmapClient : IJmapClient
{
    private readonly HttpClient _http;
    private JmapSession? _session;
    private JmapContext? _context;
    private int _nextCallId;

    public const string CoreCapability = "urn:ietf:params:jmap:core";
    public const string FileNodeCapability = "https://www.fastmail.com/dev/filenode";
    public const string BlobCapability = "urn:ietf:params:jmap:blob";
    private static readonly string[] FileNodeUsing = [CoreCapability, FileNodeCapability];
    private static readonly string[] BlobUsing = [CoreCapability, BlobCapability];
    /// <summary>Properties to request in FileNode/get calls — includes myRights for permission enforcement.</summary>
    internal static readonly string[] FileNodeProperties =
        ["id", "parentId", "blobId", "name", "type", "size", "created", "modified", "role", "myRights"];
    private static readonly HashSet<string> SupportedDigests = ["sha", "sha-256"];
    private string? _preferredDigestAlgorithm;
    private bool _preferredDigestResolved;

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

    public JmapClient(string token, bool debug = false)
    {
        HttpMessageHandler handler = new TokenAuth(token);
        if (debug)
        {
            Console.Error.WriteLine("[JMAP] Debug logging enabled");
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

    public async Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        var changesCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var createdCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var updatedCallId = "c" + Interlocked.Increment(ref _nextCallId);

        var request = JmapRequest.Create(FileNodeUsing,
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
            }, updatedCallId));

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

        return (changes, created.List, updated.List);
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

    public async Task<(FileNode[] Nodes, string State)> GetFileNodesByIdsPagedAsync(string[] ids, int pageSize = 1024, CancellationToken ct = default)
    {
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
        var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var upload = JsonSerializer.Deserialize<UploadResponse>(json, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse upload response");
        return upload.BlobId;
    }

    public async Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, string? onExists = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["accountId"] = AccountId,
            ["create"] = new Dictionary<string, object>
            {
                ["c0"] = new { parentId, blobId, name, type },
            },
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

    public async Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default)
    {
        // Content (blobId) is immutable — create a replacement with onExists:"replace"
        // so the server atomically replaces the existing node.
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = AccountId,
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
        catch (JsonException) { }
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
