using System.Text.Json;
using FileNodeClient.Logging;

namespace FileNodeClient.Service;

/// <summary>
/// Persists account metadata (display name, sync path, etc.) so accounts
/// can appear in the UI immediately on startup before the network connects.
/// </summary>
static class AccountCache
{
    public record CachedAccount(
        string AccountId, string LoginId, string DisplayName,
        string SyncRootPath, string Username);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private static string CachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fastmail", "FileNodeClient", "accounts.json");

    public static List<CachedAccount> Load()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<CachedAccount>>(json, Options) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"AccountCache: failed to load: {ex.Message}");
            return new();
        }
    }

    public static void Save(List<CachedAccount> accounts)
    {
        try
        {
            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(accounts, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"AccountCache: failed to save: {ex.Message}");
        }
    }
}
