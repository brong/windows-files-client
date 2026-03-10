namespace FileNodeClient.Logging;

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

    /// <summary>
    /// Invoke a delegate safely, catching and logging any subscriber exception.
    /// Prevents unhandled exceptions from crashing the process when firing events.
    /// </summary>
    public static void SafeInvoke(Action action, string context)
    {
        try { action(); }
        catch (Exception ex) { Error($"[{context}] Event handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// Await a fire-and-forget task safely, catching and logging any exception.
    /// Prevents unobserved task exceptions from crashing the process.
    /// </summary>
    public static async void FireAndForget(Task task, string context)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { Error($"[{context}] Fire-and-forget task failed: {ex.Message}"); }
    }
}
