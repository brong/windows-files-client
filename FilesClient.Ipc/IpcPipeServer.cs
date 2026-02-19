using System.IO.Pipes;
using System.Text;

namespace FilesClient.Ipc;

/// <summary>
/// Multi-client named pipe server. Accepts connections in a loop,
/// reads JSON Lines commands, and dispatches them to a handler.
/// Can broadcast events to all connected clients.
/// </summary>
public sealed class IpcPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcCommand, CancellationToken, Task<IpcEvent?>> _commandHandler;
    private readonly List<ConnectedClient> _clients = new();
    private readonly object _clientsLock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    public IpcPipeServer(Func<IpcCommand, CancellationToken, Task<IpcEvent?>> commandHandler,
        string? pipeName = null)
    {
        _pipeName = pipeName ?? IpcConstants.PipeName;
        _commandHandler = commandHandler;
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        List<ConnectedClient> clients;
        lock (_clientsLock)
            clients = _clients.ToList();

        foreach (var client in clients)
        {
            try { client.Dispose(); } catch { }
        }

        if (_acceptTask != null)
        {
            try { await _acceptTask; } catch { }
        }

        _cts?.Dispose();
    }

    /// <summary>
    /// Broadcast an event to all connected clients.
    /// </summary>
    public async Task BroadcastAsync(IpcEvent evt)
    {
        var json = IpcSerializer.Serialize(evt);

        List<ConnectedClient> clients;
        lock (_clientsLock)
            clients = _clients.ToList();

        var failed = new List<ConnectedClient>();

        foreach (var client in clients)
        {
            try
            {
                await client.WriteLineAsync(json);
            }
            catch
            {
                failed.Add(client);
            }
        }

        if (failed.Count > 0)
        {
            lock (_clientsLock)
            {
                foreach (var c in failed)
                {
                    _clients.Remove(c);
                    try { c.Dispose(); } catch { }
                }
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    IpcConstants.BufferSize,
                    IpcConstants.BufferSize);

                await pipe.WaitForConnectionAsync(ct);

                var client = new ConnectedClient(pipe);
                lock (_clientsLock)
                    _clients.Add(client);

                Console.WriteLine("[IPC] Client connected");

                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[IPC] Accept error: {ex.Message}");
                pipe?.Dispose();
                // Brief delay before retrying
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    private async Task HandleClientAsync(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var line in client.ReadLinesAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var command = IpcSerializer.DeserializeCommand(line);
                    if (command == null)
                    {
                        Console.Error.WriteLine($"[IPC] Unknown command: {line}");
                        continue;
                    }

                    var response = await _commandHandler(command, ct);
                    if (response != null)
                    {
                        var json = IpcSerializer.Serialize(response);
                        await client.WriteLineAsync(json);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[IPC] Command handling error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[IPC] Client error: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("[IPC] Client disconnected");
            lock (_clientsLock)
                _clients.Remove(client);
            client.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private sealed class ConnectedClient : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ConnectedClient(NamedPipeServerStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8);
            _writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
        }

        public async IAsyncEnumerable<string> ReadLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await _reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException) { yield break; }
                catch (IOException) { yield break; }

                if (line == null)
                    yield break; // Client disconnected

                yield return line;
            }
        }

        public async Task WriteLineAsync(string line)
        {
            await _writeLock.WaitAsync();
            try
            {
                await _writer.WriteLineAsync(line);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            _writeLock.Dispose();
            _reader.Dispose();
            _writer.Dispose();
            _pipe.Dispose();
        }
    }
}
