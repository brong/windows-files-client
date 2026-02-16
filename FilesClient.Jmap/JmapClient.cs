using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FilesClient.Jmap.Auth;
using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public class JmapClient : IJmapClient
{
    private readonly HttpClient _http;
    private JmapSession? _session;
    private int _nextCallId;
    private readonly ConcurrentQueue<PendingCall> _pendingCalls = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);

    private sealed record PendingCall(
        string[] Capabilities,
        string Method,
        object Args,
        string CallId,
        TaskCompletionSource<JsonElement> Tcs);

    // Fastmail uses "https://www.fastmailusercontent.com/jmap/api/" but we
    // discover it via the session resource.
    public const string CoreCapability = "urn:ietf:params:jmap:core";
    public const string StorageNodeCapability = "https://www.fastmail.com/dev/files";
    public const string BlobCapability = "urn:ietf:params:jmap:blob";
    private static readonly string[] StorageNodeUsing = [CoreCapability, StorageNodeCapability];
    private static readonly string[] BlobUsing = [CoreCapability, BlobCapability];
    private static readonly HashSet<string> SupportedDigests = ["sha", "sha-256"];
    private string? _preferredDigestAlgorithm;
    private bool _preferredDigestResolved;

    public JmapSession Session => _session
        ?? throw new InvalidOperationException("Session not initialised — call ConnectAsync first");

    public string AccountId => Session.GetPrimaryAccount(StorageNodeCapability);
    public string Username => Session.Username;

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
    }

    private async Task<JsonElement> CallAsync(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var callId = "c" + Interlocked.Increment(ref _nextCallId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctr = ct.Register(() => tcs.TrySetCanceled(ct));

        _pendingCalls.Enqueue(new PendingCall(capabilities, method, args, callId, tcs));

        await _batchLock.WaitAsync(ct);
        try
        {
            if (!tcs.Task.IsCompleted)
            {
                // We are the batch leader — drain the queue
                var batch = new List<PendingCall>();
                while (_pendingCalls.TryDequeue(out var pending))
                {
                    if (!pending.Tcs.Task.IsCompleted)
                        batch.Add(pending);
                }
                if (batch.Count > 0)
                    await ExecuteBatchAsync(batch);
            }
        }
        finally
        {
            _batchLock.Release();
        }

        ctr.Dispose();
        return await tcs.Task;
    }

    private async Task<T> CallAsync<T>(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var result = await CallAsync(capabilities, method, args, ct);
        return result.Deserialize<T>(JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize {method} response");
    }

    private async Task ExecuteBatchAsync(List<PendingCall> batch)
    {
        try
        {
            // Merge capabilities from all batch items
            var allCapabilities = new HashSet<string>();
            var calls = new (string method, object args, string callId)[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                foreach (var cap in batch[i].Capabilities)
                    allCapabilities.Add(cap);
                calls[i] = (batch[i].Method, batch[i].Args, batch[i].CallId);
            }

            var request = JmapRequest.Create(allCapabilities.ToArray(), calls);
            var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var httpResponse = await _http.PostAsync(Session.ApiUrl, content);
            httpResponse.EnsureSuccessStatusCode();
            var responseJson = await httpResponse.Content.ReadAsStringAsync();
            var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
                ?? throw new InvalidOperationException("Failed to parse JMAP response");

            // Build lookup by call ID
            var responseMap = new Dictionary<string, (string method, JsonElement args)>();
            foreach (var entry in response.MethodResponses)
            {
                var respCallId = entry[2].GetString() ?? "";
                var respMethod = entry[0].GetString() ?? "";
                responseMap[respCallId] = (respMethod, entry[1]);
            }

            // Route responses to pending calls
            foreach (var pending in batch)
            {
                if (!responseMap.TryGetValue(pending.CallId, out var resp))
                {
                    pending.Tcs.TrySetException(
                        new InvalidOperationException($"No response for call ID {pending.CallId}"));
                }
                else if (resp.method == "error")
                {
                    pending.Tcs.TrySetException(
                        new InvalidOperationException($"JMAP error: {resp.args}"));
                }
                else if (resp.method != pending.Method)
                {
                    pending.Tcs.TrySetException(
                        new InvalidOperationException(
                            $"JMAP method mismatch: expected {pending.Method}, got {resp.method}"));
                }
                else
                {
                    pending.Tcs.TrySetResult(resp.args);
                }
            }
        }
        catch (Exception ex)
        {
            // If the HTTP call itself fails, set exception on all pending calls
            foreach (var pending in batch)
                pending.Tcs.TrySetException(ex);
        }
    }

    public async Task<StorageNode[]> GetStorageNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<StorageNode>>(
            StorageNodeUsing, "StorageNode/get", new { accountId = AccountId, ids }, ct);
        return result.List;
    }

    public async Task<string[]> QueryChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var result = await CallAsync<QueryResponse>(
            StorageNodeUsing, "StorageNode/query", new
            {
                accountId = AccountId,
                filter = new { parentId },
                sort = new[] { new { property = "name", isAscending = true } },
            }, ct);
        return result.Ids;
    }

    public async Task<StorageNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var ids = await QueryChildrenAsync(parentId, ct);
        if (ids.Length == 0)
            return [];
        return await GetStorageNodesAsync(ids, ct);
    }

    public async Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default)
    {
        return await CallAsync<ChangesResponse>(
            StorageNodeUsing, "StorageNode/changes", new { accountId = AccountId, sinceState }, ct);
    }

    public async Task<(ChangesResponse Changes, StorageNode[] Created, StorageNode[] Updated)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        var changesCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var createdCallId = "c" + Interlocked.Increment(ref _nextCallId);
        var updatedCallId = "c" + Interlocked.Increment(ref _nextCallId);

        var request = JmapRequest.Create(StorageNodeUsing,
            ("StorageNode/changes", new { accountId = AccountId, sinceState }, changesCallId),
            ("StorageNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = changesCallId, name = "StorageNode/changes", path = "/created" },
            }, createdCallId),
            ("StorageNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = changesCallId, name = "StorageNode/changes", path = "/updated" },
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

        var changes = GetValidatedResult<ChangesResponse>(responseMap, changesCallId, "StorageNode/changes");
        var created = GetValidatedResult<GetResponse<StorageNode>>(responseMap, createdCallId, "StorageNode/get");
        var updated = GetValidatedResult<GetResponse<StorageNode>>(responseMap, updatedCallId, "StorageNode/get");

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

    public async Task<string> GetStateAsync(CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<StorageNode>>(
            StorageNodeUsing, "StorageNode/get", new { accountId = AccountId, ids = new[] { "root" } }, ct);
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

    public async Task<StorageNode> CreateStorageNodeAsync(string parentId, string? blobId, string name, string? type = null, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            StorageNodeUsing, "StorageNode/set", new
            {
                accountId = AccountId,
                create = new Dictionary<string, object>
                {
                    ["c0"] = new { parentId, blobId, name, type },
                },
            }, ct);

        if (setResponse.NotCreated != null && setResponse.NotCreated.TryGetValue("c0", out var setError))
            throw new InvalidOperationException($"StorageNode/set create failed: {setError.Type} — {setError.Description}");

        if (setResponse.Created == null || !setResponse.Created.TryGetValue("c0", out var created))
            throw new InvalidOperationException("StorageNode/set create returned no result");

        return created;
    }

    public async Task<StorageNode> ReplaceStorageNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default)
    {
        // Content is immutable — destroy old node and create replacement atomically.
        // Fastmail processes destroys before creates, so no name collision.
        var setResponse = await CallAsync<SetResponse>(
            StorageNodeUsing, "StorageNode/set", new
            {
                accountId = AccountId,
                destroy = new[] { nodeId },
                create = new Dictionary<string, object>
                {
                    ["c0"] = new { parentId, blobId, name, type },
                },
            }, ct);

        if (setResponse.NotDestroyed != null && setResponse.NotDestroyed.TryGetValue(nodeId, out var destroyError))
            throw new InvalidOperationException($"StorageNode/set destroy failed: {destroyError.Type} — {destroyError.Description}");
        if (setResponse.NotCreated != null && setResponse.NotCreated.TryGetValue("c0", out var createError))
            throw new InvalidOperationException($"StorageNode/set create failed: {createError.Type} — {createError.Description}");

        if (setResponse.Created == null || !setResponse.Created.TryGetValue("c0", out var created))
            throw new InvalidOperationException("StorageNode/set create returned no result");

        return created;
    }

    public async Task MoveStorageNodeAsync(string nodeId, string parentId, string newName, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            StorageNodeUsing, "StorageNode/set", new
            {
                accountId = AccountId,
                update = new Dictionary<string, object>
                {
                    [nodeId] = new { parentId, name = newName },
                },
            }, ct);

        if (setResponse.NotUpdated != null && setResponse.NotUpdated.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"StorageNode/set move failed: {setError.Type} — {setError.Description}");
    }

    public async Task DestroyStorageNodeAsync(string nodeId, CancellationToken ct = default)
    {
        var setResponse = await CallAsync<SetResponse>(
            StorageNodeUsing, "StorageNode/set", new
            {
                accountId = AccountId,
                destroy = new[] { nodeId },
            }, ct);

        if (setResponse.NotDestroyed != null && setResponse.NotDestroyed.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"StorageNode/set destroy failed: {setError.Type} — {setError.Description}");
    }

    public async IAsyncEnumerable<string> WatchForChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = Session.GetEventSourceUrl("StorageNode", "no", "60");
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
                account.TryGetProperty("StorageNode", out var state))
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
