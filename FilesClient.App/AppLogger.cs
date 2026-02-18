using System.Diagnostics;
using System.Text;

namespace FilesClient.App;

/// <summary>
/// Redirects Console.Out and Console.Error to Windows Event Log.
/// In debug mode, also writes to the original console streams.
/// </summary>
static class AppLogger
{
    private const string EventSourceName = "FastmailFiles";
    private const string EventLogName = "Application";

    private static EventLog? _eventLog;

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

        var originalOut = Console.Out;
        var originalErr = Console.Error;

        Console.SetOut(new LogWriter(originalOut, EventLogEntryType.Information, debug));
        Console.SetError(new LogWriter(originalErr, EventLogEntryType.Warning, debug));
    }

    private sealed class LogWriter : TextWriter
    {
        private readonly TextWriter? _original;
        private readonly EventLogEntryType _entryType;
        private readonly bool _debug;
        private readonly StringBuilder _lineBuffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public LogWriter(TextWriter? original, EventLogEntryType entryType, bool debug)
        {
            _original = debug ? original : null;
            _entryType = entryType;
            _debug = debug;
        }

        public override void Write(char value)
        {
            if (_debug)
                _original?.Write(value);

            if (value == '\n')
                FlushLine();
            else if (value != '\r')
                _lineBuffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;

            if (_debug)
                _original?.Write(value);

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
            if (_debug)
                _original?.WriteLine(value);

            if (value != null)
                _lineBuffer.Append(value);
            FlushLine();
        }

        public override void WriteLine()
        {
            if (_debug)
                _original?.WriteLine();
            FlushLine();
        }

        private void FlushLine()
        {
            if (_lineBuffer.Length == 0)
                return;

            var line = _lineBuffer.ToString();
            _lineBuffer.Clear();

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
