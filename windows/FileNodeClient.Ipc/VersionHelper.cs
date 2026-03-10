using System.Reflection;

namespace FileNodeClient.Ipc;

public static class VersionHelper
{
    public static VersionInfo GetVersionInfo()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion format: "1.0.26.0+2026-03-10T12:00:00Z"
        string version;
        string buildDate;
        if (infoVersion != null && infoVersion.Contains('+'))
        {
            var parts = infoVersion.Split('+', 2);
            version = parts[0];
            buildDate = parts[1];
        }
        else
        {
            version = asm.GetName().Version?.ToString() ?? "unknown";
            buildDate = File.GetLastWriteTimeUtc(asm.Location).ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        return new VersionInfo(version, buildDate);
    }
}
