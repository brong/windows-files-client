namespace FileNodeClient.Ipc;

public enum LogLevel { Debug, Info, Warning, Error }

public static class Log
{
    public static Action<LogLevel, string>? Sink { get; set; }
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warning, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    private static void Write(LogLevel level, string msg)
    {
        if (level >= MinLevel) Sink?.Invoke(level, msg);
    }
}
