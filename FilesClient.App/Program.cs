using FilesClient.Jmap;
using FilesClient.Windows;

namespace FilesClient.App;

class Program
{
    private const string DefaultSessionUrl = "https://api.fastmail.com/jmap/session";
    private const string DefaultSyncRootFolder = "FastmailFiles";

    static async Task<int> Main(string[] args)
    {
        string? token = null;
        string sessionUrl = DefaultSessionUrl;
        string? syncRootPath = null;

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
                case "--sync-root" when i + 1 < args.Length:
                    syncRootPath = args[++i];
                    break;
            }
        }

        token ??= Environment.GetEnvironmentVariable("FASTMAIL_TOKEN");

        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("Usage: FilesClient.App --token <app-password>");
            Console.Error.WriteLine("  or set FASTMAIL_TOKEN environment variable");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --token <token>         Fastmail app password / bearer token");
            Console.Error.WriteLine("  --session-url <url>     JMAP session URL (default: Fastmail)");
            Console.Error.WriteLine("  --sync-root <path>      Local sync folder path");
            return 1;
        }

        syncRootPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultSyncRootFolder);

        Console.WriteLine("Fastmail Files Client");
        Console.WriteLine($"Sync root: {syncRootPath}");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nShutting down...");
        };

        using var jmapClient = new JmapClient(token);

        try
        {
            // 1. Connect to JMAP
            Console.WriteLine("Connecting to JMAP...");
            await jmapClient.ConnectAsync(sessionUrl, cts.Token);
            Console.WriteLine($"Connected as {jmapClient.Session.Username}");
            Console.WriteLine($"Account: {jmapClient.AccountId}");
            Console.WriteLine();

            // 2. Set up sync engine (register + connect callbacks)
            using var engine = new SyncEngine(syncRootPath, jmapClient);
            Console.WriteLine("Registering sync root...");
            engine.RegisterAndConnect();

            // 3. Initial population
            Console.WriteLine("Populating placeholders...");
            var state = await engine.PopulateAsync(cts.Token);
            Console.WriteLine($"Initial sync complete. State: {state}");
            Console.WriteLine();

            // 4. Sync loop — poll for changes
            Console.WriteLine("Watching for changes (Ctrl+C to stop)...");
            string currentState = state;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    currentState = await engine.PollChangesAsync(currentState, cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Change poll error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex}");
            return 1;
        }

        return 0;
    }
}
