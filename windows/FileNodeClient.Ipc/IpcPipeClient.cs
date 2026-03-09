using FileNodeClient.Logging;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace FileNodeClient.Ipc;

/// <summary>
/// Named pipe client with automatic reconnection.
/// Sends JMAP-style requests with correlation IDs and receives
/// responses and push events from the service.
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

    private long _nextId;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();

    public event Action<IpcPush>? PushReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _pipe?.IsConnected == true;

    public IpcPipeClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? IpcConstants.PipeName;
    }

    /// <summary>
    /// Start the client: connect to the pipe and begin reading.
    /// Automatically reconnects on disconnection.
    /// </summary>
    public void Start(CancellationToken ct)
    {
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = ConnectAndReadLoopAsync(_readCts.Token);
    }

    /// <summary>
    /// Send a request and wait for its correlated response.
    /// Returns IpcResponse on success, IpcError on error.
    /// </summary>
    public async Task<IpcMessage> CallAsync(string method, object? @params = null,
        CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed))
                removed.TrySetCanceled();
        });

        var json = IpcSerializer.SerializeRequest(id, method, @params);

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_writer == null || _pipe?.IsConnected != true)
                throw new InvalidOperationException("Not connected to service");

            await _writer.WriteLineAsync(json.AsMemory(), ct);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }

        return await tcs.Task;
    }

    public async Task StopAsync()
    {
        _readCts?.Cancel();
        if (_readTask != null)
        {
            try { await _readTask; } catch { }
        }
        FailAllPending();
        Cleanup();
        _readCts?.Dispose();
    }

    private async Task ConnectAndReadLoopAsync(CancellationToken ct)
    {
        bool wasConnected = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Cleanup();
                if (wasConnected)
                {
                    wasConnected = false;
                    FailAllPending();
                    ConnectionChanged?.Invoke(false);
                    await Task.Delay(IpcConstants.ReconnectDelayMs, ct);
                }

                await ConnectAsync(ct);
                wasConnected = true;
                ConnectionChanged?.Invoke(true);

                // Request initial status — fire result as a push so consumers handle it uniformly
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await CallAsync("getStatus", null, ct);
                        if (result is IpcResponse response)
                            PushReceived?.Invoke(new IpcPush("statusSnapshot", response.Result));
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[IPC Client] Initial getStatus failed: {ex.Message}");
                    }
                }, ct);

                // Use a linked CTS so the keepalive can cancel reads on pipe failure
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // Keepalive: periodically ping to detect broken pipes
                _ = Task.Run(async () =>
                {
                    int consecutiveFailures = 0;
                    const int maxFailures = 3;
                    try
                    {
                        while (!readCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(5_000, readCts.Token);
                            try
                            {
                                await CallAsync("ping", null, readCts.Token);
                                consecutiveFailures = 0;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                consecutiveFailures++;
                                if (consecutiveFailures >= maxFailures)
                                {
                                    Log.Warn($"[IPC Client] Keepalive failed {maxFailures} times, disconnecting: {ex.Message}");
                                    try { readCts.Cancel(); } catch { }
                                    return;
                                }
                                Log.Debug($"[IPC Client] Keepalive ping failed ({consecutiveFailures}/{maxFailures}): {ex.Message}");
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, readCts.Token);

                // Read loop
                while (!readCts.Token.IsCancellationRequested && _pipe?.IsConnected == true)
                {
                    string? line;
                    try
                    {
                        line = await _reader!.ReadLineAsync(readCts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }

                    if (line == null)
                        break; // Server disconnected

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var message = IpcSerializer.ParseMessage(line);
                        switch (message)
                        {
                            case IpcResponse response:
                                if (_pending.TryRemove(response.Id, out var resTcs))
                                    resTcs.TrySetResult(response);
                                break;

                            case IpcError error:
                                if (_pending.TryRemove(error.Id, out var errTcs))
                                    errTcs.TrySetResult(error);
                                break;

                            case IpcPush push:
                                PushReceived?.Invoke(push);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[IPC Client] Message parse error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException)
            {
                // ConnectAsync timed out — service not available yet, will retry
            }
            catch (Exception ex)
            {
                Log.Error($"[IPC Client] Connection error: {ex.Message}");
                // Brief delay to avoid tight spin on unexpected errors
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Final cleanup on exit
        FailAllPending();
        Cleanup();
        if (wasConnected)
            ConnectionChanged?.Invoke(false);
    }

    private void FailAllPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new InvalidOperationException("Disconnected from service"));
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
        FailAllPending();
        Cleanup();
    }
}
