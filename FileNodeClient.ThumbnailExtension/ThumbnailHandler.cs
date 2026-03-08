using System.Drawing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace FileNodeClient.ThumbnailExtension;

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
/// Runs in-process in dllhost.exe (COM Surrogate) via comhost.dll.
/// Communicates with Service.exe over a named pipe to get thumbnail PNG bytes.
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

    // Timeout for connecting to Service.exe and receiving response
    private const int PipeTimeoutMs = 10_000;

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

            var pngBytes = RequestThumbnailFromService(_path, cx);
            if (pngBytes == null || pngBytes.Length == 0)
                return E_FAIL;

            // Decode PNG → Bitmap → HBITMAP
            using var ms = new MemoryStream(pngBytes);
            using var bitmap = new Bitmap(ms);
            phbmp = bitmap.GetHbitmap(Color.Transparent);
            pdwAlpha = WTSAT_ARGB;
            return S_OK;
        }
        catch
        {
            return E_FAIL;
        }
    }

    /// <summary>
    /// Connect to the Service.exe named pipe and request a thumbnail.
    /// Protocol: Request  = 4-byte LE path-length + UTF-8 path + 4-byte LE cx
    ///           Response = 4-byte LE PNG-length + PNG bytes (0 = failure)
    /// </summary>
    private static byte[]? RequestThumbnailFromService(string path, uint cx)
    {
        using var pipe = new NamedPipeClientStream(
            ".", "FileNodeClient.Thumbnails", PipeDirection.InOut);

        pipe.Connect(PipeTimeoutMs);

        // Write request
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var pathLen = BitConverter.GetBytes(pathBytes.Length);
        pipe.Write(pathLen, 0, 4);
        pipe.Write(pathBytes, 0, pathBytes.Length);

        var cxBytes = BitConverter.GetBytes(cx);
        pipe.Write(cxBytes, 0, 4);
        pipe.Flush();

        // Read response: 4-byte LE length + PNG bytes
        var lenBuf = new byte[4];
        if (ReadExact(pipe, lenBuf, 4) != 4)
            return null;

        var pngLen = BitConverter.ToInt32(lenBuf, 0);
        if (pngLen <= 0)
            return null;

        var pngBuf = new byte[pngLen];
        if (ReadExact(pipe, pngBuf, pngLen) != pngLen)
            return null;

        return pngBuf;
    }

    private static int ReadExact(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
