using Microsoft.Win32;

namespace FilesClient.Windows;

/// <summary>
/// Cleans up stale registry-based namespace extension entries that were
/// created by an earlier version of the app. The WinRT
/// StorageProviderSyncRootManager.Register() handles nav pane integration
/// automatically, so the manual registry approach is no longer needed.
/// </summary>
internal static class NavPaneIntegration
{
    /// <summary>
    /// Remove any leftover registry entries from the old manual registration.
    /// </summary>
    public static void CleanupStaleEntries(Guid providerId)
    {
        var clsid = providerId.ToString("B");
        var clsidPath = $@"Software\Classes\CLSID\{clsid}";

        try { Registry.CurrentUser.DeleteSubKeyTree(clsidPath, throwOnMissingSubKey: false); } catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{clsid}",
                throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", writable: true);
            key?.DeleteValue(clsid, throwOnMissingValue: false);
        }
        catch { }
    }
}
