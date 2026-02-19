namespace FilesClient.App;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var cts = new CancellationTokenSource();

        // Download Fastmail favicon for tray icon
        var iconPath = await DownloadIconAsync(cts.Token);

        using var serviceClient = new ServiceClient();

        using var trayIcon = new TrayIcon(cts, iconPath, serviceClient);
        trayIcon.Start();

        // Start IPC connection AFTER tray icon is ready, so _syncContext is set
        // and all events will be properly marshalled to the UI thread.
        serviceClient.Start(cts.Token);

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
