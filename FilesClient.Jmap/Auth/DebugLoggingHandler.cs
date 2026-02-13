using System.Text.Json;

namespace FilesClient.Jmap.Auth;

/// <summary>
/// HTTP handler that logs all requests and responses when DEBUGJMAP is enabled.
/// Sits in the handler pipeline so it captures everything: session lookups,
/// redirects, API calls, blob downloads, and error responses.
/// </summary>
internal class DebugLoggingHandler : DelegatingHandler
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
    };

    public DebugLoggingHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($">> {request.Method} {request.RequestUri}");

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            DumpJson("Request body", body);
        }

        var response = await base.SendAsync(request, cancellationToken);

        Console.Error.WriteLine($"<< {(int)response.StatusCode} {response.ReasonPhrase}");

        // Only dump response body for JSON content types (skip binary blob downloads)
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is "application/json" or "application/problem+json")
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DumpJson("Response body", responseBody);
        }

        return response;
    }

    private static void DumpJson(string label, string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var pretty = JsonSerializer.Serialize(doc, PrettyJson);
            Console.Error.WriteLine($"[JMAP {label}]\n{pretty}");
        }
        catch
        {
            Console.Error.WriteLine($"[JMAP {label}]\n{json}");
        }
    }
}
