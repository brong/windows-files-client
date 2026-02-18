using System.Runtime.InteropServices;
using FilesClient.Jmap;

namespace FilesClient.App;

class Program
{
    private const string DefaultSessionUrl = "https://api.fastmail.com/jmap/session";
    private const string FaviconUrl = "https://www.fastmail.com/favicon.ico";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool ConsoleCtrlHandlerDelegate(int dwCtrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerDelegate? handler, bool add);

    // Must be a static field to prevent GC of the delegate
    private static ConsoleCtrlHandlerDelegate? _consoleCtrlHandler;

    internal static bool IsDebugMode { get; private set; }

    internal static void ShowDebugConsole()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, 5 /* SW_SHOW */);
    }

    internal static void HideDebugConsole()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, 0 /* SW_HIDE */);
    }

    internal static bool IsDebugConsoleVisible()
    {
        var hwnd = GetConsoleWindow();
        return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
    }

    static async Task<int> Main(string[] args)
    {
        string? token = null;
        string sessionUrl = DefaultSessionUrl;
        bool debug = false;
        bool stub = false;
        bool clean = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--session-url" when i + 1 < args.Length:
                    sessionUrl = args[++i];
                    break;
                case "--debug":
                    debug = true;
                    break;
                case "--stub":
                    stub = true;
                    break;
                case "--clean":
                    clean = true;
                    break;
                case "--token" or "--session-url":
                    Console.Error.WriteLine($"Error: {args[i]} requires a value");
                    return 1;
                default:
                    Console.Error.WriteLine($"Error: unknown option '{args[i]}'");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Options:");
                    Console.Error.WriteLine("  --token <token>         Fastmail app password / bearer token");
                    Console.Error.WriteLine("  --session-url <url>     JMAP session URL (default: Fastmail)");
                    Console.Error.WriteLine("  --debug                 Log all JMAP HTTP traffic to stderr");
                    Console.Error.WriteLine("  --stub                  Use stub JMAP client (single hello.txt file)");
                    Console.Error.WriteLine("  --clean                 Unregister sync root and delete local files before syncing");
                    return 1;
            }
        }

        token ??= Environment.GetEnvironmentVariable("FASTMAIL_TOKEN");

        // Allocate a console window in debug mode (since OutputType is WinExe)
        if (debug)
        {
            AllocConsole();
            IsDebugMode = true;

            // Intercept CTRL_CLOSE_EVENT (user clicks X on console window) to hide
            // instead of terminating the process
            _consoleCtrlHandler = (int ctrlType) =>
            {
                if (ctrlType == 2 /* CTRL_CLOSE_EVENT */)
                {
                    HideDebugConsole();
                    return true; // Prevent termination
                }
                return false;
            };
            SetConsoleCtrlHandler(_consoleCtrlHandler, true);
        }

        AppLogger.Initialize(debug);

        using var cts = new CancellationTokenSource();
        if (debug)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\nShutting down...");
            };
        }

        // Download Fastmail favicon for use as sync root icon
        var iconPath = await DownloadIconAsync(cts.Token);

        if (stub)
        {
            // Stub mode: single inline account, no LoginManager
            return await RunStubModeAsync(iconPath, clean, debug, cts);
        }

        using var loginManager = new LoginManager(debug);

        // Dev: --token adds a transient (non-persisted) login
        if (token != null)
        {
            try
            {
                await loginManager.AddLoginAsync(sessionUrl, token,
                    persist: false, iconPath: iconPath, clean: clean, ct: cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to connect: {ex.Message}");
                return 1;
            }
        }

        await loginManager.StartAsync(iconPath, clean, cts.Token);

        using var trayIcon = new TrayIcon(cts, iconPath, loginManager);
        trayIcon.Start();

        // If no accounts configured, open the manage accounts dialog
        if (loginManager.Supervisors.Count == 0 && token == null)
            trayIcon.ShowManageAccounts();

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        await loginManager.StopAllAsync();
        return 0;
    }

    /// <summary>
    /// Stub mode bypasses LoginManager and runs a single StubJmapClient directly.
    /// </summary>
    private static async Task<int> RunStubModeAsync(string? iconPath, bool clean, bool debug, CancellationTokenSource cts)
    {
        Console.WriteLine("Using stub JMAP client");
        IJmapClient jmapClient = new StubJmapClient();
        Console.WriteLine($"Account: {jmapClient.AccountId}");

        var displayName = $"{jmapClient.Context.Username} Files";
        var syncRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            SanitizeFolderName(displayName));

        using var loginManager = new LoginManager(debug);
        var supervisor = new AccountSupervisor(jmapClient, syncRootPath, displayName, debug);

        try
        {
            await supervisor.StartAsync(iconPath, clean, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex}");
            return 1;
        }

        using var trayIcon = new TrayIcon(cts, iconPath, loginManager);
        trayIcon.Start();

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        await supervisor.StopAsync();
        supervisor.Dispose();
        jmapClient.Dispose();
        return 0;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars).TrimEnd(' ', '.');
    }

    private static async Task<string?> DownloadIconAsync(CancellationToken ct)
    {
        try
        {
            var iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FastmailFiles");
            Directory.CreateDirectory(iconDir);
            var iconPath = Path.Combine(iconDir, "icon.ico");

            if (File.Exists(iconPath))
                return iconPath;

            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(FaviconUrl, ct);
            await File.WriteAllBytesAsync(iconPath, data, ct);
            Console.WriteLine($"Downloaded icon to {iconPath}");
            return iconPath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not download icon: {ex.Message}");
            return null;
        }
    }
}
