using System.IO.Pipes;
using System.Text;
using FileNodeClient.Ipc;
using Microsoft.Win32;

namespace FileNodeClient.Windows;

/// <summary>
/// Named pipe server that accepts thumbnail requests from the native COM
/// ThumbnailHandler DLL loaded in-process into Explorer. Each connection
/// handles one request/response.
/// </summary>
public sealed class ThumbnailPipeServer : IDisposable
{
    private const string PipeName = "FileNodeClient.Thumbnails";
    private const string ThumbnailClsid = "{B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1C}";
    private const string UriSourceClsid = "{A7B3E2D1-9F4C-4A8B-B5E6-1D3C7F8A2E9B}";
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public void Start()
    {
        // Don't register CLSIDs in real HKCU — the MSIX manifest's
        // com:SurrogateServer handles COM registration via virtual registry.
        // Real HKCU entries conflict with/override the MSIX registration.
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        Log.Info("Thumbnail pipe server started");
    }

    /// <summary>
    /// Register the native handler DLL as InprocServer32 in the real Windows
    /// registry for both CLSIDs (ThumbnailHandler and UriSourceHandler).
    /// This ensures COM can always activate the handlers even when the MSIX
    /// virtual registry has stale entries, preventing the windows.storage.dll
    /// crash that occurs when handler CLSIDs can't be resolved.
    /// </summary>
    private static void RegisterNativeDll()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var dllPath = Path.Combine(baseDir, "FileNodeClient.ThumbnailExtension.dll");

            if (!File.Exists(dllPath))
            {
                Log.Warn($"ThumbnailPipeServer: native DLL not found at {dllPath}, skipping COM registration");
                return;
            }

            RegisterInprocServer(ThumbnailClsid, dllPath);
            RegisterInprocServer(UriSourceClsid, dllPath);

            Log.Info($"ThumbnailPipeServer: registered native DLL at {dllPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"ThumbnailPipeServer: COM registration failed: {ex.Message}");
        }
    }

    private static void RegisterInprocServer(string clsid, string dllPath)
    {
        using var clsidKey = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\CLSID\{clsid}");

        using var inprocKey = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\CLSID\{clsid}\InprocServer32");
        inprocKey.SetValue(null, dllPath);
        inprocKey.SetValue("ThreadingModel", "Both");
    }

    /// <summary>
    /// Remove the registry-based COM registration.
    /// </summary>
    public static void UnregisterComServer()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\CLSID\{ThumbnailClsid}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\CLSID\{UriSourceClsid}", throwOnMissingSubKey: false);
            Log.Info("ThumbnailPipeServer: unregistered COM servers");
        }
        catch (Exception ex)
        {
            Log.Error($"ThumbnailPipeServer: COM unregistration failed: {ex.Message}");
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }

            // Handle each connection on its own task so we can accept the next one
            _ = Task.Run(() => HandleConnection(pipe), ct);
        }
    }

    private static async Task HandleConnection(NamedPipeServerStream pipe)
    {
        try
        {
            // Read request: 4-byte LE path-length + UTF-8 path + 4-byte LE cx
            var lenBuf = new byte[4];
            if (await ReadExactAsync(pipe, lenBuf, 4) != 4)
                return;

            var pathLen = BitConverter.ToInt32(lenBuf, 0);
            if (pathLen <= 0 || pathLen > 32768)
                return;

            var pathBuf = new byte[pathLen];
            if (await ReadExactAsync(pipe, pathBuf, pathLen) != pathLen)
                return;

            if (await ReadExactAsync(pipe, lenBuf, 4) != 4)
                return;

            var cx = BitConverter.ToUInt32(lenBuf, 0);
            var path = Encoding.UTF8.GetString(pathBuf);

            Log.Debug($"ThumbnailPipeServer: request for {path} at {cx}px");

            // Delegate to ThumbnailService
            var pngBytes = ThumbnailService.GetThumbnailForPath(path, cx);

            // Write response: 4-byte LE length + PNG bytes (0 = failure)
            if (pngBytes != null && pngBytes.Length > 0)
            {
                var respLen = BitConverter.GetBytes(pngBytes.Length);
                await pipe.WriteAsync(respLen);
                await pipe.WriteAsync(pngBytes);
            }
            else
            {
                var zero = BitConverter.GetBytes(0);
                await pipe.WriteAsync(zero);
            }

            await pipe.FlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"ThumbnailPipeServer: connection error: {ex.Message}");
        }
        finally
        {
            try { pipe.Disconnect(); } catch { }
            pipe.Dispose();
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead));
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
