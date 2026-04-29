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

    public string[] GetSupportedDigestAlgorithms(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return [];
        if (!account.AccountCapabilities.TryGetValue("urn:ietf:params:jmap:blob", out var blobCap))
            return [];
        if (blobCap.TryGetProperty("supportedDigestAlgorithms", out var algos) &&
            algos.ValueKind == JsonValueKind.Array)
        {
            return algos.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => s != null)
                .ToArray()!;
        }
        return [];
    }

    public bool HasCapability(string capability) => Capabilities.ContainsKey(capability);

    public bool HasAccountCapability(string accountId, string capability)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return false;
        return account.AccountCapabilities.ContainsKey(capability);
    }

    // JMAP core capability limits (RFC 8620 §2)
    public int MaxCallsInRequest => GetCoreInt("maxCallsInRequest", 16);
    public int MaxObjectsInGet => GetCoreInt("maxObjectsInGet", 500);
    public int MaxObjectsInSet => GetCoreInt("maxObjectsInSet", 500);
    public int MaxConcurrentRequests => GetCoreInt("maxConcurrentRequests", 4);

    private int GetCoreInt(string property, int defaultValue)
    {
        if (!Capabilities.TryGetValue("urn:ietf:params:jmap:core", out var core))
            return defaultValue;
        if (core.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return defaultValue;
    }

    public long? GetChunkSize(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return null;
        if (!account.AccountCapabilities.TryGetValue(
            JmapClient.Blob2Capability, out var blob2Cap))
            return null;
        if (blob2Cap.TryGetProperty("chunkSize", out var chunkSize)
            && chunkSize.ValueKind == JsonValueKind.Number)
            return chunkSize.GetInt64();
        return null;
    }

    public int? GetMaxDataSources(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return null;
        if (!account.AccountCapabilities.TryGetValue("urn:ietf:params:jmap:blob", out var blobCap))
            return null;
        if (blobCap.TryGetProperty("maxDataSources", out var val)
            && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }

    public long? GetMaxSizeBlobSet(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return null;
        if (!account.AccountCapabilities.TryGetValue("urn:ietf:params:jmap:blob", out var blobCap))
            return null;
        if (blobCap.TryGetProperty("maxSizeBlobSet", out var val)
            && val.ValueKind == JsonValueKind.Number)
            return val.GetInt64();
        return null;
    }

    public string? GetTrashUrl(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return null;
        if (!account.AccountCapabilities.TryGetValue(
            "https://www.fastmail.com/dev/filenode", out var fileNodeCap))
            return null;
        if (fileNodeCap.TryGetProperty("webTrashUrl", out var trashUrl)
            && trashUrl.ValueKind == JsonValueKind.String)
            return trashUrl.GetString();
        return null;
    }

    public string? GetWebUrlTemplate(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return null;
        if (!account.AccountCapabilities.TryGetValue(
            "https://www.fastmail.com/dev/filenode", out var fileNodeCap))
            return null;
        if (fileNodeCap.TryGetProperty("webUrlTemplate", out var webUrlTemplate)
            && webUrlTemplate.ValueKind == JsonValueKind.String)
            return webUrlTemplate.GetString();
        return null;
    }

    public string? GetWebWriteUrlTemplate(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return null;
        if (!account.AccountCapabilities.TryGetValue(
            "https://www.fastmail.com/dev/filenode", out var fileNodeCap))
            return null;
        if (fileNodeCap.TryGetProperty("webWriteUrlTemplate", out var webWriteUrlTemplate)
            && webWriteUrlTemplate.ValueKind == JsonValueKind.String)
            return webWriteUrlTemplate.GetString();
        return null;
    }

    public bool? GetMayCreateTopLevelFileNode(string accountId)
    {
        if (!Accounts.TryGetValue(accountId, out var account))
            return null;
        if (!account.AccountCapabilities.TryGetValue(
            "https://www.fastmail.com/dev/filenode", out var fileNodeCap))
            return null;
        if (fileNodeCap.TryGetProperty("mayCreateTopLevelFileNode", out var val)
            && (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False))
            return val.GetBoolean();
        return null;
    }

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
