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
        _http = new HttpClient(handler);
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

    public async Task<FileNode[]> GetFileNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<FileNode>>(
            FileNodeUsing, "FileNode/get", new { accountId = AccountId, ids }, ct);
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
            }, createdCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = changesCallId, name = "FileNode/changes", path = "/updated" },
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

    public async Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = AccountId,
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
        // Content is immutable — destroy old node and create replacement atomically.
        var setResponse = await CallAsync<SetResponse>(
            FileNodeUsing, "FileNode/set", new
            {
                accountId = AccountId,
                onDestroyRemoveChildren = true,
                destroy = new[] { nodeId },
                create = new Dictionary<string, object>
                {
                    ["c0"] = new { parentId, blobId, name, type },
                },
            }, ct);

        if (setResponse.NotDestroyed != null && setResponse.NotDestroyed.TryGetValue(nodeId, out var destroyError))
            throw new InvalidOperationException($"FileNode/set destroy failed: {destroyError.Type} — {destroyError.Description}");
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
                accountId = AccountId,
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
                accountId = AccountId,
                onDestroyRemoveChildren = true,
                destroy = new[] { nodeId },
            }, ct);

        if (setResponse.NotDestroyed != null && setResponse.NotDestroyed.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set destroy failed: {setError.Type} — {setError.Description}");
    }

    public async IAsyncEnumerable<string> WatchForChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
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
                    var newState = ParseStateChangeData(dataBuffer);
                    if (newState != null)
                        yield return newState;
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

    private string? ParseStateChangeData(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("changed", out var changed) &&
                changed.TryGetProperty(AccountId, out var account) &&
                account.TryGetProperty("FileNode", out var state))
            {
                return state.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — skip
        }
        return null;
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

    public void Dispose()
    {
        _http.Dispose();
    }
}
