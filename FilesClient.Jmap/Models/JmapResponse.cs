using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesClient.Jmap.Models;

public class JmapResponse
{
    [JsonPropertyName("methodResponses")]
    public JsonElement[][] MethodResponses { get; set; } = [];

    [JsonPropertyName("sessionState")]
    public string SessionState { get; set; } = "";
}

public class GetResponse<T>
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("list")]
    public T[] List { get; set; } = [];

    [JsonPropertyName("notFound")]
    public string[] NotFound { get; set; } = [];
}

public class QueryResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = "";

    [JsonPropertyName("queryState")]
    public string QueryState { get; set; } = "";

    [JsonPropertyName("canCalculateChanges")]
    public bool CanCalculateChanges { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("ids")]
    public string[] Ids { get; set; } = [];

    [JsonPropertyName("total")]
    public int? Total { get; set; }
}

public class UploadResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = "";

    [JsonPropertyName("blobId")]
    public string BlobId { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class SetResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = "";

    [JsonPropertyName("oldState")]
    public string OldState { get; set; } = "";

    [JsonPropertyName("newState")]
    public string NewState { get; set; } = "";

    [JsonPropertyName("created")]
    public Dictionary<string, FileNode>? Created { get; set; }

    [JsonPropertyName("updated")]
    public Dictionary<string, FileNode?>? Updated { get; set; }

    [JsonPropertyName("destroyed")]
    public string[]? Destroyed { get; set; }

    [JsonPropertyName("notCreated")]
    public Dictionary<string, SetError>? NotCreated { get; set; }

    [JsonPropertyName("notUpdated")]
    public Dictionary<string, SetError>? NotUpdated { get; set; }

    [JsonPropertyName("notDestroyed")]
    public Dictionary<string, SetError>? NotDestroyed { get; set; }
}

public class SetError
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class ChangesResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = "";

    [JsonPropertyName("oldState")]
    public string OldState { get; set; } = "";

    [JsonPropertyName("newState")]
    public string NewState { get; set; } = "";

    [JsonPropertyName("hasMoreChanges")]
    public bool HasMoreChanges { get; set; }

    [JsonPropertyName("created")]
    public string[] Created { get; set; } = [];

    [JsonPropertyName("updated")]
    public string[] Updated { get; set; } = [];

    [JsonPropertyName("destroyed")]
    public string[] Destroyed { get; set; } = [];
}

public class BlobGetResponse
{
    [JsonPropertyName("list")]
    public BlobDataItem[] List { get; set; } = [];

    [JsonPropertyName("notFound")]
    public string[] NotFound { get; set; } = [];
}

public class BlobDataItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("data:asBase64")]
    public string? DataAsBase64 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest:sha")]
    public string? DigestSha { get; set; }

    [JsonPropertyName("digest:sha-256")]
    public string? DigestSha256 { get; set; }

    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; set; }
}

public class BlobUploadResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = "";

    [JsonPropertyName("created")]
    public Dictionary<string, BlobUploadCreatedItem>? Created { get; set; }

    [JsonPropertyName("notCreated")]
    public Dictionary<string, SetError>? NotCreated { get; set; }
}

public class BlobUploadCreatedItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class DataSourceObject
{
    [JsonPropertyName("blobId")]
    public string BlobId { get; set; } = "";

    [JsonPropertyName("digest:sha")]
    public string? DigestSha { get; set; }
}
