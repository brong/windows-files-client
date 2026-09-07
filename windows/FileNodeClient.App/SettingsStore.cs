using System.Text.Json;
using FileNodeClient.Logging;
using FileNodeClient.Windows;

namespace FileNodeClient.App;

/// <summary>
/// Persists global (not per-account) app preferences to settings.json. Currently just the
/// content-conflict resolution strategy. Mirrors <see cref="AccountCache"/>'s simple
/// load/save-on-change model.
/// </summary>
static class SettingsStore
{
    public record AppSettings(string ConflictResolution = ConflictCopyValue);

    public const string ConflictCopyValue = "conflictCopy";
    public const string NewestWinsValue = "newestWins";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fastmail", "FileNodeClient", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"SettingsStore: failed to load: {ex.Message}");
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"SettingsStore: failed to save: {ex.Message}");
        }
    }

    /// <summary>Map the persisted/IPC string to the engine enum. Unknown values fall back to
    /// the always-safe conflict-copy strategy.</summary>
    public static ConflictResolution ParseStrategy(string? value) =>
        value == NewestWinsValue ? ConflictResolution.NewestWins : ConflictResolution.ConflictCopy;

    public static string FormatStrategy(ConflictResolution strategy) =>
        strategy == ConflictResolution.NewestWins ? NewestWinsValue : ConflictCopyValue;
}
