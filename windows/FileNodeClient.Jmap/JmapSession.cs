using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileNodeClient.Jmap;

public class JmapSession
{
    [JsonPropertyName("capabilities")]
    public Dictionary<string, JsonElement> Capabilities { get; set; } = new();

    [JsonPropertyName("accounts")]
    public Dictionary<string, JmapAccount> Accounts { get; set; } = new();

    [JsonPropertyName("primaryAccounts")]
    public Dictionary<string, string> PrimaryAccounts { get; set; } = new();

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("apiUrl")]
    public string ApiUrl { get; set; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("uploadUrl")]
    public string UploadUrl { get; set; } = "";

    [JsonPropertyName("eventSourceUrl")]
    public string EventSourceUrl { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    public string GetPrimaryAccount(string capability)
    {
        return PrimaryAccounts.TryGetValue(capability, out var accountId)
            ? accountId
            : throw new InvalidOperationException($"No primary account for capability {capability}");
    }

    public string GetUploadUrl(string accountId)
    {
        return UploadUrl.Replace("{accountId}", Uri.EscapeDataString(accountId));
    }

    public string GetEventSourceUrl(string types, string closeafter, string ping) =>
        EventSourceUrl
            .Replace("{types}", Uri.EscapeDataString(types))
            .Replace("{closeafter}", Uri.EscapeDataString(closeafter))
            .Replace("{ping}", Uri.EscapeDataString(ping));

    public string GetDownloadUrl(string accountId, string blobId, string? type = null, string? name = null)
    {
        return DownloadUrl
            .Replace("{accountId}", Uri.EscapeDataString(accountId))
            .Replace("{blobId}", Uri.EscapeDataString(blobId))
            .Replace("{type}", Uri.EscapeDataString(type ?? "application/octet-stream"))
            .Replace("{name}", Uri.EscapeDataString(name ?? "download"));
    }

    public bool HasCapability(string capability) => Capabilities.ContainsKey(capability);

    public bool HasAccountCapability(string accountId, string capability) =>
        Accounts.TryGetValue(accountId, out var account)
        && account.AccountCapabilities.ContainsKey(capability);

    /// <summary>
    /// A property of one of an account's capability objects, or null if the
    /// account, capability, or property is absent.
    /// </summary>
    private JsonElement? AccountCapabilityProperty(string accountId, string capability, string property)
    {
        if (!Accounts.TryGetValue(accountId, out var account)) return null;
        if (!account.AccountCapabilities.TryGetValue(capability, out var cap)) return null;
        return cap.TryGetProperty(property, out var val) ? val : null;
    }

    private string? AccountCapabilityString(string accountId, string capability, string property) =>
        AccountCapabilityProperty(accountId, capability, property) is { ValueKind: JsonValueKind.String } v
            ? v.GetString() : null;

    private long? AccountCapabilityNumber(string accountId, string capability, string property) =>
        AccountCapabilityProperty(accountId, capability, property) is { ValueKind: JsonValueKind.Number } v
            ? v.GetInt64() : null;

    // JMAP core capability limits (RFC 8620 §2)
    public int MaxCallsInRequest => GetCoreInt("maxCallsInRequest", 16);
    public int MaxObjectsInGet => GetCoreInt("maxObjectsInGet", 500);
    public int MaxObjectsInSet => GetCoreInt("maxObjectsInSet", 500);
    public int MaxConcurrentRequests => GetCoreInt("maxConcurrentRequests", 4);

    private int GetCoreInt(string property, int defaultValue)
    {
        if (!Capabilities.TryGetValue(JmapClient.CoreCapability, out var core))
            return defaultValue;
        if (core.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return defaultValue;
    }

    // Limits advertised under the legacy blob capability's metadata. We never
    // send that capability in `using`, but the server still publishes these.
    public string[] GetSupportedDigestAlgorithms(string accountId) =>
        AccountCapabilityProperty(accountId, JmapClient.BlobCapability, "supportedDigestAlgorithms")
            is { ValueKind: JsonValueKind.Array } algos
            ? algos.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToArray()
            : [];

    public int? GetMaxDataSources(string accountId) =>
        (int?)AccountCapabilityNumber(accountId, JmapClient.BlobCapability, "maxDataSources");

    public long? GetMaxSizeBlobSet(string accountId) =>
        AccountCapabilityNumber(accountId, JmapClient.BlobCapability, "maxSizeBlobSet");

    public long? GetChunkSize(string accountId) =>
        AccountCapabilityNumber(accountId, JmapClient.Blob2Capability, "chunkSize");

    public string? GetTrashUrl(string accountId) =>
        AccountCapabilityString(accountId, JmapClient.FileNodeCapability, "webTrashUrl");

    public string? GetWebUrlTemplate(string accountId) =>
        AccountCapabilityString(accountId, JmapClient.FileNodeCapability, "webUrlTemplate");

    public string? GetWebWriteUrlTemplate(string accountId) =>
        AccountCapabilityString(accountId, JmapClient.FileNodeCapability, "webWriteUrlTemplate");

    /// <summary>
    /// Whether the server treats sibling names as case-insensitive. Null if the
    /// capability is absent. Exposed so the client knows whether case-only sibling
    /// names can coexist on the server (DESIGN §14).
    /// </summary>
    public bool? GetCaseInsensitiveNames(string accountId) =>
        AccountCapabilityProperty(accountId, JmapClient.FileNodeCapability, "caseInsensitiveNames")
            is { ValueKind: JsonValueKind.True or JsonValueKind.False } v
            ? v.GetBoolean() : null;
}

public class JmapAccount
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("isPersonal")]
    public bool IsPersonal { get; set; }

    [JsonPropertyName("isReadOnly")]
    public bool IsReadOnly { get; set; }

    [JsonPropertyName("accountCapabilities")]
    public Dictionary<string, JsonElement> AccountCapabilities { get; set; } = new();
}
