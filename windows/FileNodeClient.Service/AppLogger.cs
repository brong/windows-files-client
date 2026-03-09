using System.Text;
using FileNodeClient.Logging;

namespace FileNodeClient.Service;

static class AppLogger
{
    private static StreamWriter? _fileWriter;
    private static readonly object _fileLock = new();
    private static TextWriter? _originalConsole;

    public static string? LogFilePath { get; private set; }

    public static void Initialize(bool debug)
    {
        // Touch the ETW EventSource so it registers with the OS immediately
        _ = FileNodeClientEventSource.Instance;

        if (debug)
        {
            _originalConsole = Console.Out;
            Log.MinLevel = LogLevel.Debug;

            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Fastmail", "FileNodeClient");
                Directory.CreateDirectory(logDir);
                LogFilePath = Path.Combine(logDir, "debug.log");
                _fileWriter = new StreamWriter(LogFilePath, append: false, Encoding.UTF8)
                {
                    AutoFlush = true,
                };
                _fileWriter.WriteLine($"=== FileNodeClient debug log started at {DateTime.Now:O} ===");
            }
            catch
            {
                // Best-effort file logging
            }
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

            WriteToFile(prefix, msg);

            // Write to ETW (appears in Event Viewer under
            // Applications and Services Logs > Fastmail-FileNodeClient > Operational)
            var etw = FileNodeClientEventSource.Instance;
            try
            {
                switch (level)
                {
                    case LogLevel.Debug:
                        etw.DebugMessage(msg);
                        break;
                    case LogLevel.Info:
                        etw.InfoMessage(msg);
                        break;
                    case LogLevel.Warning:
                        etw.WarningMessage(msg);
                        break;
                    case LogLevel.Error:
                        etw.ErrorMessage(msg);
                        break;
                }
            }
            catch { /* best-effort ETW */ }
        };
    }

    private static void WriteToFile(string prefix, string line)
    {
        if (_fileWriter == null) return;
        lock (_fileLock)
        {
            try
            {
                _fileWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{prefix}] {line}");
            }
            catch { }
        }
    }
}
