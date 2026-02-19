using FilesClient.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

var options = new ServiceOptions();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--token" when i + 1 < args.Length:
            options.Token = args[++i];
            break;
        case "--session-url" when i + 1 < args.Length:
            options.SessionUrl = args[++i];
            break;
        case "--debug":
            options.Debug = true;
            break;
        case "--clean":
            options.Clean = true;
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
            Console.Error.WriteLine("  --clean                 Unregister sync root and delete local files before syncing");
            return 1;
    }
}

options.Token ??= Environment.GetEnvironmentVariable("FASTMAIL_TOKEN");

AppLogger.Initialize(options.Debug);
Console.WriteLine($"FastmailFiles service process starting (debug={options.Debug})");

var builder = new HostBuilder();
builder.ConfigureServices(services =>
{
    services.AddSingleton(options);
    services.AddHostedService<SyncHostedService>();
});

if (WindowsServiceHelpers.IsWindowsService())
{
    builder.ConfigureServices(services =>
    {
        services.AddWindowsService(cfg =>
        {
            cfg.ServiceName = "FastmailFiles";
        });
    });
}

Console.WriteLine("Building host...");
var host = builder.Build();
Console.WriteLine("Starting host...");
await host.RunAsync();
return 0;
