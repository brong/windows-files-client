using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FileNodeClient.Logging;
using FileNodeClient.Jmap.Auth;
using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Jmap;

/// <summary>
/// JMAP FileNode client for one account. A login (HttpClient + session) may
/// serve several accounts: <see cref="ForAccount"/> returns another view over
/// the same connection targeting a different accountId.
/// </summary>
public class JmapClient : IJmapClient
{
    /// <summary>State shared by every per-account view of one login.</summary>
    private sealed class Connection(HttpClient http)
    {
        public readonly HttpClient Http = http;
        public JmapSession? Session;
        public int NextCallId;
        // Some accounts advertise the Quota capability at session level but reject
        // Quota/get at HTTP level (403 Forbidden). Once we hit that, stop including
        // Quota/get in batched calls for every account on this login.
        public volatile bool QuotaForbidden;
    }

    private readonly Connection _conn;
    private readonly bool _ownsConnection;
    private string? _accountId;
    private readonly ConcurrentDictionary<string, DateTime> _pendingAccessed = new();
    private Timer? _accessedFlushTimer;

    public const string CoreCapability = "urn:ietf:params:jmap:core";
    public const string FileNodeCapability = "https://www.fastmail.com/dev/filenode";
    // The legacy `urn:ietf:params:jmap:blob` capability is deprecated and must
    // not be sent in `using` arrays. We only reference its URI to read advertised
    // limits (maxDataSources, maxSizeBlobSet, supportedDigestAlgorithms) from the
    // session's capability metadata.
    public const string BlobCapability = "urn:ietf:params:jmap:blob";
    public const string Blob2Capability = "https://www.fastmail.com/dev/blob2";
    public const string QuotaCapability = "urn:ietf:params:jmap:quota";
    private static readonly string[] FileNodeUsing = [CoreCapability, FileNodeCapability];
    private static readonly string[] Blob2Using = [CoreCapability, Blob2Capability];
    private static readonly string[] QuotaUsing = [CoreCapability, QuotaCapability];
    /// <summary>Properties to request in FileNode/get calls — includes myRights for permission enforcement.</summary>
    internal static readonly string[] FileNodeProperties =
        ["id", "parentId", "blobId", "name", "type", "size", "created", "modified", "role", "myRights", "shareWith", "executable", "accessed", "isSubscribed"];
    private static readonly HashSet<string> SupportedDigests = ["sha", "sha-256"];

    public JmapSession Session => _conn.Session
        ?? throw new InvalidOperationException("Session not initialised — call ConnectAsync first");

    public string AccountId => _accountId
        ?? throw new InvalidOperationException("Account not initialised — call ConnectAsync first");

    public string Username => Session.Username;
    public JmapContext Context => new(Username, AccountId);

    public string? PreferredDigestAlgorithm =>
        Session.GetSupportedDigestAlgorithms(AccountId).FirstOrDefault(SupportedDigests.Contains);
    public long? ChunkSize => Session.GetChunkSize(AccountId);
    public int? MaxDataSources => Session.GetMaxDataSources(AccountId);
    public long? MaxSizeBlobSet => Session.GetMaxSizeBlobSet(AccountId);
    public bool HasBlob2 => Session.HasAccountCapability(AccountId, Blob2Capability);
    public bool HasBlobConvert => HasBlob2;
    public string? TrashUrl => Session.GetTrashUrl(AccountId);
    public string? WebUrlTemplate => Session.GetWebUrlTemplate(AccountId);
    public string? WebWriteUrlTemplate => Session.GetWebWriteUrlTemplate(AccountId);
    public bool CaseInsensitiveNames => Session.GetCaseInsensitiveNames(AccountId) ?? true;

    public JmapClient(string token, bool debug = false)
        : this(new TokenAuth(token), debug) { }

    public JmapClient(HttpMessageHandler handler, bool debug = false)
    {
        if (debug)
        {
            Log.Debug("[JMAP] Debug logging enabled");
            handler = new DebugLoggingHandler(handler);
        }
        _conn = new Connection(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        _ownsConnection = true;
    }

    private JmapClient(Connection conn, string accountId)
    {
        _conn = conn;
        _accountId = accountId;
    }

    public async Task ConnectAsync(string sessionUrl, CancellationToken ct = default)
    {
        var response = await _conn.Http.GetAsync(sessionUrl, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var session = JsonSerializer.Deserialize<JmapSession>(json)
            ?? throw new InvalidOperationException("Failed to parse JMAP session");
        _conn.Session = session;
        _accountId = session.GetPrimaryAccount(FileNodeCapability);
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
    /// Returns a client that shares this login's HttpClient and session but
    /// targets a different account.
    /// </summary>
    public JmapClient ForAccount(string accountId)
    {
        if (!Session.Accounts.ContainsKey(accountId))
            throw new ArgumentException($"Account {accountId} not found in session");
        return new JmapClient(_conn, accountId);
    }

    // ---- Request plumbing ----

    private string NextCallId() => "c" + Interlocked.Increment(ref _conn.NextCallId);

    /// <summary>Responses of one batched request, keyed by call id.</summary>
    private sealed class Batch(Dictionary<string, (string Method, JsonElement Args)> byCallId)
    {
        public bool TryGet(string callId, out (string Method, JsonElement Args) response) =>
            byCallId.TryGetValue(callId, out response);

        /// <summary>The raw result of a call, throwing if it's missing or an error.</summary>
        public JsonElement Get(string callId, string expectedMethod)
        {
            if (!byCallId.TryGetValue(callId, out var resp))
                throw new InvalidOperationException($"No response for call ID {callId}");
            if (resp.Method == "error")
                throw new InvalidOperationException($"JMAP error: {resp.Args}");
            if (resp.Method != expectedMethod)
                throw new InvalidOperationException(
                    $"JMAP method mismatch: expected {expectedMethod}, got {resp.Method}");
            return resp.Args;
        }

        public T Get<T>(string callId, string expectedMethod) =>
            Get(callId, expectedMethod).Deserialize<T>(JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize {expectedMethod} response");
    }

    /// <summary>
    /// POST one or more method calls in a single request. Pending `accessed`
    /// timestamps recorded via <see cref="RecordAccess"/> are piggybacked as an
    /// extra FileNode/set call.
    /// </summary>
    private async Task<Batch> CallBatchAsync(string[] capabilities,
        IEnumerable<(string method, object args, string callId)> calls, CancellationToken ct)
    {
        var callList = calls.ToList();
        var accessedBatch = DrainPendingAccessed();
        if (accessedBatch.Count > 0)
        {
            var accessedUpdate = new Dictionary<string, object>();
            foreach (var (nodeId, time) in accessedBatch)
                accessedUpdate[nodeId] = new { accessed = time.ToUniversalTime() };
            callList.Add(("FileNode/set", new { accountId = AccountId, update = accessedUpdate }, "_accessed"));
            if (!capabilities.Contains(FileNodeCapability))
                capabilities = [.. capabilities, FileNodeCapability];
        }

        var request = JmapRequest.Create(capabilities, callList.ToArray());
        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var httpResponse = await _conn.Http.PostAsync(Session.ApiUrl, content, ct);
        httpResponse.EnsureSuccessStatusCode();
        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var response = JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");

        var byCallId = new Dictionary<string, (string, JsonElement)>();
        foreach (var entry in response.MethodResponses)
            byCallId[entry[2].GetString() ?? ""] = (entry[0].GetString() ?? "", entry[1]);
        return new Batch(byCallId);
    }

    private async Task<JsonElement> CallAsync(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var callId = NextCallId();
        var batch = await CallBatchAsync(capabilities, [(method, args, callId)], ct);
        return batch.Get(callId, method);
    }

    private async Task<T> CallAsync<T>(string[] capabilities, string method, object args, CancellationToken ct)
    {
        var callId = NextCallId();
        var batch = await CallBatchAsync(capabilities, [(method, args, callId)], ct);
        return batch.Get<T>(callId, method);
    }

    /// <summary>FileNode/query chained into FileNode/get via a result reference.</summary>
    private async Task<FileNode[]> QueryAndGetAsync(object filter, object? sort, CancellationToken ct)
    {
        var queryCallId = NextCallId();
        var getCallId = NextCallId();
        var queryArgs = new Dictionary<string, object?> { ["accountId"] = AccountId, ["filter"] = filter };
        if (sort != null) queryArgs["sort"] = sort;

        var batch = await CallBatchAsync(FileNodeUsing,
        [
            ("FileNode/query", queryArgs, queryCallId),
            ("FileNode/get", new Dictionary<string, object>
            {
                ["accountId"] = AccountId,
                ["#ids"] = new { resultOf = queryCallId, name = "FileNode/query", path = "/ids" },
                ["properties"] = FileNodeProperties,
            }, getCallId),
        ], ct);

        batch.Get(queryCallId, "FileNode/query"); // surface query errors first
        return batch.Get<GetResponse<FileNode>>(getCallId, "FileNode/get").List;
    }

    // ---- FileNode reads ----

    public async Task<string> FindHomeNodeIdAsync(CancellationToken ct = default)
    {
        var nodes = await QueryAndGetAsync(new { role = "home" }, null, ct);
        return nodes.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("No FileNode with role 'home' found");
    }

    public async Task<string?> FindTrashNodeIdAsync(CancellationToken ct = default)
    {
        var nodes = await QueryAndGetAsync(new { role = "trash" }, null, ct);
        return nodes.FirstOrDefault()?.Id;
    }

    public async Task<FileNode[]> GetFileNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var result = await CallAsync<GetResponse<FileNode>>(
            FileNodeUsing, "FileNode/get", new { accountId = AccountId, ids, properties = FileNodeProperties }, ct);
        return result.List;
    }

    public Task<FileNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default) =>
        QueryAndGetAsync(new { parentId }, new[] { new { property = "name", isAscending = true } }, ct);

    public async Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default)
    {
        return await CallAsync<ChangesResponse>(
            FileNodeUsing, "FileNode/changes", new { accountId = AccountId, sinceState }, ct);
    }

    public async Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated, Quota[]? Quotas)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default)
    {
        // Batch Quota/get in when the server advertises the capability. Some
        // accounts still reject it with HTTP 403 — retry once without it and
        // remember so we don't repeat the wasted call.
        bool includeQuota = Session.HasCapability(QuotaCapability) && !_conn.QuotaForbidden;
        try
        {
            return await ExecuteChangesAndNodesAsync(sinceState, includeQuota, ct);
        }
        catch (HttpRequestException ex) when (includeQuota && ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Log.Info($"[JMAP] Quota/get forbidden for {AccountId}; disabling Quota batching for this session");
            _conn.QuotaForbidden = true;
            return await ExecuteChangesAndNodesAsync(sinceState, includeQuota: false, ct);
        }
    }

    private async Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated, Quota[]? Quotas)>
        ExecuteChangesAndNodesAsync(string sinceState, bool includeQuota, CancellationToken ct)
    {
        var changesCallId = NextCallId();
        var createdCallId = NextCallId();
        var updatedCallId = NextCallId();

        Dictionary<string, object> GetByRef(string path) => new()
        {
            ["accountId"] = AccountId,
            ["#ids"] = new { resultOf = changesCallId, name = "FileNode/changes", path },
            ["properties"] = FileNodeProperties,
        };

        var calls = new List<(string method, object args, string callId)>
        {
            ("FileNode/changes", new { accountId = AccountId, sinceState }, changesCallId),
            ("FileNode/get", GetByRef("/created"), createdCallId),
            ("FileNode/get", GetByRef("/updated"), updatedCallId),
        };

        string? quotaCallId = null;
        string[] capabilities = FileNodeUsing;
        if (includeQuota)
        {
            quotaCallId = NextCallId();
            capabilities = [CoreCapability, FileNodeCapability, QuotaCapability];
            calls.Add(("Quota/get", new Dictionary<string, JsonElement>
            {
                ["accountId"] = JsonSerializer.SerializeToElement(AccountId),
                ["ids"] = JsonSerializer.SerializeToElement<string[]?>(null),
            }, quotaCallId));
        }

        var batch = await CallBatchAsync(capabilities, calls, ct);

        // These feed each other via result references, so a failure in any one
        // makes the rest meaningless. Let the exception bubble.
        var changes = batch.Get<ChangesResponse>(changesCallId, "FileNode/changes");
        var created = batch.Get<GetResponse<FileNode>>(createdCallId, "FileNode/get");
        var updated = batch.Get<GetResponse<FileNode>>(updatedCallId, "FileNode/get");

        // Quota is optional: tolerate a method-level error, but stop asking.
        Quota[]? quotas = null;
        if (quotaCallId != null && batch.TryGet(quotaCallId, out var quotaResp))
        {
            if (quotaResp.Method == "Quota/get")
                quotas = quotaResp.Args.Deserialize<GetResponse<Quota>>(JmapSerializerOptions.Default)?.List;
            else
            {
                Log.Info($"[JMAP] Quota/get returned {quotaResp.Method} for {AccountId}; disabling Quota batching for this session");
                _conn.QuotaForbidden = true;
            }
        }

        return (changes, created.List, updated.List, quotas);
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
        if (ids.Length == 0) return (Array.Empty<FileNode>(), "");

        // Fan out pages concurrently. All calls share the same HttpClient
        // connection pool and capability headers; the server happily parallelizes
        // across distinct JMAP calls in separate POSTs. For a 6332-node account
        // at 1024/page this drops ~5s of sequential waits to ~1s.
        var pageCount = (ids.Length + pageSize - 1) / pageSize;
        var tasks = new Task<GetResponse<FileNode>>[pageCount];
        for (int p = 0; p < pageCount; p++)
        {
            var chunk = ids.AsSpan(p * pageSize, Math.Min(pageSize, ids.Length - p * pageSize)).ToArray();
            tasks[p] = CallAsync<GetResponse<FileNode>>(
                FileNodeUsing, "FileNode/get",
                new { accountId = AccountId, ids = chunk, properties = FileNodeProperties }, ct);
        }

        var results = await Task.WhenAll(tasks);
        var allNodes = results.SelectMany(r => r.List).ToArray();
        // State should be identical across pages for a consistent snapshot; use the last.
        return (allNodes, results[^1].State);
    }

    // ---- Blob transfer ----

    public async Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default)
    {
        var url = Session.GetDownloadUrl(AccountId, blobId, type, name);
        var response = await _conn.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default)
    {
        var url = Session.GetDownloadUrl(AccountId, blobId, type, name);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);

        var response = await _conn.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        bool isPartial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        return (stream, isPartial);
    }

    /// <summary>
    /// Raw HTTP POST of bytes to the upload URL. Forced to HTTP/1.1 so each
    /// upload gets its own TCP connection and doesn't starve interactive
    /// downloads via HTTP/2 multiplexing contention.
    /// </summary>
    private async Task<UploadResponse> PostBlobAsync(Stream data, string contentType, long? contentLength, CancellationToken ct)
    {
        var content = new StreamContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (contentLength.HasValue)
            content.Headers.ContentLength = contentLength;
        using var request = new HttpRequestMessage(HttpMethod.Post, Session.GetUploadUrl(AccountId))
        {
            Content = content,
            Version = System.Net.HttpVersion.Version11,
        };
        var response = await _conn.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<UploadResponse>(json, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse upload response");
    }

    public async Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default)
    {
        var upload = await PostBlobAsync(data, contentType, null, ct);
        return upload.BlobId;
    }

    /// <summary>
    /// Combine already-uploaded chunks into one blob via Blob/set (blob2),
    /// with per-chunk and overall digests for server-side integrity checking.
    /// </summary>
    private async Task<string> CombineChunksAsync(List<(string BlobId, string Sha1Base64)> chunks,
        string contentType, string overallSha1Base64, CancellationToken ct)
    {
        Log.Info($"[ChunkedUpload] Combining {chunks.Count} chunks for account {AccountId}, blobIds=[{string.Join(", ", chunks.Select(c => c.BlobId))}]");
        var createId = Guid.NewGuid().ToString("N")[..12];
        var result = await CallAsync(Blob2Using, "Blob/set", new
        {
            accountId = AccountId,
            create = new Dictionary<string, object>
            {
                [createId] = new Dictionary<string, object>
                {
                    ["data"] = chunks.Select(c => new Dictionary<string, object?>
                    {
                        ["blobId"] = c.BlobId,
                        ["digest:sha"] = c.Sha1Base64,
                    }).ToArray(),
                    ["type"] = contentType,
                    ["digest:sha"] = overallSha1Base64,
                },
            },
        }, ct);

        var blobUpload = result.Deserialize<BlobUploadResponse>(JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse Blob/set response");
        if (blobUpload.NotCreated != null && blobUpload.NotCreated.TryGetValue(createId, out var err))
            throw new InvalidOperationException($"Blob/set failed: {err.Type} — {err.Description}");
        if (blobUpload.Created == null || !blobUpload.Created.TryGetValue(createId, out var created))
            throw new InvalidOperationException("Blob/set returned no result");
        return created.Id;
    }

    /// <summary>
    /// Represents a previously uploaded chunk that can be reused on resume.
    /// </summary>
    public record UploadedChunkInfo(string BlobId, string Sha1Base64, long Offset, int Length);

    internal const long MinChunkSize = 1_048_576; // 1 MB minimum
    internal const long MaxChunkSize = 67_108_864; // 64 MB maximum

    public Task<string> UploadBlobChunkedAsync(Stream data, string contentType, long totalSize,
        Action<long>? onProgress = null, Action<UploadedChunkInfo>? onChunkUploaded = null,
        List<UploadedChunkInfo>? previousChunks = null,
        CancellationToken ct = default)
    {
        var baseChunkSize = ChunkSize ?? MaxChunkSize;

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

        return UploadBlobChunkedInternalAsync(effectiveChunkSize,
            data, contentType, totalSize, onProgress, onChunkUploaded, previousChunks, ct);
    }

    private async Task<string> UploadBlobChunkedInternalAsync(long chunkSize,
        Stream data, string contentType, long totalSize,
        Action<long>? onProgress, Action<UploadedChunkInfo>? onChunkUploaded,
        List<UploadedChunkInfo>? previousChunks,
        CancellationToken ct)
    {
        // Small buffer for resume verification (hash previously uploaded chunks
        // without loading them entirely into memory).
        const int HashBufferSize = 65536;

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
                var blobCheck = await CallAsync(Blob2Using, "Blob/get", new
                {
                    accountId = AccountId,
                    ids = blobIds,
                    properties = new[] { "id", "size" },
                }, ct);
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
                return await UploadBlobChunkedInternalAsync(chunkSize, data, contentType, totalSize,
                    onProgress, onChunkUploaded, null, ct);
            }

            var hashBuf = ArrayPool<byte>.Shared.Rent(HashBufferSize);
            try
            {
                for (int i = 0; i < validCount; i++)
                {
                    var prev = previousChunks[i];
                    // Hash the chunk data incrementally (needed for overall SHA1)
                    // without loading the whole chunk into memory
                    using var chunkVerifyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    var remaining = prev.Length;
                    while (remaining > 0)
                    {
                        var toRead = Math.Min(remaining, hashBuf.Length);
                        var read = await data.ReadAsync(hashBuf.AsMemory(0, toRead), ct);
                        if (read == 0) break;
                        chunkVerifyHash.AppendData(hashBuf.AsSpan(0, read));
                        overallHash.AppendData(hashBuf.AsSpan(0, read));
                        remaining -= read;
                    }

                    var chunkSha1Base64 = Convert.ToBase64String(chunkVerifyHash.GetHashAndReset());
                    if (chunkSha1Base64 != prev.Sha1Base64)
                    {
                        // File has changed since chunks were uploaded — start over
                        data.Position = 0;
                        return await UploadBlobChunkedInternalAsync(chunkSize, data, contentType, totalSize,
                            onProgress, onChunkUploaded, null, ct);
                    }

                    chunkBlobIds.Add((prev.BlobId, prev.Sha1Base64));
                    totalUploaded += prev.Length - remaining;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(hashBuf);
            }
            onProgress?.Invoke(totalUploaded);
        }

        while (totalUploaded < totalSize)
        {
            var thisChunkSize = (int)Math.Min(chunkSize, totalSize - totalUploaded);

            // Stream directly from file → HTTP POST, computing hashes and
            // reporting progress as bytes flow through. No full-chunk buffer.
            using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var chunkStream = new ChunkUploadStream(
                data, thisChunkSize, chunkHash, overallHash,
                totalUploaded, totalSize, onProgress, null);

            // Chunks are raw bytes, not the final content type.
            var upload = await PostBlobAsync(chunkStream, "application/octet-stream", thisChunkSize, ct);

            var chunkSha1Base64 = chunkStream.GetChunkSha1Base64();
            var bytesRead = chunkStream.TotalBytesRead;
            chunkBlobIds.Add((upload.BlobId, chunkSha1Base64));
            totalUploaded += bytesRead;
            Log.Info($"[ChunkedUpload] Chunk {chunkBlobIds.Count} uploaded: blobId={upload.BlobId}, size={bytesRead}, total={totalUploaded}/{totalSize}");
            onChunkUploaded?.Invoke(new UploadedChunkInfo(upload.BlobId, chunkSha1Base64, totalUploaded - bytesRead, bytesRead));

            // Yield between chunks so interactive work (downloads) can proceed.
            await Task.Yield();
        }

        // Signal final progress — all bytes uploaded, now combining.
        // This resets the stall timer so the combine call has a full
        // timeout window without being cancelled prematurely.
        onProgress?.Invoke(totalSize);

        var overallSha1Base64 = Convert.ToBase64String(overallHash.GetHashAndReset());
        return await CombineChunksAsync(chunkBlobIds, contentType, overallSha1Base64, ct);
    }

    public async Task<string> UploadBlobDeltaAsync(Stream data, string contentType, long totalSize,
        string? oldBlobId,
        Action<long>? onProgress = null, CancellationToken ct = default)
    {
        // No old blob or no blob2 → fall back to full chunked upload
        if (oldBlobId == null || !HasBlob2)
            return await UploadBlobChunkedAsync(data, contentType, totalSize, onProgress, ct: ct);

        // Query server for old blob's chunk structure
        List<(string blobId, long size, string? digestSha)>? serverChunks = null;
        try
        {
            var blobResult = await CallAsync<BlobGetResponse>(Blob2Using, "Blob/get", new
            {
                accountId = AccountId,
                ids = new[] { oldBlobId },
                properties = new[] { "id", "size", "chunks" },
            }, ct);

            if (blobResult.List.Length > 0 && blobResult.List[0].Chunks is { Length: > 0 } chunks)
                serverChunks = chunks.Select(c => (c.BlobId, c.Size, c.DigestSha)).ToList();
        }
        catch
        {
            // Blob/get failed — fall back to full upload
        }

        if (serverChunks == null || serverChunks.Count == 0)
            return await UploadBlobChunkedAsync(data, contentType, totalSize, onProgress, ct: ct);

        // Delta upload: walk the file in the server's chunk boundaries, reusing
        // any chunk whose SHA1 matches and uploading the rest. Data beyond the
        // server's chunk count is uploaded in MaxChunkSize pieces.
        var chunkBlobIds = new List<(string BlobId, string Sha1Base64)>();
        using var overallHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long totalUploaded = 0;
        int reusedChunks = 0;

        for (int i = 0; totalUploaded < totalSize; i++)
        {
            var serverChunk = i < serverChunks.Count ? serverChunks[i] : default;
            var thisChunkSize = (int)Math.Min(i < serverChunks.Count ? serverChunk.size : MaxChunkSize,
                totalSize - totalUploaded);

            var chunkData = ArrayPool<byte>.Shared.Rent(thisChunkSize);
            try
            {
                int bytesRead = 0;
                while (bytesRead < thisChunkSize)
                {
                    var n = await data.ReadAsync(chunkData.AsMemory(bytesRead, thisChunkSize - bytesRead), ct);
                    if (n == 0) break;
                    bytesRead += n;
                }

                overallHash.AppendData(chunkData, 0, bytesRead);
                var localSha1 = Convert.ToBase64String(SHA1.HashData(chunkData.AsSpan(0, bytesRead)));

                if (serverChunk.digestSha != null && localSha1 == serverChunk.digestSha)
                {
                    chunkBlobIds.Add((serverChunk.blobId, localSha1));
                    reusedChunks++;
                    Log.Info($"[DeltaUpload] Chunk {i}: reused (SHA1 match)");
                }
                else
                {
                    using var chunkStream = new MemoryStream(chunkData, 0, bytesRead);
                    var upload = await PostBlobAsync(chunkStream, "application/octet-stream", null, ct);
                    chunkBlobIds.Add((upload.BlobId, localSha1));
                    Log.Info($"[DeltaUpload] Chunk {i}: uploaded new ({bytesRead} bytes)");
                }

                totalUploaded += bytesRead;
                onProgress?.Invoke(totalUploaded);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkData);
            }
        }

        Log.Info($"[DeltaUpload] Reused {reusedChunks}/{chunkBlobIds.Count} chunks");

        if (chunkBlobIds.Count == 1)
            return chunkBlobIds[0].BlobId;

        var overallSha1Base64 = Convert.ToBase64String(overallHash.GetHashAndReset());
        return await CombineChunksAsync(chunkBlobIds, contentType, overallSha1Base64, ct);
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
        var response = await _conn.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<DirectWriteResponse>(json, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse direct write response");
        return (result.BlobId, result.Size, result.Type);
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
        if (offset.HasValue) blobArgs["offset"] = offset.Value;
        if (length.HasValue) blobArgs["length"] = length.Value;

        var blobResponse = await CallAsync<BlobGetResponse>(Blob2Using, "Blob/get", blobArgs, ct);
        if (blobResponse.NotFound.Length > 0)
            throw new FileNotFoundException($"Blob not found: {blobId}");
        if (blobResponse.List.Length == 0)
            throw new InvalidOperationException($"Blob/get returned no results for {blobId}");

        return blobResponse.List[0];
    }

    // ---- FileNode writes ----

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
            ["compareCaseInsensitively"] = true,
            ["create"] = new Dictionary<string, object?> { ["c0"] = createObj },
        };
        if (onExists != null)
            args["onExists"] = onExists;

        var setResponse = await CallAsync<SetResponse>(FileNodeUsing, "FileNode/set", args, ct);

        if (setResponse.NotCreated != null && setResponse.NotCreated.TryGetValue("c0", out var setError))
            throw new InvalidOperationException($"FileNode/set create failed: {setError.Type} — {setError.Description}");

        if (setResponse.Created == null || !setResponse.Created.TryGetValue("c0", out var created))
            throw new InvalidOperationException("FileNode/set create returned no result");

        return created;
    }

    /// <summary>
    /// FileNode/set update of a single node, throwing on a notUpdated error.
    /// </summary>
    private async Task UpdateFileNodeAsync(string nodeId, Dictionary<string, object?> fields,
        string? onExists, string what, CancellationToken ct)
    {
        var args = new Dictionary<string, object>
        {
            ["accountId"] = AccountId,
            ["compareCaseInsensitively"] = true,
            ["update"] = new Dictionary<string, object?> { [nodeId] = fields },
        };
        if (onExists != null)
            args["onExists"] = onExists;

        var setResponse = await CallAsync<SetResponse>(FileNodeUsing, "FileNode/set", args, ct);
        if (setResponse.NotUpdated != null && setResponse.NotUpdated.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set {what} failed: {setError.Type} — {setError.Description}");
    }

    public async Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, DateTime? createdAt = null, DateTime? modifiedAt = null, string? onExists = null, CancellationToken ct = default)
    {
        // v10: blobId is mutable — update directly via FileNode/set update.
        // Node ID stays the same (no destroy+create needed).
        var updateFields = new Dictionary<string, object?> { ["blobId"] = blobId };
        if (type != null)
            updateFields["type"] = type;
        if (modifiedAt.HasValue)
            updateFields["modified"] = modifiedAt.Value.ToUniversalTime();

        await UpdateFileNodeAsync(nodeId, updateFields, onExists, "update", ct);

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

    public Task MoveFileNodeAsync(string nodeId, string parentId, string newName, string? onExists = null, DateTime? modifiedAt = null, CancellationToken ct = default)
    {
        var updateFields = new Dictionary<string, object?>
        {
            ["parentId"] = parentId,
            ["name"] = newName,
            // null tells the server to set the current time
            ["modified"] = modifiedAt?.ToUniversalTime(),
        };
        return UpdateFileNodeAsync(nodeId, updateFields, onExists, "move", ct);
    }

    public async Task BatchUpdateAccessedAsync(Dictionary<string, DateTime> accessed, CancellationToken ct = default)
    {
        if (accessed.Count == 0) return;

        var update = new Dictionary<string, object>();
        foreach (var (nodeId, time) in accessed)
            update[nodeId] = new { accessed = time.ToUniversalTime() };

        await CallAsync<SetResponse>(FileNodeUsing, "FileNode/set", new { accountId = AccountId, update }, ct);
        // Ignore individual notUpdated errors — node may have been deleted
    }

    public void RecordAccess(string nodeId)
    {
        _pendingAccessed[nodeId] = DateTime.UtcNow;
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
                accountId = AccountId,
                onDestroyRemoveChildren = true,
                destroy = new[] { nodeId },
            }, ct);

        if (setResponse.NotDestroyed != null && setResponse.NotDestroyed.TryGetValue(nodeId, out var setError))
            throw new InvalidOperationException($"FileNode/set destroy failed: {setError.Type} — {setError.Description}");
    }

    // ---- Push ----

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
        var url = Session.GetEventSourceUrl("*", "no", "60");
        Log.Debug($"SSE connecting: {url}");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _conn.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        Log.Debug($"SSE connected: {response.StatusCode} {response.Content.Headers.ContentType}");

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

    // ---- Quota / thumbnails ----

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
        var converted = await ConvertImagesAsync([(blobId, width, height)], mimeType, ct);
        return converted.TryGetValue(blobId, out var thumbBlobId)
            ? thumbBlobId
            : throw new InvalidOperationException($"Blob/convert failed for {blobId}");
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

            var response = await CallAsync<BlobUploadResponse>(Blob2Using, "Blob/convert", new
            {
                accountId = AccountId,
                create,
            }, ct);

            if (response.Created != null)
            {
                foreach (var (createId, item) in response.Created)
                {
                    if (idToBlobId.TryGetValue(createId, out var blobId))
                        allConverted[blobId] = item.Id;
                }
            }
            if (response.NotCreated != null)
            {
                foreach (var (createId, err) in response.NotCreated)
                    Log.Debug($"[JMAP] Blob/convert failed for {idToBlobId.GetValueOrDefault(createId)}: {err.Type} — {err.Description}");
            }
        }
        return allConverted;
    }

    public void Dispose()
    {
        _accessedFlushTimer?.Dispose();
        // Best-effort flush
        try { FlushPendingAccessedAsync().GetAwaiter().GetResult(); } catch { }
        if (_ownsConnection)
            _conn.Http.Dispose();
    }
}
