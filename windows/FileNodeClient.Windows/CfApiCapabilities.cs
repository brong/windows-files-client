using FileNodeClient.Logging;

namespace FileNodeClient.Windows;

/// <summary>
/// Runtime feature detection for Windows Cloud Files API (cfapi) based on OS build number.
/// Allows graceful degradation on older Windows 10 versions.
/// </summary>
internal static class CfApiCapabilities
{
    private static readonly int OsBuild = Environment.OSVersion.Version.Build;

    /// <summary>
    /// CfOpenFileWithOplock and CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION (build 18362 / Windows 10 1903+)
    /// </summary>
    public static bool HasBlockSelfHydration => OsBuild >= 18362;

    /// <summary>
    /// CfReportSyncStatus and CF_OPERATION_INFO.SyncStatus (build 19041 / Windows 10 2004+)
    /// </summary>
    public static bool HasSyncStatus => OsBuild >= 19041;

    /// <summary>
    /// Progressive hydration policy (build 19041 / Windows 10 2004+)
    /// </summary>
    public static bool HasProgressiveHydration => OsBuild >= 19041;

    public static void LogCapabilities()
    {
        Log.Info($"OS build {OsBuild}: BlockSelfHydration={HasBlockSelfHydration}, SyncStatus={HasSyncStatus}, ProgressiveHydration={HasProgressiveHydration}");
    }
}
