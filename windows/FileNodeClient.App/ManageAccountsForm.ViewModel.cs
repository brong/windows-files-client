using FileNodeClient.Ipc;

namespace FileNodeClient.App;

sealed partial class ManageAccountsForm
{
    private sealed class ViewModel
    {
        // --- Service state (snapshotted from ServiceClient events) ---
        public bool IsConnected;
        public List<AccountInfo> Accounts = new();
        public List<string> ConnectingLoginIds = new();
        public List<FailedLogin> FailedLogins = new();
        public List<string> ConnectedLoginIds = new();

        // --- Activity state (updated from activity pushes) ---
        public Dictionary<string, ActivitySnapshot> ActivityCache = new();

        // --- Selection state (updated from user clicks) ---
        public string? SelectedLoginId;
        public string? SelectedAccountId;

        // --- Derived/local state ---
        public VersionInfo? ServiceVersion;
        public HashSet<string> SettingUpAccounts = new();
        public Dictionary<string, List<DiscoveredAccount>> DiscoveredAccounts = new();
        public Dictionary<string, LoginAccountsResult> LoginAccountResults = new();
        public HashSet<string> RefreshedLogins = new();

        // --- Dirty tracking ---
        private bool _dirty;
        public bool IsDirty => _dirty;
        public void MarkDirty() => _dirty = true;
        public void ClearDirty() => _dirty = false;
    }
}
