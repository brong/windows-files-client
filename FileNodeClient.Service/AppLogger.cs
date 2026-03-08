using System.Diagnostics;
using System.Text;
using FileNodeClient.Ipc;

namespace FileNodeClient.Service;

static class AppLogger
{
    private const string EventSourceName = "FileNodeClient";
    private const string EventLogName = "Application";

    private static EventLog? _eventLog;
    private static StreamWriter? _fileWriter;
    private static readonly object _fileLock = new();
    private static TextWriter? _originalConsole;

    public static string? LogFilePath { get; private set; }

    public static void Initialize(bool debug)
    {
        // Create event source if it doesn't exist (requires admin on first run)
        try
        {
            if (!EventLog.SourceExists(EventSourceName))
                EventLog.CreateEventSource(EventSourceName, EventLogName);
        }
        catch (System.Security.SecurityException)
        {
            // Non-admin -- source may already exist or we'll use generic source
        }

        try
        {
            _eventLog = new EventLog(EventLogName) { Source = EventSourceName };
        }
        catch
        {
            // If we can't create the event log, logging is best-effort
        }

        if (debug)
        {
            _originalConsole = Console.Out;
            Log.MinLevel = LogLevel.Debug;

            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileNodeClient");
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

            var entryType = level switch
            {
                LogLevel.Warning => EventLogEntryType.Warning,
                LogLevel.Error => EventLogEntryType.Error,
                _ => EventLogEntryType.Information,
            };

            try { _eventLog?.WriteEntry(msg, entryType); }
            catch { /* best-effort */ }
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
