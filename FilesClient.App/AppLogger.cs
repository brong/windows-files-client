using System.Diagnostics;
using System.Text;

namespace FilesClient.App;

/// <summary>
/// Redirects Console.Out and Console.Error to Windows Event Log.
/// In debug mode, also writes to the original console streams and a log file.
/// </summary>
static class AppLogger
{
    private const string EventSourceName = "FastmailFiles";
    private const string EventLogName = "Application";

    private static EventLog? _eventLog;
    private static StreamWriter? _fileWriter;
    private static readonly object _fileLock = new();

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
            // Non-admin — source may already exist or we'll use generic source
        }

        try
        {
            _eventLog = new EventLog(EventLogName) { Source = EventSourceName };
        }
        catch
        {
            // If we can't create the event log, logging is best-effort
        }

        // In debug mode, also write to a log file
        if (debug)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FastmailFiles");
                Directory.CreateDirectory(logDir);
                LogFilePath = Path.Combine(logDir, "debug.log");
                _fileWriter = new StreamWriter(LogFilePath, append: false, Encoding.UTF8)
                {
                    AutoFlush = true,
                };
                _fileWriter.WriteLine($"=== FastmailFiles debug log started at {DateTime.Now:O} ===");
            }
            catch
            {
                // Best-effort file logging
            }
        }

        var originalOut = Console.Out;
        var originalErr = Console.Error;

        Console.SetOut(new LogWriter(originalOut, EventLogEntryType.Information, debug, "OUT"));
        Console.SetError(new LogWriter(originalErr, EventLogEntryType.Warning, debug, "ERR"));
    }

    internal static void WriteToFile(string prefix, string line)
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

    private sealed class LogWriter : TextWriter
    {
        private readonly TextWriter? _original;
        private readonly EventLogEntryType _entryType;
        private readonly bool _debug;
        private readonly string _prefix;
        private readonly StringBuilder _lineBuffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public LogWriter(TextWriter? original, EventLogEntryType entryType, bool debug, string prefix)
        {
            _original = debug ? original : null;
            _entryType = entryType;
            _debug = debug;
            _prefix = prefix;
        }

        public override void Write(char value)
        {
            if (value == '\n')
                FlushLine();
            else if (value != '\r')
                _lineBuffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;

            foreach (var ch in value)
            {
                if (ch == '\n')
                    FlushLine();
                else if (ch != '\r')
                    _lineBuffer.Append(ch);
            }
        }

        public override void WriteLine(string? value)
        {
            if (value != null)
                _lineBuffer.Append(value);
            FlushLine();
        }

        public override void WriteLine()
        {
            FlushLine();
        }

        private void FlushLine()
        {
            if (_lineBuffer.Length == 0)
                return;

            var line = _lineBuffer.ToString();
            _lineBuffer.Clear();

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

            if (_debug)
                _original?.WriteLine($"{timestamp} {line}");

            AppLogger.WriteToFile(_prefix, line);

            try
            {
                _eventLog?.WriteEntry(line, _entryType);
            }
            catch
            {
                // Best-effort logging
            }
        }

        public override void Flush()
        {
            _original?.Flush();
            FlushLine();
        }
    }
}
