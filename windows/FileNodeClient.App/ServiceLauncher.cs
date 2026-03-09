using System.Diagnostics;
using FileNodeClient.Logging;

namespace FileNodeClient.App;

static class ServiceLauncher
{
    private const string ServiceExeName = "FileNodeClient.Service";
    private static bool _debug;

    public static bool IsServiceProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName(ServiceExeName).Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ServiceLauncher] Cannot query processes: {ex.Message}");
            return false;
        }
    }

    public static bool TryStartService(bool debug = false)
    {
        if (debug) _debug = true; // sticky — once set, always forward

        if (IsServiceProcessRunning())
            return true;

        var baseDir = AppContext.BaseDirectory;
        var serviceExePath = Path.Combine(baseDir, ServiceExeName + ".exe");

        if (!File.Exists(serviceExePath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = serviceExePath,
                Arguments = _debug ? "--debug" : "",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[ServiceLauncher] Failed to start service: {ex.Message}");
            return false;
        }
    }

    public static bool StopService()
    {
        try
        {
            var processes = Process.GetProcessesByName(ServiceExeName);
            if (processes.Length == 0)
                return true;

            foreach (var p in processes)
            {
                try { p.Kill(); }
                catch (InvalidOperationException) { /* already exited */ }
                finally { p.Dispose(); }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[ServiceLauncher] Failed to stop service: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> WaitForExitAsync(int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!IsServiceProcessRunning())
                return true;
            await Task.Delay(250);
        }
        return !IsServiceProcessRunning();
    }
}
