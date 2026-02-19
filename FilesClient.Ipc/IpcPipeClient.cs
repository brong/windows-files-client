using System.IO.Pipes;
using System.Text;

namespace FilesClient.Ipc;

/// <summary>
/// Named pipe client with automatic reconnection.
/// Sends JSON Lines commands and receives events from the service.
/// </summary>
public sealed class IpcPipeClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _disposed;

    public event Action<IpcEvent>? EventReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _pipe?.IsConnected == true;

    public IpcPipeClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? IpcConstants.PipeName;
    }

    /// <summary>
    /// Start the client: connect to the pipe and begin reading events.
    /// Automatically reconnects on disconnection.
    /// </summary>
    public void Start(CancellationToken ct)
    {
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = ConnectAndReadLoopAsync(_readCts.Token);
    }

    /// <summary>
    /// Send a command to the service and return. Responses come back as events.
    /// </summary>
    public async Task SendCommandAsync(IpcCommand command, CancellationToken ct = default)
    {
        var json = IpcSerializer.Serialize(command);

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_writer == null || _pipe?.IsConnected != true)
                throw new InvalidOperationException("Not connected to service");

            await _writer.WriteLineAsync(json.AsMemory(), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task StopAsync()
    {
        _readCts?.Cancel();
        if (_readTask != null)
        {
            try { await _readTask; } catch { }
        }
        Cleanup();
        _readCts?.Dispose();
    }

    private async Task ConnectAndReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                ConnectionChanged?.Invoke(true);

                // Request initial status
                await SendCommandAsync(new GetStatusCommand(), ct);

                // Read events until disconnected
                while (!ct.IsCancellationRequested && _pipe?.IsConnected == true)
                {
                    string? line;
                    try
                    {
                        line = await _reader!.ReadLineAsync(ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }

                    if (line == null)
                        break; // Server disconnected

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var evt = IpcSerializer.DeserializeEvent(line);
                        if (evt != null)
                            EventReceived?.Invoke(evt);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[IPC Client] Event parse error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[IPC Client] Connection error: {ex.Message}");
            }

            // Disconnected — clean up and retry
            Cleanup();
            ConnectionChanged?.Invoke(false);

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(IpcConstants.ReconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            Cleanup();

            _pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await _pipe.ConnectAsync(IpcConstants.ReconnectDelayMs, ct);

            _reader = new StreamReader(_pipe, Encoding.UTF8);
            _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void Cleanup()
    {
        _reader?.Dispose();
        _reader = null;
        _writer?.Dispose();
        _writer = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readCts?.Cancel();
        _readCts?.Dispose();
        _writeLock.Dispose();
        _connectLock.Dispose();
        Cleanup();
    }
}
