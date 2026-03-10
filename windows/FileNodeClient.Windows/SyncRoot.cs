using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using FileNodeClient.Logging;

namespace FileNodeClient.Windows;

internal class SyncRoot : IDisposable
{
    // Stable provider GUID — must remain the same across all runs.
    private static readonly Guid ProviderId = new("f5e2d9a1-3b7c-4e8f-9a01-6c2d5e8f1b3a");

    private const string ProviderName = "FileNodeClient";

    private readonly string _syncRootPath;
    private readonly string _logPrefix;
    private string _syncRootId = ProviderName;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;
    private bool _registered;

    // Must keep a reference to the callback registrations & delegates
    // so the GC doesn't collect them while the connection is active.
    private CF_CALLBACK_REGISTRATION[]? _callbackRegistrations;
    private CF_CALLBACK[]? _callbackDelegates;

    public string SyncRootPath => _syncRootPath;

    public SyncRoot(string syncRootPath, string logPrefix)
    {
        _syncRootPath = syncRootPath;
        _logPrefix = logPrefix;
    }

    public async Task RegisterAsync(string displayName, string providerVersion, string accountId,
        string? iconPath = null, Uri? recycleBinUri = null, string? webUrlTemplate = null)
    {
        Directory.CreateDirectory(_syncRootPath);

        // Clean up stale registry entries from previous manual nav pane registration
        NavPaneIntegration.CleanupStaleEntries(ProviderId);

        // Sync root ID must be in the format: Provider!WindowsSID!AccountId
        // This is required for proper Shell integration and trust.
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        _syncRootId = $"{ProviderName}!{userSid}!{accountId}";

        var folder = await StorageFolder.GetFolderFromPathAsync(_syncRootPath);

        // If a different sync root is already registered at this path (e.g. server
        // was wiped and account IDs changed), unregister it first.
        try
        {
            var existing = StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            if (existing?.Id != null && existing.Id != _syncRootId)
            {
                Log.Info($"{_logPrefix} Unregistering stale sync root at same path: {existing.Id}");
                StorageProviderSyncRootManager.Unregister(existing.Id);
            }
        }
        catch { /* No existing sync root at this path */ }

        // Validate icon path — fall back to shell icon if file is missing
        var iconResource = iconPath != null && File.Exists(iconPath)
            ? iconPath
            : "%SystemRoot%\\system32\\shell32.dll,-1";

        var info = new StorageProviderSyncRootInfo();
        info.Id = _syncRootId;
        info.Path = folder;
        info.DisplayNameResource = displayName;
        info.IconResource = iconResource;
        info.HydrationPolicy = CfApiCapabilities.HasProgressiveHydration
            ? StorageProviderHydrationPolicy.Progressive
            : StorageProviderHydrationPolicy.Full;
        info.HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed;
        info.PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull;
        info.InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime
            | StorageProviderInSyncPolicy.DirectoryLastWriteTime;
        info.Version = providerVersion;
        info.ShowSiblingsAsGroup = false;
        info.HardlinkPolicy = StorageProviderHardlinkPolicy.None;
        info.ProtectionMode = StorageProviderProtectionMode.Personal;
        info.ProviderId = ProviderId;
        info.Context = CryptographicBuffer.ConvertStringToBinary(
            _syncRootId, BinaryStringEncoding.Utf8);

        if (recycleBinUri != null)
            info.RecycleBinUri = recycleBinUri;

        // Register with retry — cfapi can transiently fail with 0x80070490
        // (Element not found) if a previous sync root was recently unregistered.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                StorageProviderSyncRootManager.Register(info);
                break;
            }
            catch (System.Runtime.InteropServices.COMException ex) when (
                attempt < 3 && (uint)ex.HResult == 0x80070490)
            {
                Log.Warn($"{_logPrefix} Register attempt {attempt + 1} failed (0x80070490), retrying...");
                await Task.Delay(1000 * (attempt + 1));
            }
        }
        _registered = true;
        Log.Info($"{_logPrefix} Sync root registered: {_syncRootPath} (id={_syncRootId})");

        // Write AUMID to link sync root to MSIX package identity.
        // Without this, cloud file shell extensions (icons, thumbnails, context menus)
        // are never invoked by Explorer.
        WriteAumidIfPackaged(_syncRootId);

        // Write webUrlTemplate to config so the URI source COM handler can read it
        WriteWebUrlTemplate(_syncRootId, webUrlTemplate);
    }

    internal unsafe void Connect(CF_CALLBACK_REGISTRATION[] callbacks, CF_CALLBACK[] delegates)
    {
        // Keep references alive for the lifetime of the connection
        _callbackRegistrations = callbacks;
        _callbackDelegates = delegates;

        CF_CONNECTION_KEY key;
        PInvoke.CfConnectSyncRoot(
            _syncRootPath,
            callbacks,
            null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO
                | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH
                | (CfApiCapabilities.HasBlockSelfHydration
                    ? CF_CONNECT_FLAGS.CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION
                    : 0),
            &key).ThrowOnFailure();

        _connectionKey = key;
        _connected = true;
        Log.Info($"{_logPrefix} Sync root connected, ready for callbacks.");
    }

    internal CF_CONNECTION_KEY GetConnectionKey() => _connectionKey;

    internal void UpdateProviderStatus(CF_SYNC_PROVIDER_STATUS status)
    {
        if (_connected)
            PInvoke.CfUpdateSyncProviderStatus(_connectionKey, status);
    }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfReportSyncStatus(string SyncRootPath, IntPtr SyncStatus);

    /// <summary>
    /// Report a human-readable sync status message to Explorer.
    /// Pass null message to clear any previous status.
    /// Code high bit (0x80000000) = error; 0 = info/clear.
    /// </summary>
    /// <summary>
    /// Compute the total byte size needed for a CF_SYNC_STATUS with the given message.
    /// </summary>
    internal static int SyncStatusSize(string message) => 16 + message.Length * sizeof(char);

    /// <summary>
    /// Fill a caller-provided buffer with a CF_SYNC_STATUS struct.
    /// Buffer must be at least <see cref="SyncStatusSize"/> bytes.
    /// </summary>
    internal static void FillSyncStatus(Span<byte> buffer, uint code, string message)
    {
        const int headerSize = 16; // StructSize + Code + DescriptionOffset + DescriptionLength
        int stringBytes = message.Length * sizeof(char);
        int totalSize = headerSize + stringBytes;

        buffer.Slice(0, totalSize).Clear();
        BitConverter.TryWriteBytes(buffer, (uint)totalSize);
        BitConverter.TryWriteBytes(buffer.Slice(4), code);
        BitConverter.TryWriteBytes(buffer.Slice(8), (uint)headerSize);
        BitConverter.TryWriteBytes(buffer.Slice(12), (uint)stringBytes);
        MemoryMarshal.AsBytes(message.AsSpan()).CopyTo(buffer.Slice(headerSize));
    }

    internal unsafe void ReportSyncStatus(uint code, string? message)
    {
        if (!CfApiCapabilities.HasSyncStatus)
            return;

        try
        {
            if (message == null)
            {
                Marshal.ThrowExceptionForHR(CfReportSyncStatus(_syncRootPath, IntPtr.Zero));
                return;
            }

            Span<byte> buffer = stackalloc byte[SyncStatusSize(message)];
            FillSyncStatus(buffer, code, message);

            fixed (byte* ptr = buffer)
            {
                Marshal.ThrowExceptionForHR(CfReportSyncStatus(_syncRootPath, (IntPtr)ptr));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"CfReportSyncStatus failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        if (_connected)
        {
            PInvoke.CfDisconnectSyncRoot(_connectionKey).ThrowOnFailure();
            _connected = false;
            _callbackRegistrations = null;
            _callbackDelegates = null;
            Log.Info($"{_logPrefix} Sync root disconnected.");
        }
    }

    public void Unregister()
    {
        if (_registered)
        {
            StorageProviderSyncRootManager.Unregister(_syncRootId);
            _registered = false;
            Log.Info($"{_logPrefix} Sync root unregistered.");
        }
    }

    public void Dispose()
    {
        Disconnect();
        // Do NOT Unregister here — the sync root must stay registered so that
        // placeholder files remain valid between service restarts. Unregistering
        // causes File.Exists() to return false for cloud file placeholders
        // (ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING), which breaks cache warm start.
        // Unregister is only called explicitly via Clean() or account removal.
    }

    /// <summary>
    /// Config directory for storing per-sync-root settings (e.g. webUrlTemplate)
    /// that COM handlers can read at runtime.
    /// </summary>
    private static string GetConfigDir(string syncRootId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fastmail", "FileNodeClient", syncRootId);

    private static void WriteAumidIfPackaged(string syncRootId)
    {
        try
        {
            var package = global::Windows.ApplicationModel.Package.Current;
            var familyName = package.Id.FamilyName;
            // Use the "Service" application ID from AppxManifest.xml — that's the
            // process that hosts the cloud files COM handlers.
            var aumid = $"{familyName}!Service";

            var subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\{syncRootId}";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey, writable: true);
            if (key != null)
            {
                key.SetValue("AUMID", aumid, Microsoft.Win32.RegistryValueKind.String);
                Log.Info($"AUMID set: {aumid}");
            }
            else
            {
                Log.Warn($"Could not open SyncRootManager registry key for AUMID: {subKey}");
            }
        }
        catch (InvalidOperationException)
        {
            // Not running as a packaged app — no AUMID needed
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to write AUMID: {ex.Message}");
        }
    }

    private static void WriteWebUrlTemplate(string syncRootId, string? template)
    {
        var dir = GetConfigDir(syncRootId);
        var path = Path.Combine(dir, "webUrlTemplate");
        if (template != null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, template);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Read the webUrlTemplate for a sync root. Used by the URI source COM handler.
    /// </summary>
    internal static string? ReadWebUrlTemplate(string syncRootId)
    {
        var path = Path.Combine(GetConfigDir(syncRootId), "webUrlTemplate");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>
    /// Enumerate all sync roots registered on this machine that belong to our
    /// provider. Returns the account ID and path for each one.
    /// </summary>
    internal static List<(string AccountId, string Path)> GetRegisteredSyncRoots()
    {
        var results = new List<(string, string)>();
        try
        {
            var allRoots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            foreach (var root in allRoots)
            {
                if (root.Id == null || !root.Id.StartsWith(ProviderName + "!"))
                    continue;
                // Format: FileNodeClient!SID!AccountId
                var parts = root.Id.Split('!', 3);
                if (parts.Length < 3) continue;
                results.Add((parts[2], root.Path?.Path ?? ""));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to enumerate sync roots: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Unregister the sync root for the given account and delete all local files.
    /// This removes cfapi tracking first so that placeholder files can be deleted
    /// without error 0x8007016A ("cloud provider is not running").
    /// </summary>
    public static void Clean(string syncRootPath, string accountId)
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        var syncRootId = $"{ProviderName}!{userSid}!{accountId}";

        // 1. Unregister the sync root (WinRT layer)
        try
        {
            StorageProviderSyncRootManager.Unregister(syncRootId);
            Log.Info($"Unregistered sync root: {syncRootId}");
        }
        catch (Exception ex)
        {
            Log.Info($"Sync root not registered (or already cleaned): {ex.Message}");
        }

        // 2. Clean up NavPane / shell registry entries
        NavPaneIntegration.CleanupStaleEntries(ProviderId);

        // 3. Remove NTFS DENY ACLs so files can be deleted
        RemoveWriteProtectionRecursive(syncRootPath);

        // 4. Brief delay for the cloud filter driver to release handles after unregister
        Thread.Sleep(1000);

        // 4. Delete all local files in the sync root directory.
        //    After unregistering, cloud placeholder files still have reparse points that
        //    cause normal file operations to fail with ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING.
        //    We open each file with FILE_FLAG_OPEN_REPARSE_POINT to bypass the cloud filter
        //    driver, then delete via FILE_FLAG_DELETE_ON_CLOSE.
        if (Directory.Exists(syncRootPath))
        {
            DeleteCloudFilesRecursive(syncRootPath);

            // Verify deletion — if anything remains, retry after a longer delay
            if (Directory.Exists(syncRootPath))
            {
                Log.Info("  Some files remain, retrying after delay...");
                Thread.Sleep(3000);
                DeleteCloudFilesRecursive(syncRootPath);
            }

            if (Directory.Exists(syncRootPath))
                Log.Error($"  WARNING: Could not fully delete {syncRootPath}");
            else
                Log.Info($"Deleted sync root directory: {syncRootPath}");
        }
        else
        {
            Log.Info($"Sync root directory does not exist: {syncRootPath}");
        }
    }

    /// <summary>
    /// Detach a sync root: delete dehydrated placeholder files, remove empty
    /// directories (bottom-up), and unregister the sync root. Hydrated files
    /// are left in place so the user keeps their downloaded content.
    /// </summary>
    public static void Detach(string syncRootPath, string accountId)
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        var syncRootId = $"{ProviderName}!{userSid}!{accountId}";

        // 1. Unregister the sync root first so placeholders become normal files
        try
        {
            StorageProviderSyncRootManager.Unregister(syncRootId);
            Log.Info($"Unregistered sync root: {syncRootId}");
        }
        catch (Exception ex)
        {
            Log.Info($"Sync root not registered (or already cleaned): {ex.Message}");
        }

        NavPaneIntegration.CleanupStaleEntries(ProviderId);

        // Remove NTFS DENY ACLs so files can be deleted/detached
        RemoveWriteProtectionRecursive(syncRootPath);

        // Brief delay for the cloud filter driver to release handles
        Thread.Sleep(1000);

        if (!Directory.Exists(syncRootPath))
        {
            Log.Info($"Sync root directory does not exist: {syncRootPath}");
            return;
        }

        // 2. Walk directory recursively: delete dehydrated files, leave hydrated ones
        DeleteDehydratedFilesRecursive(syncRootPath);

        // 3. Remove empty directories bottom-up
        RemoveEmptyDirectories(syncRootPath);

        Log.Info($"Detach complete for: {syncRootPath}");
    }

    private const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    private static void DeleteDehydratedFilesRecursive(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path))
        {
            try
            {
                var attrs = (uint)File.GetAttributes(file);
                if ((attrs & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0)
                {
                    // Dehydrated placeholder — delete using reparse bypass
                    DeleteWithReparseBypass(file, isDirectory: false);
                }
                // Hydrated files are left in place
            }
            catch (Exception ex)
            {
                Log.Error($"  Failed to check/delete {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(path))
            DeleteDehydratedFilesRecursive(dir);
    }

    private static void RemoveEmptyDirectories(string path)
    {
        foreach (var dir in Directory.EnumerateDirectories(path))
            RemoveEmptyDirectories(dir);

        // Don't delete the sync root directory itself
        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            try
            {
                DeleteWithReparseBypass(path, isDirectory: true);
            }
            catch (Exception ex)
            {
                Log.Error($"  Failed to remove empty dir {path}: {ex.Message}");
            }
        }
    }

    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
    private const uint DELETE_ACCESS = 0x00010000; // DELETE access right

    /// <summary>
    /// Recursively deletes a directory containing cloud file placeholders.
    /// Uses CreateFileW with FILE_FLAG_OPEN_REPARSE_POINT to bypass the cloud filter
    /// driver, then FILE_FLAG_DELETE_ON_CLOSE to delete the file when the handle closes.
    /// </summary>
    private static void DeleteCloudFilesRecursive(string path)
    {
        var files = Directory.EnumerateFiles(path).ToList();
        var dirs = Directory.EnumerateDirectories(path).ToList();
        Log.Info($"  Deleting {files.Count} files and {dirs.Count} dirs in {Path.GetFileName(path)}/");

        foreach (var file in files)
            DeleteWithReparseBypass(file, isDirectory: false);

        foreach (var dir in dirs)
            DeleteCloudFilesRecursive(dir);

        // Verify files are gone before trying to delete the directory
        var remaining = Directory.EnumerateFileSystemEntries(path).ToList();
        if (remaining.Count > 0)
            Log.Error($"  {remaining.Count} items remain in {Path.GetFileName(path)}/: {string.Join(", ", remaining.Select(Path.GetFileName))}");

        DeleteWithReparseBypass(path, isDirectory: true);
    }

    private static void DeleteWithReparseBypass(string path, bool isDirectory)
    {
        try
        {
            var flags = FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_DELETE_ON_CLOSE;
            if (isDirectory)
                flags |= FILE_FLAG_BACKUP_SEMANTICS;

            using var handle = PInvoke.CreateFile(
                path,
                DELETE_ACCESS,
                global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE
                    | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                    | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null,
                global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                (global::Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES)flags,
                null);

            if (handle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                Log.Error($"  Failed to open for delete (err={err}): {Path.GetFileName(path)}");
            }
            // File/directory is deleted when handle is disposed (DELETE_ON_CLOSE)
            // Check if file still exists after handle disposal happens at end of using block
        }
        catch (Exception ex)
        {
            // Fall back to normal delete
            try
            {
                if (isDirectory) Directory.Delete(path);
                else File.Delete(path);
            }
            catch
            {
                Log.Error($"  Failed to delete {(isDirectory ? "directory " : "")}{path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Add or remove a DENY ACL on a directory to prevent the current user from
    /// creating files/subdirectories inside it.  Uses InheritanceFlags.None so
    /// only this directory is affected — writable children are not impacted.
    /// </summary>
    internal static void SetDirectoryWriteProtection(string path, bool protect)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var dirInfo = new DirectoryInfo(path);
            var acl = dirInfo.GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User!;
            var rule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Deny);

            if (protect)
                acl.AddAccessRule(rule);
            else
                acl.RemoveAccessRule(rule);

            dirInfo.SetAccessControl(acl);
            Log.Info($"  {(protect ? "Protected" : "Unprotected")} folder: {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to {(protect ? "set" : "remove")} write protection on {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove DENY ACLs from all subdirectories under the given root.
    /// Best-effort — exceptions on individual directories are swallowed.
    /// </summary>
    internal static void RemoveWriteProtectionRecursive(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
            {
                try { SetDirectoryWriteProtection(dir, false); }
                catch { /* best-effort */ }
            }
            // Also remove from root itself
            try { SetDirectoryWriteProtection(rootPath, false); }
            catch { /* best-effort */ }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to remove write protections under {rootPath}: {ex.Message}");
        }
    }
}
