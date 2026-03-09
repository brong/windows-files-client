using System.IO.Pipes;
using System.Text;
using FileNodeClient.Logging;

namespace FileNodeClient.Windows;

/// <summary>
/// Named pipe server that accepts thumbnail requests from the native COM
/// ThumbnailHandler DLL running in dllhost.exe (COM Surrogate). Each
/// connection handles one request/response.
/// </summary>
public sealed class ThumbnailPipeServer : IDisposable
{
    private const string PipeName = "FileNodeClient.Thumbnails";
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public void Start()
    {
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        Log.Info("Thumbnail pipe server started");
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
