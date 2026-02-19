using System.Runtime.InteropServices;

namespace FilesClient.App;

class Program
{
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    private const uint SC_CLOSE = 0xF060;
    private const uint MF_BYCOMMAND = 0x00000000;

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
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool debug = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--debug":
                    debug = true;
                    break;
                default:
                    Console.Error.WriteLine($"Error: unknown option '{args[i]}'");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Options:");
                    Console.Error.WriteLine("  --debug    Show debug console");
                    return 1;
            }
        }

        // Allocate a console window in debug mode (since OutputType is WinExe)
        if (debug)
        {
            AllocConsole();
            IsDebugMode = true;

            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
            {
                // Start hidden — user can show via tray menu
                ShowWindow(hwnd, 0 /* SW_HIDE */);

                var sysMenu = GetSystemMenu(hwnd, false);
                if (sysMenu != IntPtr.Zero)
                    DeleteMenu(sysMenu, SC_CLOSE, MF_BYCOMMAND);
            }
        }

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

        // Download Fastmail favicon for tray icon
        var iconPath = await DownloadIconAsync(cts.Token);

        using var serviceClient = new ServiceClient();
        serviceClient.Start(cts.Token);

        using var trayIcon = new TrayIcon(cts, iconPath, serviceClient);
        trayIcon.Start();

        // If not connected after a brief wait, show manage accounts
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000, cts.Token);
            if (serviceClient.Accounts.Count == 0 && serviceClient.ConnectingLoginIds.Count == 0
                && serviceClient.IsConnected)
            {
                trayIcon.ShowManageAccounts();
            }
        }, cts.Token);

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        await serviceClient.StopAsync();
        return 0;
    }

    private static async Task<string?> DownloadIconAsync(CancellationToken ct)
    {
        const string FaviconUrl = "https://www.fastmail.com/favicon.ico";
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
