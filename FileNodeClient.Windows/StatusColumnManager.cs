using FileNodeClient.Ipc;
using Windows.Storage;
using Windows.Storage.Provider;

namespace FileNodeClient.Windows;

/// <summary>
/// Pushes custom property values (Status, Sharing) to Explorer columns
/// via StorageProviderItemProperties.SetAsync(). Requires MSIX package identity.
/// </summary>
internal static class StatusColumnManager
{
    public const int StatusPropertyId = 0;
    public const int SharingPropertyId = 1;

    public static async Task SetStatusAsync(string filePath, string status)
    {
        try
        {
            var item = await StorageFile.GetFileFromPathAsync(filePath);
            var props = new List<StorageProviderItemProperty>
            {
                new() { Id = StatusPropertyId, Value = status, IconResource = "" }
            };
            await StorageProviderItemProperties.SetAsync(item, props);
        }
        catch (Exception ex)
        {
            Log.Info($"StatusColumnManager: SetStatus failed for {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    public static async Task SetSharingAsync(string filePath, string sharing)
    {
        try
        {
            var item = await StorageFile.GetFileFromPathAsync(filePath);
            var props = new List<StorageProviderItemProperty>
            {
                new() { Id = SharingPropertyId, Value = sharing, IconResource = "" }
            };
            await StorageProviderItemProperties.SetAsync(item, props);
        }
        catch (Exception ex)
        {
            Log.Info($"StatusColumnManager: SetSharing failed for {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    public static async Task SetPropertiesAsync(string filePath, string status, string? sharing = null)
    {
        try
        {
            var item = await StorageFile.GetFileFromPathAsync(filePath);
            var props = new List<StorageProviderItemProperty>
            {
                new() { Id = StatusPropertyId, Value = status, IconResource = "" }
            };
            if (sharing != null)
                props.Add(new() { Id = SharingPropertyId, Value = sharing, IconResource = "" });
            await StorageProviderItemProperties.SetAsync(item, props);
        }
        catch (Exception ex)
        {
            Log.Info($"StatusColumnManager: SetProperties failed for {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    public static async Task SetDirectoryPropertiesAsync(string dirPath, string status, string? sharing = null)
    {
        try
        {
            var item = await StorageFolder.GetFolderFromPathAsync(dirPath);
            var props = new List<StorageProviderItemProperty>
            {
                new() { Id = StatusPropertyId, Value = status, IconResource = "" }
            };
            if (sharing != null)
                props.Add(new() { Id = SharingPropertyId, Value = sharing, IconResource = "" });
            await StorageProviderItemProperties.SetAsync(item, props);
        }
        catch (Exception ex)
        {
            Log.Info($"StatusColumnManager: SetDirectoryProperties failed for {Path.GetFileName(dirPath)}: {ex.Message}");
        }
    }
}
