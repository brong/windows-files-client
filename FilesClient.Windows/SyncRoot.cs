using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FilesClient.Windows;

internal class SyncRoot : IDisposable
{
    // Stable provider GUID — must remain the same across all runs.
    private static readonly Guid ProviderId = new("f5e2d9a1-3b7c-4e8f-9a01-6c2d5e8f1b3a");

    private const string ProviderName = "FastmailFiles";

    private readonly string _syncRootPath;
    private string _syncRootId = ProviderName;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;
    private bool _registered;

    // Must keep a reference to the callback registrations & delegates
    // so the GC doesn't collect them while the connection is active.
    private CF_CALLBACK_REGISTRATION[]? _callbackRegistrations;
    private CF_CALLBACK[]? _callbackDelegates;

    public string SyncRootPath => _syncRootPath;

    public SyncRoot(string syncRootPath)
    {
        _syncRootPath = syncRootPath;
    }

    public async Task RegisterAsync(string displayName, string providerVersion, string accountId, string? iconPath = null)
    {
        Directory.CreateDirectory(_syncRootPath);

        // Clean up stale registry entries from previous manual nav pane registration
        NavPaneIntegration.CleanupStaleEntries(ProviderId);

        // Sync root ID must be in the format: Provider!WindowsSID!AccountId
        // This is required for proper Shell integration and trust.
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-0-0";
        _syncRootId = $"{ProviderName}!{userSid}!{accountId}";

        var folder = await StorageFolder.GetFolderFromPathAsync(_syncRootPath);

        var iconResource = iconPath != null
            ? iconPath
            : "%SystemRoot%\\system32\\shell32.dll,-1";

        var info = new StorageProviderSyncRootInfo();
        info.Id = _syncRootId;
        info.Path = folder;
        info.DisplayNameResource = displayName;
        info.IconResource = iconResource;
        info.HydrationPolicy = StorageProviderHydrationPolicy.Full;
        info.HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed;
        info.PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull;
        info.InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime
            | StorageProviderInSyncPolicy.DirectoryLastWriteTime;
        info.Version = providerVersion;
        info.ShowSiblingsAsGroup = false;
        info.HardlinkPolicy = StorageProviderHardlinkPolicy.None;
        info.ProtectionMode = StorageProviderProtectionMode.Personal;
        info.ProviderId = ProviderId;
        info.Context = CryptographicBuffer.ConvertStringToBinary(
            _syncRootId, BinaryStringEncoding.Utf8);

        StorageProviderSyncRootManager.Register(info);
        _registered = true;
        Console.WriteLine($"Sync root registered: {_syncRootPath} (id={_syncRootId})");
    }

    internal unsafe void Connect(CF_CALLBACK_REGISTRATION[] callbacks, CF_CALLBACK[] delegates)
    {
        // Keep references alive for the lifetime of the connection
        _callbackRegistrations = callbacks;
        _callbackDelegates = delegates;

        CF_CONNECTION_KEY key;
        PInvoke.CfConnectSyncRoot(
            _syncRootPath,
            callbacks,
            null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE,
            &key).ThrowOnFailure();

        _connectionKey = key;
        _connected = true;
        Console.WriteLine("Sync root connected, ready for callbacks.");
    }

    internal CF_CONNECTION_KEY GetConnectionKey() => _connectionKey;

    internal void UpdateProviderStatus(CF_SYNC_PROVIDER_STATUS status)
    {
        if (_connected)
            PInvoke.CfUpdateSyncProviderStatus(_connectionKey, status);
    }

    public void Disconnect()
    {
        if (_connected)
        {
            PInvoke.CfDisconnectSyncRoot(_connectionKey).ThrowOnFailure();
            _connected = false;
            _callbackRegistrations = null;
            _callbackDelegates = null;
            Console.WriteLine("Sync root disconnected.");
        }
    }

    public void Unregister()
    {
        if (_registered)
        {
            StorageProviderSyncRootManager.Unregister(_syncRootId);
            _registered = false;
            Console.WriteLine("Sync root unregistered.");
        }
    }

    public void Dispose()
    {
        Disconnect();
        Unregister();
    }
}
