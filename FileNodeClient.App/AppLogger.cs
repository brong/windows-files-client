using System.Text;
using FileNodeClient.Ipc;

namespace FileNodeClient.App;

static class AppLogger
{
    private static StreamWriter? _fileWriter;
    private static readonly object _fileLock = new();
    private static TextWriter? _originalConsole;

    public static void Initialize(bool debug)
    {
        if (debug)
        {
            _originalConsole = Console.Out;
            Log.MinLevel = LogLevel.Debug;
        }

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileNodeClient");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "app.log");
            _fileWriter = new StreamWriter(logPath, append: false, Encoding.UTF8)
            {
                AutoFlush = true,
            };
            _fileWriter.WriteLine($"=== FileNodeClient App log started at {DateTime.Now:O} ===");
        }
        catch
        {
            // Best-effort file logging
        }

        Log.Sink = (level, msg) =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var prefix = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "???",
            };

            if (debug)
                _originalConsole?.WriteLine($"{timestamp} [{prefix}] {msg}");

            if (_fileWriter == null) return;
            lock (_fileLock)
            {
                try
                {
                    _fileWriter.WriteLine($"{timestamp} [{prefix}] {msg}");
                }
                catch { }
            }
        };
    }
}
