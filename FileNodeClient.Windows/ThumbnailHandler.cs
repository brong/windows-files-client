using System.Drawing;
using System.Runtime.InteropServices;
using FileNodeClient.Ipc;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FileNodeClient.Windows;

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

[ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IThumbnailProvider
{
    [PreserveSig]
    int GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha);
}

/// <summary>
/// COM handler implementing IThumbnailProvider for dehydrated cloud file placeholders.
/// Returns image thumbnails via Blob/convert without hydrating the file.
/// Runs in-process in Service.exe (ExeServer via MSIX manifest).
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
public sealed class ThumbnailHandler : IInitializeWithFile, IThumbnailProvider
{
    public const string Clsid = "B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1C";
    private string? _path;

    private const int S_OK = 0;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const uint WTSAT_ARGB = 2;

    void IInitializeWithFile.Initialize(string pszFilePath, uint grfMode)
    {
        _path = pszFilePath;
    }

    int IThumbnailProvider.GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha)
    {
        phbmp = IntPtr.Zero;
        pdwAlpha = 0;

        try
        {
            if (_path == null)
                return E_FAIL;

            // Find which sync root this file belongs to
            var syncRootPath = ThumbnailService.FindSyncRoot(_path);
            if (syncRootPath == null)
                return E_FAIL;

            // Read the nodeId from the placeholder's FileIdentity
            var nodeId = ReadPlaceholderNodeId(_path);
            if (nodeId == null)
                return E_FAIL;

            // Get thumbnail PNG bytes from the service
            var pngBytes = ThumbnailService.GetThumbnail(syncRootPath, nodeId, cx);
            if (pngBytes == null)
                return E_FAIL;

            // Decode PNG → Bitmap → HBITMAP
            using var ms = new MemoryStream(pngBytes);
            using var bitmap = new Bitmap(ms);
            phbmp = bitmap.GetHbitmap(Color.Transparent);
            pdwAlpha = WTSAT_ARGB;
            return S_OK;
        }
        catch (Exception ex)
        {
            Log.Error($"ThumbnailHandler: error for {_path}: {ex.Message}");
            return E_FAIL;
        }
    }

    private static unsafe string? ReadPlaceholderNodeId(string path)
    {
        try
        {
            // Use FILE_WRITE_ATTRIBUTES (0x100) to avoid triggering FETCH_DATA on dehydrated files.
            // FileAccess.Read would cause cfapi to hydrate the placeholder.
            using var handle = PInvoke.CreateFile(
                path,
                0x00000100, // FILE_WRITE_ATTRIBUTES
                global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                    | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE
                    | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                default,
                null);

            if (handle.IsInvalid)
                return null;

            var cfHandle = new global::Windows.Win32.Foundation.HANDLE(handle.DangerousGetHandle());

            var buffer = new byte[256];
            fixed (byte* pBuffer = buffer)
            {
                uint returnedLen;
                var hr = PInvoke.CfGetPlaceholderInfo(
                    cfHandle,
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

                return System.Text.Encoding.UTF8.GetString(pBuffer + fileIdentityOffset, (int)identityLength);
            }
        }
        catch
        {
            return null;
        }
    }
}
