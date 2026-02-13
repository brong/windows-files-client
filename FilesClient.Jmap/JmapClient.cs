using System.Text.Json;
using FilesClient.Jmap.Auth;
using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public class JmapClient : IDisposable
{
    private readonly HttpClient _http;
    private JmapSession? _session;

    // Fastmail uses "https://www.fastmailusercontent.com/jmap/api/" but we
    // discover it via the session resource.
    public const string StorageNodeCapability = "https://www.fastmail.com/dev/files";
    private static readonly string[] StorageNodeUsing = [StorageNodeCapability];

    public JmapSession Session => _session
        ?? throw new InvalidOperationException("Session not initialised — call ConnectAsync first");

    public string AccountId => Session.GetPrimaryAccount(StorageNodeCapability);

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

    public async Task<JmapResponse> CallAsync(JmapRequest request, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(request, JmapSerializerOptions.Default);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(Session.ApiUrl, content, ct);
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JmapResponse>(responseJson, JmapSerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse JMAP response");
    }

    public async Task<StorageNode[]> GetStorageNodesAsync(string[] ids, CancellationToken ct = default)
    {
        var request = JmapRequest.Create(StorageNodeUsing,
            ("StorageNode/get", new { accountId = AccountId, ids }, "g0"));

        var response = await CallAsync(request, ct);
        var (method, _, _) = response.GetResponse(0);

        if (method == "error")
        {
            var error = response.GetArgs<JsonElement>(0);
            throw new InvalidOperationException($"JMAP error: {error}");
        }

        var getResponse = response.GetArgs<GetResponse<StorageNode>>(0);
        return getResponse.List;
    }

    public async Task<string[]> QueryChildrenAsync(string parentId, CancellationToken ct = default)
    {
        var request = JmapRequest.Create(StorageNodeUsing,
            ("StorageNode/query", new
            {
                accountId = AccountId,
                filter = new { parentId },
                sort = new[] { new { property = "name", isAscending = true } },
            }, "q0"));

        var response = await CallAsync(request, ct);
        var (method, _, _) = response.GetResponse(0);

        if (method == "error")
        {
            var error = response.GetArgs<JsonElement>(0);
            throw new InvalidOperationException($"JMAP error: {error}");
        }

        return response.GetArgs<QueryResponse>(0).Ids;
    }

    public async Task<StorageNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        // Query + Get in a single round-trip using back-references
        var request = JmapRequest.Create(StorageNodeUsing,
            ("StorageNode/query", new
            {
                accountId = AccountId,
                filter = new { parentId },
                sort = new[] { new { property = "name", isAscending = true } },
            }, "q0"),
            ("StorageNode/get", new
            {
                accountId = AccountId,
                ids = (object?)null, // will be replaced by back-reference
                __back_ref = new { ids = new { resultOf = "q0", name = "StorageNode/query", path = "/ids" } },
            }, "g0"));

        // Actually, JMAP back-references use a special "#" syntax.
        // Let's use two separate calls for clarity in the PoC.
        var ids = await QueryChildrenAsync(parentId, ct);
        if (ids.Length == 0)
            return [];
        return await GetStorageNodesAsync(ids, ct);
    }

    public async Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default)
    {
        var request = JmapRequest.Create(StorageNodeUsing,
            ("StorageNode/changes", new
            {
                accountId = AccountId,
                sinceState,
            }, "c0"));

        var response = await CallAsync(request, ct);
        var (method, _, _) = response.GetResponse(0);

        if (method == "error")
        {
            var error = response.GetArgs<JsonElement>(0);
            throw new InvalidOperationException($"JMAP error: {error}");
        }

        return response.GetArgs<ChangesResponse>(0);
    }

    public async Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default)
    {
        var url = Session.GetDownloadUrl(AccountId, blobId, type, name);
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
