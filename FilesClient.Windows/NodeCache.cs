using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesClient.Windows;

public record CacheSnapshot
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("homeNodeId")]
    public string HomeNodeId { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("entries")]
    public Dictionary<string, string> Entries { get; init; } = new();
}

public static class NodeCache
{
    private static string GetCachePath(string scopeKey)
    {
        var parts = scopeKey.Split('/', '\\');
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastmailFiles", Path.Combine(parts));
        return Path.Combine(dir, "nodecache.json");
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
            if (snapshot == null || snapshot.Version != 1
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
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var prefix = syncRootPath + Path.DirectorySeparatorChar;
        var entries = new Dictionary<string, string>();
        foreach (var (nodeId, fullPath) in nodeIdToPath)
        {
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                entries[nodeId] = fullPath.Substring(prefix.Length);
        }

        var snapshot = new CacheSnapshot
        {
            Version = 1,
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
