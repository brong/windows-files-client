using System.Text.Json.Serialization;

namespace FilesClient.Jmap.Models;

public class FileNode
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
    public long? Size { get; set; }

    [JsonPropertyName("created")]
    public DateTime? Created { get; set; }

    [JsonPropertyName("modified")]
    public DateTime? Modified { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("accessed")]
    public DateTime? Accessed { get; set; }

    [JsonPropertyName("executable")]
    public bool? Executable { get; set; }

    [JsonPropertyName("isSubscribed")]
    public bool? IsSubscribed { get; set; }

    [JsonPropertyName("myRights")]
    public FilesRights? MyRights { get; set; }

    [JsonPropertyName("shareWith")]
    public Dictionary<string, FilesRights>? ShareWith { get; set; }

    [JsonIgnore]
    public bool IsFolder => BlobId == null;
}

public class FilesRights
{
    [JsonPropertyName("mayRead")]
    public bool MayRead { get; set; }

    [JsonPropertyName("mayWrite")]
    public bool MayWrite { get; set; }

    [JsonPropertyName("mayShare")]
    public bool MayShare { get; set; }
}
