using System.Text.Json.Serialization;

namespace FileNodeClient.Jmap.Models;

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

    // draft-12 split mayWrite into four granular rights
    [JsonPropertyName("mayAddChildren")]
    public bool MayAddChildren { get; set; }

    [JsonPropertyName("mayRename")]
    public bool MayRename { get; set; }

    [JsonPropertyName("mayDelete")]
    public bool MayDelete { get; set; }

    [JsonPropertyName("mayModifyContent")]
    public bool MayModifyContent { get; set; }

    [JsonPropertyName("mayShare")]
    public bool MayShare { get; set; }

    /// <summary>
    /// Convenience: true if the user can add children, rename, delete, and modify content.
    /// Replaces the old mayWrite property (removed in draft-12).
    /// </summary>
    [JsonIgnore]
    public bool MayWrite => MayAddChildren && MayRename && MayDelete && MayModifyContent;
}
