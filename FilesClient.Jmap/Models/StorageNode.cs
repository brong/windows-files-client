using System.Text.Json.Serialization;

namespace FilesClient.Jmap.Models;

public class StorageNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("blobId")]
    public string? BlobId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("created")]
    public DateTime? Created { get; set; }

    [JsonPropertyName("modified")]
    public DateTime? Modified { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("orientation")]
    public int? Orientation { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("myRights")]
    public FilesRights? MyRights { get; set; }

    [JsonPropertyName("shareWith")]
    public Dictionary<string, FilesRights>? ShareWith { get; set; }

    [JsonPropertyName("sharedLinkUrl")]
    public string? SharedLinkUrl { get; set; }

    [JsonPropertyName("isSharedLinkEnabled")]
    public bool? IsSharedLinkEnabled { get; set; }

    [JsonIgnore]
    public bool IsFolder => BlobId == null;
}

public class FilesRights
{
    [JsonPropertyName("mayRead")]
    public bool MayRead { get; set; }

    [JsonPropertyName("mayWrite")]
    public bool MayWrite { get; set; }

    [JsonPropertyName("mayAdmin")]
    public bool MayAdmin { get; set; }
}
