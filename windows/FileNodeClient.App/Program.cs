using FileNodeClient.Logging;
using FileNodeClient.Windows;

namespace FileNodeClient.App;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool debug = false, clean = false, comLaunched = false;
        string? token = null;
        string sessionUrl = "https://api.fastmail.com/jmap/session";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--token" when i + 1 < args.Length: token = args[++i]; break;
                case "--session-url" when i + 1 < args.Length: sessionUrl = args[++i]; break;
                case "--debug": debug = true; break;
                case "--clean": clean = true; break;
                // COM launched us to serve the UriSource ExeServer class (manifest
                // com:ExeServer) because no running instance had it registered.
                // We start normally — the class object is registered below.
                case "-Embedding" or "/Embedding": comLaunched = true; break;
                default:
                    MessageBox.Show(
                        $"Unknown option '{args[i]}'\n\n" +
                        "Options:\n" +
                        "  --token <token>         Fastmail app password / bearer token (dev, not persisted)\n" +
                        "  --session-url <url>     JMAP session URL (default: Fastmail)\n" +
                        "  --debug                 Verbose logging\n" +
                        "  --clean                 Unregister sync roots and delete local files before syncing",
                        "FileNodeClient", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
            }
        }
        token ??= Environment.GetEnvironmentVariable("FASTMAIL_TOKEN");

        using var mutex = new Mutex(true, "FileNodeClient.App.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // A COM activation that raced our own startup: the running instance
            // owns the class object, so just go away quietly.
            if (!comLaunched)
                MessageBox.Show("FileNodeClient is already running.", "FileNodeClient",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        AppLogger.Initialize(debug);
        Log.Info($"FileNodeClient starting (debug={debug})");

        // Global safety nets — log and swallow so a stray exception on any
        // thread doesn't take the sync engine down with it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error($"[FATAL] Unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn($"[WARN] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        using var cts = new CancellationTokenSource();

        // Register COM class factories so Explorer can activate our handlers,
        // and the named pipe the native thumbnail DLL talks to.
        ComServerHost.Register();
        using var thumbnailPipeServer = new ThumbnailPipeServer();
        thumbnailPipeServer.Start();

        var iconPath = await DownloadIconAsync(cts.Token);

        using var loginManager = new LoginManager(debug, iconPath);
        using var sync = new SyncController(loginManager);

        using var trayIcon = new TrayIcon(cts, iconPath, sync);
        trayIcon.Start();

        // Dev: --token adds a transient (non-persisted) login
        if (token != null)
        {
            try
            {
                await loginManager.AddLoginAsync(new LoginCredential(sessionUrl, token),
                    persist: false, clean: clean, ct: cts.Token);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to connect: {ex.Message}");
            }
        }

        // Load stored credentials and start syncing
        await loginManager.StartAsync(clean, cts.Token);

        if (sync.Accounts.Count == 0 && sync.ConnectingLoginIds.Count == 0)
            trayIcon.ShowManageAccounts();

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        Log.Info("FileNodeClient stopping...");
        try { await loginManager.StopAllAsync(); }
        catch (Exception ex) { Log.Error($"Error during shutdown: {ex.Message}"); }
        return 0;
    }

    private static async Task<string?> DownloadIconAsync(CancellationToken ct)
    {
        const string FaviconUrl = "https://www.fastmail.com/favicon.ico";
        try
        {
            // Use %USERPROFILE% directly instead of %LOCALAPPDATA% because
            // MSIX virtualizes LocalApplicationData — the icon path must be
            // visible to Explorer/cfapi which runs outside the package context.
            var iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".fastmail");
            Directory.CreateDirectory(iconDir);
            var iconPath = Path.Combine(iconDir, "icon.ico");

            if (File.Exists(iconPath))
                return iconPath;

            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(FaviconUrl, ct);
            await File.WriteAllBytesAsync(iconPath, data, ct);
            Log.Info($"Downloaded icon to {iconPath}");
            return iconPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not download icon: {ex.Message}");
            return null;
        }
    }
}
