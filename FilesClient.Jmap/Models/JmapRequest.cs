using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesClient.Jmap.Models;

public class JmapRequest
{
    [JsonPropertyName("using")]
    public string[] Using { get; set; } = [];

    [JsonPropertyName("methodCalls")]
    public JsonElement[][] MethodCalls { get; set; } = [];

    public static JmapRequest Create(string[] capabilities, params (string method, object args, string callId)[] calls)
    {
        var methodCalls = new JsonElement[calls.Length][];
        for (int i = 0; i < calls.Length; i++)
        {
            var (method, args, callId) = calls[i];
            methodCalls[i] =
            [
                JsonSerializer.SerializeToElement(method),
                JsonSerializer.SerializeToElement(args, JmapSerializerOptions.Default),
                JsonSerializer.SerializeToElement(callId),
            ];
        }

        return new JmapRequest
        {
            Using = capabilities,
            MethodCalls = methodCalls,
        };
    }
}

public static class JmapSerializerOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
