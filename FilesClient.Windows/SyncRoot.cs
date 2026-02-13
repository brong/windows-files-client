using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FilesClient.Windows;

internal class SyncRoot : IDisposable
{
    // Stable provider GUID — must remain the same across all runs.
    private static readonly Guid ProviderId = new("f5e2d9a1-3b7c-4e8f-9a01-6c2d5e8f1b3a");

    private readonly string _syncRootPath;
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

    public unsafe void Register(string providerName, string providerVersion)
    {
        Directory.CreateDirectory(_syncRootPath);

        fixed (char* pProviderName = providerName)
        fixed (char* pProviderVersion = providerVersion)
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = pProviderName,
                ProviderVersion = pProviderVersion,
                ProviderId = ProviderId,
            };

            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_NONE,
                },
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL,
                    Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
                },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
                PlaceholderManagement = CF_PLACEHOLDER_MANAGEMENT_POLICY.CF_PLACEHOLDER_MANAGEMENT_POLICY_DEFAULT,
            };

            PInvoke.CfRegisterSyncRoot(
                _syncRootPath,
                in registration,
                in policies,
                CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE).ThrowOnFailure();
        }

        _registered = true;
        Console.WriteLine($"Sync root registered: {_syncRootPath}");
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
            PInvoke.CfUnregisterSyncRoot(_syncRootPath).ThrowOnFailure();
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
