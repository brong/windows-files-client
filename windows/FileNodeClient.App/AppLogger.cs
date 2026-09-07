using System.Text;
using FileNodeClient.Logging;

namespace FileNodeClient.App;

static class AppLogger
{
    private static StreamWriter? _fileWriter;
    private static readonly object _fileLock = new();

    public static string? LogFilePath { get; private set; }

    public static void Initialize(bool debug)
    {
        if (debug)
            Log.MinLevel = LogLevel.Debug;

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fastmail", "FileNodeClient");
            Directory.CreateDirectory(logDir);
            LogFilePath = Path.Combine(logDir, "debug.log");
            // Keep the previous run's log: a startup failure is usually explained by
            // what the last instance did just before it exited.
            var previous = Path.Combine(logDir, "debug.prev.log");
            try { if (File.Exists(LogFilePath)) File.Move(LogFilePath, previous, overwrite: true); }
            catch { /* another instance may still hold it */ }
            _fileWriter = new StreamWriter(LogFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            _fileWriter.WriteLine($"=== FileNodeClient log started at {DateTime.Now:O} ===");
        }
        catch
        {
            // Best-effort file logging
        }

        Log.Sink = (level, msg) =>
        {
            if (_fileWriter == null) return;
            var prefix = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "???",
            };
            lock (_fileLock)
            {
                try { _fileWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{prefix}] {msg}"); }
                catch { }
            }
        };
    }
}
