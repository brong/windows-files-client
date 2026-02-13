using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesClient.Jmap;

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

    public string GetDownloadUrl(string accountId, string blobId, string? type = null, string? name = null)
    {
        return DownloadUrl
            .Replace("{accountId}", Uri.EscapeDataString(accountId))
            .Replace("{blobId}", Uri.EscapeDataString(blobId))
            .Replace("{type}", Uri.EscapeDataString(type ?? "application/octet-stream"))
            .Replace("{name}", Uri.EscapeDataString(name ?? "download"));
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
