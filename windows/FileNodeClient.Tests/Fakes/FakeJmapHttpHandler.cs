using System.Net;
using System.Text;
using System.Text.Json;

namespace FileNodeClient.Tests.Fakes;

/// <summary>
/// The HTTP surface of a JMAP server, for testing <see cref="Jmap.JmapClient"/> itself
/// (batching, paging, error mapping) rather than the sync engine. Serves the session
/// document at <see cref="SessionUrl"/> and dispatches each method call POSTed to
/// <see cref="ApiUrl"/> to <see cref="OnMethod"/>, which returns the response method
/// name and its arguments object (serialized with the client's own options).
/// </summary>
public sealed class FakeJmapHttpHandler : HttpMessageHandler
{
    public const string SessionUrl = "https://jmap.test/session";
    public const string ApiUrl = "https://jmap.test/api";
    public const string AccountId = "acc1";

    /// <summary>(method, args, callId) → (responseMethod, responseArgs). Default: echo an error.</summary>
    public Func<string, JsonElement, string, (string Method, object Args)> OnMethod { get; set; } =
        (method, _, _) => ("error", new { type = "unknownMethod", description = $"unscripted {method}" });

    public int PostCount { get; private set; }
    public List<(string Method, JsonElement Args)> Calls { get; } = new();

    public static object DefaultSession() => new
    {
        capabilities = new Dictionary<string, object>
        {
            ["urn:ietf:params:jmap:core"] = new { maxCallsInRequest = 16, maxObjectsInGet = 500, maxObjectsInSet = 500, maxConcurrentRequests = 4 },
            ["https://www.fastmail.com/dev/filenode"] = new { },
        },
        accounts = new Dictionary<string, object>
        {
            [AccountId] = new
            {
                name = "tests@jmap.test", isPersonal = true, isReadOnly = false,
                accountCapabilities = new Dictionary<string, object>
                {
                    ["https://www.fastmail.com/dev/filenode"] = new { caseInsensitiveNames = true },
                },
            },
        },
        primaryAccounts = new Dictionary<string, string> { ["https://www.fastmail.com/dev/filenode"] = AccountId },
        username = "tests@jmap.test",
        apiUrl = ApiUrl,
        downloadUrl = "https://jmap.test/download/{accountId}/{blobId}/{name}?type={type}",
        uploadUrl = "https://jmap.test/upload/{accountId}/",
        eventSourceUrl = "https://jmap.test/events?types={types}&closeafter={closeafter}&ping={ping}",
        state = "sess1",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        if (request.Method == HttpMethod.Get && url == SessionUrl)
            return Json(DefaultSession());

        if (request.Method == HttpMethod.Post && url == ApiUrl)
        {
            PostCount++;
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var responses = new List<object[]>();
            var earlier = new Dictionary<string, JsonElement>();
            foreach (var call in doc.RootElement.GetProperty("methodCalls").EnumerateArray())
            {
                var method = call[0].GetString()!;
                var args = ResolveResultReferences(call[1], earlier);
                var callId = call[2].GetString()!;
                Calls.Add((method, args));
                var (respMethod, respArgs) = OnMethod(method, args, callId);
                responses.Add([respMethod, respArgs, callId]);
                earlier[callId] = JsonSerializer.SerializeToElement(respArgs);
            }
            return Json(new { methodResponses = responses, sessionState = "sess1" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"unscripted {request.Method} {url}") };
    }

    /// <summary>
    /// RFC 8620 §3.7: an argument "#name": {resultOf, name, path} is replaced by the
    /// value at that JSON pointer in an earlier response of the same request.
    /// </summary>
    private static JsonElement ResolveResultReferences(JsonElement args, Dictionary<string, JsonElement> earlier)
    {
        if (!args.EnumerateObject().Any(p => p.Name.StartsWith('#'))) return args.Clone();
        var resolved = new Dictionary<string, object?>();
        foreach (var prop in args.EnumerateObject())
        {
            if (!prop.Name.StartsWith('#')) { resolved[prop.Name] = prop.Value.Clone(); continue; }
            var source = earlier[prop.Value.GetProperty("resultOf").GetString()!];
            var pointer = prop.Value.GetProperty("path").GetString()!;
            JsonElement current = source;
            foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
                current = current.GetProperty(segment);
            resolved[prop.Name[1..]] = current.Clone();
        }
        return JsonSerializer.SerializeToElement(resolved);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
}

/// <summary>A minimal scripted <see cref="HttpMessageHandler"/> for non-JMAP endpoints (OAuth).</summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Respond(request);
    }

    public static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
}
