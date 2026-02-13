using Microsoft.Win32;

namespace FilesClient.Windows;

/// <summary>
/// Registers a shell namespace extension under HKCU so that the sync root
/// appears as a top-level entry in Explorer's navigation pane (alongside
/// OneDrive, This PC, etc.).
///
/// See: https://learn.microsoft.com/en-us/windows/win32/shell/integrate-cloud-storage
/// </summary>
internal class NavPaneIntegration
{
    // Shell instance object CLSID — tells Explorer to treat this as a folder view.
    private const string ShellInstanceClsid = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";

    // SFGAO flags: CANCOPY | CANLINK | STORAGE | HASPROPSHEET | STORAGEANCESTOR |
    //              FILESYSANCESTOR | FOLDER | FILESYSTEM | HASSUBFOLDER
    private const uint ShellFolderAttributes = 0xF080004D;

    // FolderValueFlags: combines WebDAV folder behaviour flags
    private const uint FolderValueFlags = 0x28;

    // SortOrderIndex: 0x42 places it near OneDrive in the nav pane
    private const uint SortOrderIndex = 0x42;

    private readonly string _clsid;
    private readonly string _displayName;
    private readonly string _syncRootPath;
    private readonly string _iconResource;

    public NavPaneIntegration(Guid providerId, string displayName, string syncRootPath, string iconResource)
    {
        // Use the provider GUID as the CLSID for the namespace extension,
        // ensuring it's stable across runs.
        _clsid = providerId.ToString("B"); // {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
        _displayName = displayName;
        _syncRootPath = syncRootPath;
        _iconResource = iconResource;
    }

    public void Register()
    {
        var clsidPath = $@"Software\Classes\CLSID\{_clsid}";

        using (var key = Registry.CurrentUser.CreateSubKey(clsidPath))
        {
            key.SetValue("", _displayName);
            key.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            key.SetValue("SortOrderIndex", (int)SortOrderIndex, RegistryValueKind.DWord);
        }

        // DefaultIcon
        using (var key = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\DefaultIcon"))
        {
            key.SetValue("", _iconResource);
        }

        // InProcServer32 — use shell32.dll to emulate standard folder behaviour
        using (var key = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\InProcServer32"))
        {
            key.SetValue("", "%systemroot%\\system32\\shell32.dll", RegistryValueKind.ExpandString);
        }

        // Instance — shell instance object
        using (var key = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\Instance"))
        {
            key.SetValue("CLSID", ShellInstanceClsid);
        }

        // InitPropertyBag — file system attributes and target path
        using (var key = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\Instance\InitPropertyBag"))
        {
            // FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_READONLY
            key.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
            key.SetValue("TargetFolderPath", _syncRootPath, RegistryValueKind.ExpandString);
        }

        // ShellFolder — flags controlling shell behaviour
        using (var key = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\ShellFolder"))
        {
            key.SetValue("FolderValueFlags", (int)FolderValueFlags, RegistryValueKind.DWord);
            key.SetValue("Attributes", unchecked((int)ShellFolderAttributes), RegistryValueKind.DWord);
        }

        // Register in Desktop\NameSpace so it appears as a child of the desktop
        using (var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{_clsid}"))
        {
            key.SetValue("", _displayName);
        }

        // Hide from the actual Desktop surface (only show in nav pane)
        using (var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"))
        {
            key.SetValue(_clsid, 1, RegistryValueKind.DWord);
        }

        Console.WriteLine($"Nav pane registered: {_displayName}");
    }

    public void Unregister()
    {
        var clsidPath = $@"Software\Classes\CLSID\{_clsid}";

        try { Registry.CurrentUser.DeleteSubKeyTree(clsidPath, throwOnMissingSubKey: false); } catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{_clsid}",
                throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", writable: true);
            key?.DeleteValue(_clsid, throwOnMissingValue: false);
        }
        catch { }

        Console.WriteLine("Nav pane unregistered.");
    }
}
