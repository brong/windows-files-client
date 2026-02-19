using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesClient.Ipc;

public static class IpcSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Type discriminator field name
    private const string TypeField = "type";

    private static readonly Dictionary<string, Type> CommandTypes = new()
    {
        ["getStatus"] = typeof(GetStatusCommand),
        ["addLogin"] = typeof(AddLoginCommand),
        ["discoverAccounts"] = typeof(DiscoverAccountsCommand),
        ["removeLogin"] = typeof(RemoveLoginCommand),
        ["cleanUpAccount"] = typeof(CleanUpAccountCommand),
        ["configureLogin"] = typeof(ConfigureLoginCommand),
        ["getOutbox"] = typeof(GetOutboxCommand),
        ["getLoginAccounts"] = typeof(GetLoginAccountsCommand),
        ["updateLogin"] = typeof(UpdateLoginCommand),
        ["detachAccount"] = typeof(DetachAccountCommand),
        ["refreshAccount"] = typeof(RefreshAccountCommand),
        ["cleanAccount"] = typeof(CleanAccountCommand),
        ["enableAccount"] = typeof(EnableAccountCommand),
    };

    private static readonly Dictionary<string, Type> EventTypes = new()
    {
        ["statusSnapshot"] = typeof(StatusSnapshotEvent),
        ["accountStatusChanged"] = typeof(AccountStatusChangedEvent),
        ["accountsChanged"] = typeof(AccountsChangedEvent),
        ["addLoginResult"] = typeof(AddLoginResultEvent),
        ["discoverAccountsResult"] = typeof(DiscoverAccountsResultEvent),
        ["commandResult"] = typeof(CommandResultEvent),
        ["outboxSnapshot"] = typeof(OutboxSnapshotEvent),
        ["loginAccountsResult"] = typeof(LoginAccountsResultEvent),
    };

    private static readonly Dictionary<Type, string> TypeToName;

    static IpcSerializer()
    {
        TypeToName = new Dictionary<Type, string>();
        foreach (var (name, type) in CommandTypes)
            TypeToName[type] = name;
        foreach (var (name, type) in EventTypes)
            TypeToName[type] = name;
    }

    public static string Serialize(object message)
    {
        if (!TypeToName.TryGetValue(message.GetType(), out var typeName))
            throw new ArgumentException($"Unknown message type: {message.GetType().Name}");

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString(TypeField, typeName);

        // Serialize the message body as a nested "data" object
        var dataJson = JsonSerializer.SerializeToElement(message, message.GetType(), Options);
        writer.WritePropertyName("data");
        dataJson.WriteTo(writer);

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IpcCommand? DeserializeCommand(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty(TypeField, out var typeProp))
            return null;

        var typeName = typeProp.GetString();
        if (typeName == null || !CommandTypes.TryGetValue(typeName, out var type))
            return null;

        if (!root.TryGetProperty("data", out var dataProp))
            return null;

        return JsonSerializer.Deserialize(dataProp.GetRawText(), type, Options) as IpcCommand;
    }

    public static IpcEvent? DeserializeEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty(TypeField, out var typeProp))
            return null;

        var typeName = typeProp.GetString();
        if (typeName == null || !EventTypes.TryGetValue(typeName, out var type))
            return null;

        if (!root.TryGetProperty("data", out var dataProp))
            return null;

        return JsonSerializer.Deserialize(dataProp.GetRawText(), type, Options) as IpcEvent;
    }
}
