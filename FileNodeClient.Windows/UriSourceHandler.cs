using System.Runtime.InteropServices;
using System.Text;
using FileNodeClient.Logging;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Storage.Provider;

namespace FileNodeClient.Windows;

/// <summary>
/// COM handler implementing IStorageProviderUriSource.
/// Explorer calls GetContentUriForPath when the user clicks "View online".
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
public sealed class UriSourceHandler : IStorageProviderUriSource
{
    // Stable CLSID — must not change across versions.
    public const string Clsid = "A7B3E2D1-9F4C-4A8B-B5E6-1D3C7F8A2E9B";

    public void GetPathForContentUri(string contentUri, StorageProviderGetPathForContentUriResult result)
    {
        // Reverse direction (URL → local path) — not implemented.
        result.Status = StorageProviderUriSourceStatus.NoSyncRoot;
    }

    public void GetContentInfoForPath(string path, StorageProviderGetContentInfoForPathResult result)
    {
        try
        {
            // 1. Find which sync root this path belongs to
            var syncRootId = FindSyncRootIdForPath(path);
            if (syncRootId == null)
            {
                result.Status = StorageProviderUriSourceStatus.NoSyncRoot;
                return;
            }

            // 2. Read the URL template from config
            var template = SyncRoot.ReadWebUrlTemplate(syncRootId);
            if (template == null)
            {
                result.Status = StorageProviderUriSourceStatus.FileNotFound;
                return;
            }

            // 3. Read the nodeId from the placeholder's FileIdentity
            var nodeId = ReadPlaceholderNodeId(path);
            if (nodeId == null)
            {
                result.Status = StorageProviderUriSourceStatus.FileNotFound;
                return;
            }

            // 4. Build the URL
            var url = template.Replace("{nodeId}", Uri.EscapeDataString(nodeId));
            result.ContentUri = url;
            result.Status = StorageProviderUriSourceStatus.Success;
        }
        catch (Exception ex)
        {
            Log.Error($"UriSourceHandler.GetContentUriForPath failed: {ex.Message}");
            result.Status = StorageProviderUriSourceStatus.FileNotFound;
        }
    }

    private static string? FindSyncRootIdForPath(string path)
    {
        try
        {
            var folder = global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(
                Path.GetDirectoryName(path)!).GetAwaiter().GetResult();
            var info = StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            return info?.Id;
        }
        catch
        {
            return null;
        }
    }

    private static unsafe string? ReadPlaceholderNodeId(string path)
    {
        try
        {
            var options = Directory.Exists(path) ? (FileOptions)0x02000000 : FileOptions.None;
            using var safeHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, options);
            var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());

            var buffer = new byte[256];
            fixed (byte* pBuffer = buffer)
            {
                uint returnedLen;
                var hr = PInvoke.CfGetPlaceholderInfo(
                    handle,
                    CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC,
                    pBuffer,
                    (uint)buffer.Length,
                    &returnedLen);

                if (hr.Failed)
                    return null;

                // CF_PLACEHOLDER_BASIC_INFO layout:
                //   PinState (int, offset 0)
                //   InSyncState (int, offset 4)
                //   FileId (long, offset 8)
                //   SyncRootFileId (long, offset 16)
                //   FileIdentityLength (uint, offset 24)
                //   FileIdentity (byte[], offset 28)
                const int fileIdentityLengthOffset = 24;
                const int fileIdentityOffset = 28;

                if (returnedLen < (uint)fileIdentityOffset)
                    return null;

                var identityLength = *(uint*)(pBuffer + fileIdentityLengthOffset);
                if (identityLength == 0 || returnedLen < (uint)fileIdentityOffset + identityLength)
                    return null;

                return Encoding.UTF8.GetString(pBuffer + fileIdentityOffset, (int)identityLength);
            }
        }
        catch
        {
            return null;
        }
    }
}
