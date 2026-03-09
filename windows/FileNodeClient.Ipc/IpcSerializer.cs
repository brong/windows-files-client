using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileNodeClient.Ipc;

public static class IpcSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SerializeRequest(string id, string method, object? @params = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("method", method);
        if (@params != null)
        {
            writer.WritePropertyName("params");
            JsonSerializer.SerializeToElement(@params, @params.GetType(), Options).WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeResponse(string id, object? result = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WritePropertyName("result");
        if (result != null)
            JsonSerializer.SerializeToElement(result, result.GetType(), Options).WriteTo(writer);
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeError(string id, string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializePush(string method, object? @params = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("method", method);
        if (@params != null)
        {
            writer.WritePropertyName("params");
            JsonSerializer.SerializeToElement(@params, @params.GetType(), Options).WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IpcMessage? ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var hasId = root.TryGetProperty("id", out var idProp);
        var hasMethod = root.TryGetProperty("method", out var methodProp);
        var hasResult = root.TryGetProperty("result", out var resultProp);
        var hasError = root.TryGetProperty("error", out var errorProp);

        if (hasId && hasMethod)
        {
            // Request
            var id = idProp.GetString()!;
            var method = methodProp.GetString()!;
            JsonElement? @params = root.TryGetProperty("params", out var p) ? p.Clone() : null;
            return new IpcRequest(id, method, @params);
        }

        if (hasId && hasResult)
        {
            // Response
            var id = idProp.GetString()!;
            return new IpcResponse(id, resultProp.Clone());
        }

        if (hasId && hasError)
        {
            // Error
            var id = idProp.GetString()!;
            var message = errorProp.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString() ?? "Unknown error"
                : "Unknown error";
            return new IpcError(id, message);
        }

        if (hasMethod && !hasId)
        {
            // Push
            var method = methodProp.GetString()!;
            JsonElement? @params = root.TryGetProperty("params", out var p) ? p.Clone() : null;
            return new IpcPush(method, @params);
        }

        return null;
    }

    public static T Deserialize<T>(JsonElement? element)
    {
        if (element == null)
            return JsonSerializer.Deserialize<T>("{}", Options)!;
        return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), Options)!;
    }
}
