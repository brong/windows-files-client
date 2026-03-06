using System.Diagnostics;

namespace FilesClient.App;

static class ServiceLauncher
{
    private const string ServiceExeName = "FilesClient.Service";

    public static bool IsServiceProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName(ServiceExeName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryStartService()
    {
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
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return true;
        }
        catch
        {
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
                catch { /* already exited */ }
                finally { p.Dispose(); }
            }
            return true;
        }
        catch
        {
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
