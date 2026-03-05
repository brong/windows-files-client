using System.Text.Json.Serialization;

namespace FilesClient.Jmap.Models;

/// <summary>
/// JMAP Quota object per RFC 9425.
/// </summary>
public class Quota
{
    [JsonPropertyName("id")]            public string Id { get; set; } = "";
    [JsonPropertyName("resourceType")]  public string ResourceType { get; set; } = "";
    [JsonPropertyName("used")]          public long Used { get; set; }
    [JsonPropertyName("hardLimit")]     public long HardLimit { get; set; }
    [JsonPropertyName("warnLimit")]     public long? WarnLimit { get; set; }
    [JsonPropertyName("softLimit")]     public long? SoftLimit { get; set; }
    [JsonPropertyName("scope")]         public string Scope { get; set; } = "";
    [JsonPropertyName("name")]          public string Name { get; set; } = "";
    [JsonPropertyName("types")]         public string[]? Types { get; set; }
    [JsonPropertyName("description")]   public string? Description { get; set; }
}
