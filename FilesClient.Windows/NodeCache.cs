using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesClient.Windows;

public record CacheEntry
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("modified")]
    public DateTime Modified { get; init; }

    [JsonPropertyName("isFolder")]
    public bool IsFolder { get; init; }
}

public record CacheSnapshot
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("homeNodeId")]
    public string HomeNodeId { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("entries")]
    public Dictionary<string, CacheEntry> Entries { get; init; } = new();
}

public static class NodeCache
{
    private static string GetCachePath(string scopeKey)
    {
        var parts = scopeKey.Split('/', '\\');
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastmailFiles", System.IO.Path.Combine(parts));
        return System.IO.Path.Combine(dir, "nodecache.json");
    }

    public static CacheSnapshot? Load(string scopeKey)
    {
        var path = GetCachePath(scopeKey);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<CacheSnapshot>(json);
            if (snapshot == null || snapshot.Version != 2
                || string.IsNullOrEmpty(snapshot.HomeNodeId)
                || string.IsNullOrEmpty(snapshot.State))
                return null;

            return snapshot;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load node cache: {ex.Message}");
            return null;
        }
    }

    public static void Save(string scopeKey, string homeNodeId, string state,
        IReadOnlyDictionary<string, string> nodeIdToPath, string syncRootPath)
    {
        var path = GetCachePath(scopeKey);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var prefix = syncRootPath + System.IO.Path.DirectorySeparatorChar;
        var entries = new Dictionary<string, CacheEntry>();
        foreach (var (nodeId, fullPath) in nodeIdToPath)
        {
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = fullPath.Substring(prefix.Length);
            try
            {
                if (Directory.Exists(fullPath))
                {
                    entries[nodeId] = new CacheEntry
                    {
                        Path = relativePath,
                        IsFolder = true,
                    };
                }
                else if (File.Exists(fullPath))
                {
                    var info = new FileInfo(fullPath);
                    entries[nodeId] = new CacheEntry
                    {
                        Path = relativePath,
                        Size = info.Length,
                        Modified = info.LastWriteTimeUtc,
                    };
                }
                else
                {
                    // Item in mappings but not on disk — skip
                }
            }
            catch
            {
                // Can't stat — skip
            }
        }

        var snapshot = new CacheSnapshot
        {
            Version = 2,
            HomeNodeId = homeNodeId,
            State = state,
            Entries = entries,
        };

        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save node cache: {ex.Message}");
        }
    }

    public static void Delete(string scopeKey)
    {
        var path = GetCachePath(scopeKey);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to delete node cache: {ex.Message}");
        }
    }
}
