using System.Drawing;
using FileNodeClient.Ipc;
using FileNodeClient.Logging;
using FileNodeClient.Jmap.Auth;

namespace FileNodeClient.App;

sealed partial class ManageAccountsForm : Form
{
    private readonly ServiceClient _serviceClient;
    private readonly CancellationTokenSource _appCts;
    private readonly TreeView _treeView;
    private readonly Panel _detailPanel;
    private readonly Panel _detailTopPanel;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly ViewModel _vm = new();

    // Detail panel controls — login selected
    private readonly Panel _loginPanel;
    private readonly Label _loginIdLabel;
    private readonly TextBox _sessionUrlBox;
    private readonly TextBox _tokenBox;
    private readonly Button _updateCredentialsButton;
    private readonly Button _removeLoginButton;
    private readonly Button _reauthenticateButton;
    private readonly TextBox _reauthStatusLabel;

    // Detail panel controls — synced account selected
    private readonly Panel _syncedAccountPanel;
    private readonly Label _syncedAccountName;
    private readonly Label _accountStatusLabel;
    private readonly Label _syncFolderLabel;
    private readonly Label _quotaLabel;
    private readonly Button _detachButton;
    private readonly Button _removeButton;
    private readonly Button _refreshButton;
    private readonly Button _cleanButton;
    private readonly Button _openFolderButton;
    private readonly Button _pauseButton;
    private readonly Button _resumeButton;
    private readonly Button _syncNowButton;
    private readonly ListView _activityListView;

    // Detail panel controls — non-synced account selected
    private readonly Panel _availableAccountPanel;
    private readonly Label _availableAccountLabel;
    private readonly Button _addAccountButton;

    // No selection panel
    private readonly Label _noSelectionLabel;

    // Empty activity state
    private readonly Label _activityEmptyLabel;

    // Data model
    private record LoginNode(string LoginId);
    private record AccountNode(string LoginId, string AccountId, string Name,
        bool IsSynced, bool IsMissing, AccountInfo? SyncInfo);
    private record ActivityItemTag(string AccountId, Guid EntryId, string? LocalPath);

    private bool _suppressSelectionChanged;
    private bool _operationInProgress;
    private string? _renderedLoginId;
    private string? _renderedAccountId;
    private readonly Button _addLoginButton;
    private readonly Button _startServiceButton;
    private readonly Button _stopServiceButton;
    private readonly Button _restartServiceButton;

    public ManageAccountsForm(ServiceClient serviceClient, CancellationTokenSource appCts)
    {
        _serviceClient = serviceClient;
        _appCts = appCts;

        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "FileNodeClient";
        MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        AppIcon.Apply(this);

        // --- TreeView on the left ---
        _treeView = new TreeView
        {
            Dock = DockStyle.Left,
            Width = 340,
            HideSelection = false,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            FullRowSelect = true,
        };
        _treeView.AfterSelect += (_, _) =>
        {
            if (!_suppressSelectionChanged)
                OnSelectionChanged();
        };
        // Click on already-selected node deselects it (show version info).
        // Must use BeginInvoke so the deselection runs AFTER the TreeView's
        // built-in click processing (which re-selects the clicked node).
        _treeView.MouseDown += (_, e) =>
        {
            var hit = _treeView.HitTest(e.Location);
            if (hit.Node != null && hit.Node == _treeView.SelectedNode)
            {
                BeginInvoke(() =>
                {
                    _treeView.SelectedNode = null;
                    OnSelectionChanged();
                });
            }
        };

        var splitter = new Splitter { Dock = DockStyle.Left, Width = 4 };

        // --- Detail panel on the right ---
        _detailPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 12),
        };

        // ======= Login detail panel =======
        _loginPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Visible = false };

        var loginLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = Padding.Empty,
        };
        loginLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _loginIdLabel = new Label
        {
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        loginLayout.Controls.Add(_loginIdLabel);

        loginLayout.Controls.Add(new Label
        {
            Text = "Re-authenticate this login:",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 100, 100),
            Margin = new Padding(0, 0, 0, 8),
        });

        loginLayout.Controls.Add(new Label
        {
            Text = "Session URL:",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        });

        _sessionUrlBox = new TextBox
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 0, 0, 10),
        };
        loginLayout.Controls.Add(_sessionUrlBox);

        loginLayout.Controls.Add(new Label
        {
            Text = "App password (token):",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        });

        _tokenBox = new TextBox
        {
            UseSystemPasswordChar = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 0, 0, 14),
        };
        loginLayout.Controls.Add(_tokenBox);

        var loginButtonFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 0),
            Padding = Padding.Empty,
        };

        _reauthenticateButton = new Button
        {
            Text = "Re-authenticate with browser...",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 0, 8, 0),
        };
        _reauthenticateButton.Click += OnReauthenticateClicked;
        loginButtonFlow.Controls.Add(_reauthenticateButton);

        _updateCredentialsButton = new Button
        {
            Text = "Update Credentials",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 0, 8, 0),
        };
        _updateCredentialsButton.Click += OnUpdateCredentialsClicked;

        _removeLoginButton = new Button
        {
            Text = "Remove Login",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 0, 0, 0),
        };
        _removeLoginButton.Click += OnRemoveLoginClicked;

        loginButtonFlow.Controls.AddRange([_updateCredentialsButton, _removeLoginButton]);
        loginLayout.Controls.Add(loginButtonFlow);

        _reauthStatusLabel = new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = _loginPanel.BackColor,
            ForeColor = Color.Gray,
            Multiline = true,
            WordWrap = true,
            Width = 400,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 4, 0, 0),
        };
        loginLayout.Controls.Add(_reauthStatusLabel);

        _loginPanel.Controls.Add(loginLayout);

        // ======= Synced account detail panel =======
        _syncedAccountPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Visible = false };

        var syncedTopLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = Padding.Empty,
        };
        syncedTopLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _syncedAccountName = new Label
        {
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        syncedTopLayout.Controls.Add(_syncedAccountName);

        _accountStatusLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };
        syncedTopLayout.Controls.Add(_accountStatusLabel);

        _syncFolderLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80),
            Margin = new Padding(0, 0, 0, 4),
        };
        syncedTopLayout.Controls.Add(_syncFolderLabel);

        _quotaLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80),
            Margin = new Padding(0, 0, 0, 16),
            Visible = false,
        };
        syncedTopLayout.Controls.Add(_quotaLabel);

        var syncedButtonFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 0),
            Padding = Padding.Empty,
        };

        _openFolderButton = new Button { Text = "Open Folder", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _openFolderButton.Click += OnOpenFolderClicked;

        _detachButton = new Button { Text = "Detach", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _detachButton.Click += OnDetachClicked;

        _removeButton = new Button { Text = "Remove", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _removeButton.Click += OnRemoveClicked;

        _refreshButton = new Button { Text = "Refresh", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _refreshButton.Click += OnRefreshClicked;

        _cleanButton = new Button { Text = "Clean", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _cleanButton.Click += OnCleanClicked;

        _pauseButton = new Button { Text = "Pause", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6), Visible = false };
        _pauseButton.Click += OnPauseClicked;

        _resumeButton = new Button { Text = "Resume", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6), Visible = false };
        _resumeButton.Click += OnResumeClicked;

        _syncNowButton = new Button { Text = "Sync Now", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 0, 6), Visible = false };
        _syncNowButton.Click += OnSyncNowClicked;

        syncedButtonFlow.Controls.AddRange([_openFolderButton, _pauseButton, _resumeButton, _syncNowButton, _detachButton, _removeButton, _refreshButton, _cleanButton]);
        syncedTopLayout.Controls.Add(syncedButtonFlow);

        _syncedAccountPanel.Controls.Add(syncedTopLayout);

        // Activity list view — created here, added to _detailPanel later
        _activityListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Clickable,
            ShowItemToolTips = true,
            Visible = false,
            OwnerDraw = true,
        };
        _activityListView.Columns.Add("Name", 100);
        _activityListView.Columns.Add("Size", 60, HorizontalAlignment.Right);
        _activityListView.Columns.Add("Action", 60);
        _activityListView.Columns.Add("Status", 60);
        _activityListView.Columns.Add("Updated", 80);
        _activityListView.Resize += (_, _) => AutoSizeActivityColumns();

        _activityListView.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _activityListView.DrawItem += (_, _) => { };
        _activityListView.DrawSubItem += OnDrawActivitySubItem;
        _activityListView.MouseUp += OnActivityListMouseUp;

        // Empty activity label — shown when nothing to sync
        _activityEmptyLabel = new Label
        {
            Text = "Nothing to sync",
            Dock = DockStyle.Fill,
            ForeColor = Color.Green,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, Font.Size * 1.2f),
            Visible = false,
        };

        // ======= Available (non-synced) account panel =======
        _availableAccountPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Visible = false };

        var availableLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = Padding.Empty,
        };
        availableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _availableAccountLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        availableLayout.Controls.Add(_availableAccountLabel);

        _addAccountButton = new Button
        {
            Text = "Start Syncing",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 0, 0, 0),
        };
        _addAccountButton.Click += OnAddAccountClicked;
        availableLayout.Controls.Add(_addAccountButton);

        _availableAccountPanel.Controls.Add(availableLayout);

        // ======= No selection label =======
        _noSelectionLabel = new Label
        {
            Text = "Select a login or account from the tree.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 8, 0, 0),
        };

        // Split detail area into two containers:
        // 1. _detailTopPanel (Top, AutoSize) — holds only the ACTIVE detail panel
        // 2. activityPanel (Fill) — holds only the activity list and empty label
        // ShowPanel() swaps which panel is in _detailTopPanel so hidden panels
        // don't affect its height calculation.
        _detailTopPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var activityPanel = new Panel
        {
            Dock = DockStyle.Fill,
        };
        activityPanel.Controls.AddRange([_activityEmptyLabel, _activityListView]);
        _activityListView.BringToFront();
        _activityEmptyLabel.BringToFront();

        _detailPanel.Controls.Add(_detailTopPanel);
        _detailPanel.Controls.Add(activityPanel);
        // Fill must be at front (docked last) so Top panel gets its space first
        activityPanel.BringToFront();

        // --- Bottom bar ---
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
        };

        _addLoginButton = new Button
        {
            Text = "Add Login...",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 0, 4, 0),
            Enabled = _serviceClient.IsConnected,
        };
        _addLoginButton.Click += OnAddLoginClicked;

        _startServiceButton = new Button
        {
            Text = "Start Service",
            AutoSize = true,
            Height = 30,
            Visible = !_serviceClient.IsConnected,
            Margin = new Padding(0, 0, 4, 0),
        };
        _startServiceButton.Click += OnStartServiceClicked;

        _stopServiceButton = new Button
        {
            Text = "Stop Service",
            AutoSize = true,
            Height = 30,
            Visible = _serviceClient.IsConnected,
            Margin = new Padding(0, 0, 4, 0),
        };
        _stopServiceButton.Click += OnStopServiceClicked;

        _restartServiceButton = new Button
        {
            Text = "Restart Service",
            AutoSize = true,
            Height = 30,
            Visible = _serviceClient.IsConnected,
            Margin = new Padding(0, 0, 4, 0),
        };
        _restartServiceButton.Click += OnRestartServiceClicked;

        var leftButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Location = new Point(10, 9),
        };
        leftButtons.Controls.AddRange([_addLoginButton, _startServiceButton, _stopServiceButton, _restartServiceButton]);

        var exitButton = new Button
        {
            Text = "Exit",
            AutoSize = true,
            Height = 30,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        exitButton.Location = new Point(bottomPanel.Width - exitButton.Width - 14, 9);
        exitButton.Click += OnExitClicked;

        var closeButton = new Button
        {
            Text = "Close",
            AutoSize = true,
            Height = 30,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        // Position Close to the left of Exit
        closeButton.Location = new Point(bottomPanel.Width - exitButton.Width - closeButton.Width - 22, 9);
        closeButton.Click += (_, _) => Hide();

        bottomPanel.Controls.AddRange([leftButtons, closeButton, exitButton]);

        // --- Assemble ---
        Controls.Add(_detailPanel);
        Controls.Add(splitter);
        Controls.Add(_treeView);
        Controls.Add(bottomPanel);

        // Single render timer — coalesces all state changes into one UI pass
        _renderTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); Render(); };

        // Safety-net timer — catches any missed events
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        _refreshTimer.Tick += (_, _) => { SnapshotServiceState(); ScheduleRender(); };
        _refreshTimer.Start();

        _serviceClient.AccountsChanged += () => { SnapshotServiceState(); ScheduleRender(); };
        _serviceClient.StatusChanged += () => { SnapshotServiceState(); ScheduleRender(); };
        _serviceClient.ActivityChanged += OnActivityPushed;
        _serviceClient.ConnectionChanged += OnConnectionChanged;

        SnapshotServiceState();
        Render();
        _ = RefreshAllLoginAccountsAsync();
    }

    // === Core rendering infrastructure ===

    private void ScheduleRender()
    {
        _vm.MarkDirty();
        if (InvokeRequired) BeginInvoke(() => _renderTimer.Start());
        else _renderTimer.Start();
    }

    private void SnapshotServiceState()
    {
        _vm.IsConnected = _serviceClient.IsConnected;
        _vm.Accounts = _serviceClient.Accounts.ToList();
        _vm.ConnectingLoginIds = _serviceClient.ConnectingLoginIds.ToList();
        _vm.FailedLogins = _serviceClient.FailedLogins.ToList();
        _vm.ConnectedLoginIds = _serviceClient.ConnectedLoginIds.ToList();
    }

    private void OnConnectionChanged(bool connected)
    {
        void Handle()
        {
            SnapshotServiceState();
            UpdateServiceButtons();
            if (connected)
            {
                FetchServiceVersion();
                FetchInitialActivity();
            }
            else
            {
                _vm.ServiceVersion = null;
                _vm.ActivityCache.Clear();
            }
            ScheduleRender();
        }
        if (InvokeRequired) BeginInvoke(Handle); else Handle();
    }

    private void Render()
    {
        _vm.ClearDirty();
        if (!Visible) return;

        RenderTree();
        RenderDetailPanel();
        RenderActivityView();
    }

    private void RenderTree()
    {
        var accounts = _vm.Accounts;
        var connectingIds = _vm.ConnectingLoginIds;
        var failedLogins = _vm.FailedLogins;
        var connectedLoginIds = _vm.ConnectedLoginIds;
        var serviceConnected = _vm.IsConnected;

        // Build desired tree state as flat list of (loginId, accountId?, text, color, tag)
        var desired = new List<(string LoginId, List<(string Text, Color Color, object Tag)> Children)>();

        if (!serviceConnected)
        {
            // Special case: show "Service not running" as a single root node
        }
        else
        {
            var byLogin = accounts
                .Where(a => !string.IsNullOrEmpty(a.LoginId))
                .GroupBy(a => a.LoginId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var allLoginIds = byLogin.Keys
                .Union(_vm.DiscoveredAccounts.Keys)
                .Union(connectingIds)
                .Union(connectedLoginIds)
                .Union(failedLogins.Select(f => f.LoginId))
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            foreach (var loginId in allLoginIds)
            {
                var syncedAccounts = byLogin.GetValueOrDefault(loginId) ?? new List<AccountInfo>();
                var syncedIds = syncedAccounts.Select(a => a.AccountId).ToHashSet();
                var children = new List<(string Text, Color Color, object Tag)>();

                var failedInfo = failedLogins.FirstOrDefault(f => f.LoginId == loginId);

                if (connectingIds.Contains(loginId) && syncedAccounts.Count == 0)
                {
                    children.Add(("Connecting...", Color.Gray, (object)"connecting"));
                }
                else if (failedInfo != null && syncedAccounts.Count == 0)
                {
                    children.Add(($"Connection failed: {failedInfo.Error}", Color.Red, (object)"failed"));
                }
                else
                {
                    var discoveredIds = _vm.DiscoveredAccounts.TryGetValue(loginId, out var disc)
                        ? disc.Select(d => d.AccountId).ToHashSet() : null;
                    var loginRefreshed = _vm.RefreshedLogins.Contains(loginId);

                    foreach (var account in syncedAccounts)
                    {
                        var isMissing = loginRefreshed && discoveredIds != null
                            && !discoveredIds.Contains(account.AccountId);

                        var rejCount = _serviceClient.GetRejectedCount(account.AccountId);
                        var statusText = isMissing ? "Missing on server" : account.Status switch
                        {
                            AccountStatus.Paused when account.PauseReason?.Contains("DiskFull") == true => "Disk full",
                            AccountStatus.Paused when account.PauseReason?.Contains("UserRequested") == true => "Paused",
                            AccountStatus.Paused when account.PauseReason?.Contains("MeteredConnection") == true => "Metered",
                            AccountStatus.Idle when account.PendingCount > 0 => $"{account.PendingCount} pending",
                            AccountStatus.Idle when rejCount > 0 => rejCount == 1 ? "1 file rejected" : $"{rejCount} files rejected",
                            AccountStatus.Idle => "Up to date",
                            AccountStatus.Syncing => "Syncing",
                            AccountStatus.Error => "Error",
                            AccountStatus.Disconnected => "Offline",
                            _ => "Unknown",
                        };

                        children.Add(($"{account.DisplayName} \u2014 {statusText}",
                            isMissing ? Color.FromArgb(200, 120, 0) : default,
                            new AccountNode(loginId, account.AccountId, account.DisplayName,
                                IsSynced: true, IsMissing: isMissing, SyncInfo: account)));
                    }

                    if (disc != null)
                    {
                        foreach (var d in disc.Where(d => !syncedIds.Contains(d.AccountId)))
                        {
                            var isSettingUp = _vm.SettingUpAccounts.Contains(d.AccountId);
                            var label = isSettingUp ? "Setting up" : "Not synced";
                            children.Add(($"{d.Name} \u2014 {label}",
                                isSettingUp ? Color.DodgerBlue : Color.Gray,
                                new AccountNode(loginId, d.AccountId, d.Name,
                                    IsSynced: false, IsMissing: false, SyncInfo: null)));
                        }
                    }

                    // Clear setting-up flag for accounts that are now synced
                    foreach (var acct in syncedAccounts)
                        _vm.SettingUpAccounts.Remove(acct.AccountId);
                }

                desired.Add((loginId, children));
            }
        }

        // Check if tree structure changed (login count + child counts)
        bool structureChanged = !serviceConnected || _treeView.Nodes.Count != desired.Count;
        if (!structureChanged)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                var loginTn = _treeView.Nodes[i];
                if (loginTn.Tag is not LoginNode ln || ln.LoginId != desired[i].LoginId
                    || loginTn.Nodes.Count != desired[i].Children.Count)
                {
                    structureChanged = true;
                    break;
                }
                for (int j = 0; j < desired[i].Children.Count; j++)
                {
                    var childTag = desired[i].Children[j].Tag;
                    var existingTag = loginTn.Nodes[j].Tag;
                    // Compare by account ID if both are AccountNode, otherwise by reference type
                    if (childTag is AccountNode desiredAn && existingTag is AccountNode existingAn)
                    {
                        if (desiredAn.AccountId != existingAn.AccountId)
                        {
                            structureChanged = true;
                            break;
                        }
                    }
                    else if (childTag.GetType() != existingTag?.GetType())
                    {
                        structureChanged = true;
                        break;
                    }
                }
                if (structureChanged) break;
            }
        }

        if (!serviceConnected)
        {
            if (_treeView.Nodes.Count != 1 || _treeView.Nodes[0].Text != "Service not running")
            {
                _suppressSelectionChanged = true;
                _treeView.BeginUpdate();
                _treeView.Nodes.Clear();
                _treeView.Nodes.Add(new TreeNode("Service not running")
                    { ForeColor = Color.Gray });
                _treeView.EndUpdate();
                _suppressSelectionChanged = false;
            }
            return;
        }

        _suppressSelectionChanged = true;

        if (structureChanged)
        {
            // Full rebuild — structure changed
            _treeView.BeginUpdate();
            _treeView.Nodes.Clear();

            foreach (var (loginId, children) in desired)
            {
                var parts = loginId.Split('@');
                var displayText = parts.Length >= 3
                    ? $"{parts[0]}@{parts[1]} ({parts[2]})"
                    : loginId;

                var loginTreeNode = new TreeNode(displayText)
                {
                    Tag = new LoginNode(loginId),
                    NodeFont = new Font(_treeView.Font, FontStyle.Bold),
                };

                foreach (var (text, color, tag) in children)
                    loginTreeNode.Nodes.Add(new TreeNode(text) { Tag = tag, ForeColor = color });

                _treeView.Nodes.Add(loginTreeNode);
            }

            _treeView.ExpandAll();

            // Restore selection from ViewModel
            if (_vm.SelectedLoginId != null || _vm.SelectedAccountId != null)
            {
                foreach (TreeNode loginTn in _treeView.Nodes)
                {
                    if (loginTn.Tag is LoginNode restoredLn && restoredLn.LoginId == _vm.SelectedLoginId)
                    {
                        _treeView.SelectedNode = loginTn;
                        break;
                    }
                    foreach (TreeNode accountTn in loginTn.Nodes)
                    {
                        if (accountTn.Tag is AccountNode restoredAn && restoredAn.AccountId == _vm.SelectedAccountId)
                        {
                            _treeView.SelectedNode = accountTn;
                            break;
                        }
                    }
                }
            }
            _treeView.EndUpdate();
        }
        else
        {
            // Incremental update — just update text, color, and tag of existing nodes
            for (int i = 0; i < desired.Count; i++)
            {
                var loginTn = _treeView.Nodes[i];
                for (int j = 0; j < desired[i].Children.Count; j++)
                {
                    var (text, color, tag) = desired[i].Children[j];
                    var childTn = loginTn.Nodes[j];
                    if (childTn.Text != text) childTn.Text = text;
                    if (childTn.ForeColor != color) childTn.ForeColor = color;
                    childTn.Tag = tag;
                }
            }
        }

        _suppressSelectionChanged = false;

        // Clear selection if nothing was previously selected
        if (_vm.SelectedLoginId == null && _vm.SelectedAccountId == null)
            _treeView.SelectedNode = null;

        // If any login doesn't have discovered accounts yet, kick off discovery.
        var allIds = desired.Select(d => d.LoginId).ToList();
        var undiscoveredLogins = allIds
            .Where(id => !_vm.DiscoveredAccounts.ContainsKey(id) && !connectingIds.Contains(id))
            .ToList();
        if (undiscoveredLogins.Count > 0)
            _ = DiscoverAccountsForLoginsAsync(undiscoveredLogins);
    }

    private void RenderDetailPanel()
    {
        var selectedNode = _treeView.SelectedNode;
        bool loginChanged = _vm.SelectedLoginId != _renderedLoginId;
        bool accountChanged = _vm.SelectedAccountId != _renderedAccountId;
        bool selectionChanged = loginChanged || accountChanged;

        _renderedLoginId = _vm.SelectedLoginId;
        _renderedAccountId = _vm.SelectedAccountId;

        // Swap the active panel into _detailTopPanel — only one child at a time
        // so AutoSize reflects only the visible panel's height.
        void ShowPanel(Control target)
        {
            if (_detailTopPanel.Controls.Count == 1 && _detailTopPanel.Controls[0] == target)
                return;
            _detailTopPanel.SuspendLayout();
            _detailTopPanel.Controls.Clear();
            target.Dock = DockStyle.Top;
            target.Visible = true;
            _detailTopPanel.Controls.Add(target);
            _detailTopPanel.ResumeLayout(true);
        }

        if (selectedNode?.Tag is LoginNode loginNode)
        {
            ShowPanel(_loginPanel);
            if (selectionChanged)
            {
                _loginIdLabel.Text = loginNode.LoginId;
                var atIdx = loginNode.LoginId.LastIndexOf('@');
                var host = atIdx >= 0 ? loginNode.LoginId.Substring(atIdx + 1) : "";
                _sessionUrlBox.Text = !string.IsNullOrEmpty(host)
                    ? $"https://{host}/jmap/session"
                    : "";
                _tokenBox.Text = "";
                _reauthStatusLabel.Text = "";
            }
        }
        else if (selectedNode?.Tag is AccountNode accountNode)
        {
            if (accountNode.IsSynced && accountNode.SyncInfo != null)
            {
                ShowPanel(_syncedAccountPanel);
                UpdateSyncedAccountLabels(accountNode);
            }
            else if (_vm.SettingUpAccounts.Contains(accountNode.AccountId))
            {
                ShowPanel(_availableAccountPanel);
                _availableAccountLabel.Text = $"{accountNode.Name} is being set up...";
                _addAccountButton.Visible = false;
            }
            else
            {
                ShowPanel(_availableAccountPanel);
                _availableAccountLabel.Text = $"{accountNode.Name} is not currently synced.";
                _addAccountButton.Visible = true;
            }
        }
        else
        {
            // No selection — show version info
            if (!_vm.IsConnected)
            {
                _noSelectionLabel.Text = "Service is not running. Click \"Start Service\" to connect.";
            }
            else
            {
                UpdateVersionDisplay();
            }
            ShowPanel(_noSelectionLabel);
        }
    }

    private void RenderActivityView()
    {
        if (!_vm.IsConnected)
        {
            _activityListView.Visible = false;
            _activityEmptyLabel.Visible = false;
            _activityListView.Items.Clear();
            return;
        }

        // No activity display when nothing selected
        var selectedNode = _treeView.SelectedNode;
        if (selectedNode == null)
        {
            _activityListView.Visible = false;
            _activityEmptyLabel.Visible = false;
            _activityListView.Items.Clear();
            return;
        }

        var allEntries = new List<(string DisplayName, string AccountId, string LoginId, OutboxEntry Entry)>();
        var allDownloads = new List<(string DisplayName, string AccountId, string LoginId, ActiveDownloadEntry Download)>();

        foreach (var acct in _vm.Accounts)
        {
            if (!_vm.ActivityCache.TryGetValue(acct.AccountId, out var snap)) continue;

            foreach (var entry in snap.ActiveEntries)
                allEntries.Add((acct.DisplayName, acct.AccountId, acct.LoginId, entry));
            foreach (var entry in snap.ErrorEntries)
                allEntries.Add((acct.DisplayName, acct.AccountId, acct.LoginId, entry));
            foreach (var entry in snap.PendingEntries)
                allEntries.Add((acct.DisplayName, acct.AccountId, acct.LoginId, entry));
            foreach (var entry in snap.RejectedEntries)
                allEntries.Add((acct.DisplayName, acct.AccountId, acct.LoginId, entry));

            if (snap.ActiveDownloads is { } dls)
                foreach (var dl in dls)
                    allDownloads.Add((acct.DisplayName, acct.AccountId, acct.LoginId, dl));
        }

        // Filter by selection
        if (_vm.SelectedAccountId != null)
        {
            allEntries = allEntries.Where(e => e.AccountId == _vm.SelectedAccountId).ToList();
            allDownloads = allDownloads.Where(d => d.AccountId == _vm.SelectedAccountId).ToList();
        }
        else if (_vm.SelectedLoginId != null)
        {
            allEntries = allEntries.Where(e => e.LoginId == _vm.SelectedLoginId).ToList();
            allDownloads = allDownloads.Where(d => d.LoginId == _vm.SelectedLoginId).ToList();
        }

        bool hasSyncedSelection = selectedNode?.Tag is AccountNode selAcct && selAcct.IsSynced
            || (selectedNode?.Tag is LoginNode && _vm.Accounts.Any(a => a.LoginId == _vm.SelectedLoginId));

        RefreshActivityList(allEntries, allDownloads, hasSyncedSelection);
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _operationInProgress = true;
        var result = MessageBox.Show(
            "This will close the FileNodeClient tray icon.\n\n" +
            "Your files will continue to sync in the background.",
            "FileNodeClient",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (result == DialogResult.OK)
        {
            _appCts.Cancel();
            Application.ExitThread();
        }
        _operationInProgress = false;
    }

    private void UpdateServiceButtons()
    {
        var connected = _serviceClient.IsConnected;
        _addLoginButton.Enabled = connected;
        _startServiceButton.Visible = !connected;
        _startServiceButton.Enabled = true;
        _startServiceButton.Text = "Start Service";
        _stopServiceButton.Visible = connected;
        _stopServiceButton.Enabled = true;
        _stopServiceButton.Text = "Stop Service";
        _restartServiceButton.Visible = connected;
        _restartServiceButton.Enabled = true;
        _restartServiceButton.Text = "Restart Service";
    }

    private async void OnStartServiceClicked(object? sender, EventArgs e)
    {
        _startServiceButton.Enabled = false;
        _startServiceButton.Text = "Starting...";

        var started = ServiceLauncher.TryStartService();
        if (!started)
        {
            _startServiceButton.Text = "Start Service";
            _startServiceButton.Enabled = true;
            MessageBox.Show("Could not start the service.\nFileNodeClient.Service.exe was not found.",
                "FileNodeClient", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Wait up to 10s for connection, then re-enable button
        for (var i = 0; i < 20 && !_serviceClient.IsConnected; i++)
            await Task.Delay(500);

        if (!_serviceClient.IsConnected)
        {
            _startServiceButton.Text = "Start Service";
            _startServiceButton.Enabled = true;
        }
    }

    private async void OnStopServiceClicked(object? sender, EventArgs e)
    {
        _stopServiceButton.Enabled = false;
        _restartServiceButton.Enabled = false;
        _stopServiceButton.Text = "Stopping...";

        ServiceLauncher.StopService();
        await ServiceLauncher.WaitForExitAsync();

        UpdateServiceButtons();
    }

    private async void OnRestartServiceClicked(object? sender, EventArgs e)
    {
        _stopServiceButton.Enabled = false;
        _restartServiceButton.Enabled = false;
        _restartServiceButton.Text = "Restarting...";

        ServiceLauncher.StopService();
        if (!await ServiceLauncher.WaitForExitAsync())
        {
            UpdateServiceButtons();
            MessageBox.Show("Could not stop the service.", "FileNodeClient",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Brief pause to let the pipe fully close before restarting
        await Task.Delay(500);

        var started = ServiceLauncher.TryStartService();
        if (!started)
        {
            UpdateServiceButtons();
            MessageBox.Show("Service stopped but could not restart.\nFileNodeClient.Service.exe was not found.",
                "FileNodeClient", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Wait up to 10s for reconnection
        for (var i = 0; i < 20 && !_serviceClient.IsConnected; i++)
            await Task.Delay(500);

        UpdateServiceButtons();
    }

    private void OnOpenFolderClicked(object? sender, EventArgs e)
    {
        if (_treeView.SelectedNode?.Tag is AccountNode { IsSynced: true, SyncInfo: not null } accountNode)
            TrayIcon.OpenSyncFolder(accountNode.SyncInfo.SyncRootPath);
    }

    private void OnActivityPushed(ActivitySnapshot snapshot)
    {
        if (InvokeRequired) { BeginInvoke(() => OnActivityPushed(snapshot)); return; }
        _vm.ActivityCache[snapshot.AccountId] = snapshot;
        ScheduleRender();
    }

    private async void FetchServiceVersion()
    {
        try
        {
            _vm.ServiceVersion = await _serviceClient.GetVersionAsync();
            ScheduleRender();
        }
        catch { /* IPC not available */ }
    }

    private void UpdateVersionDisplay()
    {
        var appVersion = VersionHelper.GetVersionInfo();
        var lines = new List<string>();
        lines.Add($"App: {appVersion.Version}  ({FormatBuildDate(appVersion.BuildDate)})");
        if (_vm.ServiceVersion != null)
            lines.Add($"Service: {_vm.ServiceVersion.Version}  ({FormatBuildDate(_vm.ServiceVersion.BuildDate)})");
        else
            lines.Add("Service: not connected");
        _noSelectionLabel.Text = string.Join("\n", lines);
    }

    private static string FormatBuildDate(string buildDate)
    {
        if (DateTime.TryParse(buildDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return buildDate;
    }

    private async void FetchInitialActivity()
    {
        try
        {
            var accounts = _serviceClient.Accounts;
            var tasks = accounts.Select(a => _serviceClient.GetActivityAsync(a.AccountId)).ToList();
            var results = await Task.WhenAll(tasks);

            for (int i = 0; i < results.Length; i++)
                _vm.ActivityCache[results[i].AccountId] = results[i];

            ScheduleRender();
        }
        catch
        {
            // IPC not available
        }
    }

    private void RefreshActivityList(
        List<(string DisplayName, string AccountId, string LoginId, OutboxEntry Entry)> entries,
        List<(string DisplayName, string AccountId, string LoginId, ActiveDownloadEntry Download)> downloads,
        bool showEmptyLabel = true)
    {
        var now = DateTime.UtcNow;

        // Build all items into a flat list, then sort by progress descending
        // IsRejected flag distinguishes permanent rejections from transient errors
        // ActivityTag carries context for context menu actions
        var desired = new List<(string Key, string Col0, string Col1, string Col2, string Col3, string Col4,
            Color ForeColor, string Tooltip, int? ProgressTag, int SortProgress, bool IsRejected,
            ActivityItemTag? Tag)>();

        // Downloads (active and pending)
        foreach (var (displayName, accountId, _, dl) in downloads)
        {
            if (dl.IsPending)
            {
                desired.Add(($"pending:{accountId}:{dl.FileName}", dl.FileName, FormatFileSize(dl.TotalSize), "Download",
                    "Queued", "", Color.Gray, dl.FileName, null, -2, false, null));
            }
            else
            {
                var statusText = dl.Progress.HasValue ? "" : "Downloading...";
                var tooltip = dl.Progress.HasValue ? $"Downloading {dl.Progress.Value}%" : dl.FileName;
                desired.Add(($"dl:{accountId}:{dl.FileName}", dl.FileName, FormatFileSize(dl.TotalSize), "Download",
                    statusText, FormatRelativeTime(dl.StartedAt, now),
                    Color.DodgerBlue, tooltip, dl.Progress, dl.Progress ?? -1, false, null));
            }
        }

        // Outbox entries
        foreach (var (displayName, accountId, _, entry) in entries)
        {
            var name = entry.LocalPath != null
                ? Path.GetFileName(entry.LocalPath)
                : entry.NodeId ?? "(unknown)";

            var action = DeriveAction(entry);
            string status;
            int? progressPercent = null;
            int sortProgress = -2;
            if (entry.IsProcessing)
            {
                if (entry.UploadedBytes.HasValue && entry.FileSize.HasValue && entry.FileSize.Value > 0)
                    progressPercent = (int)(entry.UploadedBytes.Value * 100 / entry.FileSize.Value);
                else if (entry.UploadedBytes.HasValue)
                    progressPercent = 0;
                status = progressPercent.HasValue ? "" : "Syncing...";
                sortProgress = progressPercent ?? -1;
            }
            else
            {
                status = DeriveStatus(entry, now);
            }

            var tooltip = entry.LocalPath ?? entry.NodeId ?? "";
            if (entry.LastError != null)
                tooltip += $"\nError: {entry.LastError}";

            // Permanent rejections = red (user must fix), temp errors = orange (will retry)
            var color = entry.IsRejected ? Color.Red
                : entry.IsProcessing ? Color.DodgerBlue
                : entry.LastError != null ? Color.FromArgb(200, 120, 0)
                : _activityListView.ForeColor;

            // Rejected and errored entries sort in active tier so they stay visible
            if (entry.IsRejected || entry.LastError != null)
                sortProgress = -1;

            var tag = entry.IsRejected
                ? new ActivityItemTag(accountId, entry.Id, entry.LocalPath)
                : null;

            desired.Add(($"outbox:{entry.Id}", name, FormatFileSize(entry.FileSize), action,
                status, FormatRelativeTime(entry.UpdatedAt, now),
                color, tooltip, progressPercent, sortProgress, entry.IsRejected, tag));
        }

        // Sort: highest progress first, then in-progress without %, then queued/waiting
        // Stable tiebreaker by key to prevent items at similar progress from swapping
        desired.Sort((a, b) =>
        {
            var c = b.SortProgress.CompareTo(a.SortProgress);
            return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
        });

        // Cap the list: show active/rejected/errored items (progress >= -1) individually,
        // collapse remaining pending items into a single summary row.
        const int MaxVisiblePending = 5;
        const int MaxVisibleRejected = 5;

        // Count active-tier items (sortProgress >= -1)
        int activeCount = 0;
        while (activeCount < desired.Count && desired[activeCount].SortProgress >= -1)
            activeCount++;

        // Among the active tier, count permanent rejections
        int rejectedInActive = 0;
        for (int i = 0; i < activeCount; i++)
            if (desired[i].IsRejected)
                rejectedInActive++;

        int pendingCount = desired.Count - activeCount;
        var kept = new List<(string Key, string Col0, string Col1, string Col2, string Col3, string Col4,
            Color ForeColor, string Tooltip, int? ProgressTag, int SortProgress, bool IsRejected,
            ActivityItemTag? Tag)>();

        // Keep all active items, cap rejected to MaxVisibleRejected
        int shownRejected = 0;
        for (int i = 0; i < activeCount; i++)
        {
            if (desired[i].IsRejected && shownRejected >= MaxVisibleRejected)
                continue;
            if (desired[i].IsRejected) shownRejected++;
            kept.Add(desired[i]);
        }

        if (rejectedInActive > MaxVisibleRejected)
        {
            int hiddenRejected = rejectedInActive - MaxVisibleRejected;
            kept.Add(("summary:rejected", $"+ {hiddenRejected:N0} more rejected", "", "",
                "", "",
                Color.Red, $"{hiddenRejected} additional files rejected", null, -1, true, null));
        }

        // Add pending items with cap
        int shownPending = 0;
        for (int i = activeCount; i < desired.Count && shownPending < MaxVisiblePending; i++, shownPending++)
            kept.Add(desired[i]);

        if (pendingCount > MaxVisiblePending)
        {
            int hiddenCount = pendingCount - MaxVisiblePending;
            kept.Add(("summary:pending", $"+ {hiddenCount:N0} more pending", "", "",
                "", "",
                Color.Gray, $"{hiddenCount} additional items queued", null, -3, false, null));
        }

        desired = kept;

        // Toggle between empty label and list view
        if (desired.Count == 0)
        {
            _activityListView.Visible = false;
            _activityEmptyLabel.Visible = showEmptyLabel;
            _activityListView.Items.Clear();
            return;
        }

        _activityEmptyLabel.Visible = false;
        _activityListView.Visible = true;

        // Minimal diff: match by key, move existing rows, only touch what changed
        _activityListView.BeginUpdate();

        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];

            if (i < _activityListView.Items.Count && _activityListView.Items[i].Name == d.Key)
            {
                // Key already in the right position — update fields only if changed
                UpdateItemFields(_activityListView.Items[i], d);
            }
            else
            {
                // Look for this key later in the current list
                int foundAt = -1;
                for (int j = i + 1; j < _activityListView.Items.Count; j++)
                {
                    if (_activityListView.Items[j].Name == d.Key)
                    {
                        foundAt = j;
                        break;
                    }
                }

                if (foundAt >= 0)
                {
                    // Move existing item from foundAt to position i
                    var item = _activityListView.Items[foundAt];
                    _activityListView.Items.RemoveAt(foundAt);
                    _activityListView.Items.Insert(i, item);
                    UpdateItemFields(item, d);
                }
                else
                {
                    // New item — insert at position i
                    var item = new ListViewItem(d.Col0) { Name = d.Key };
                    item.SubItems.Add(d.Col1);
                    item.SubItems.Add(d.Col2);
                    var statusSub = item.SubItems.Add(d.Col3);
                    if (d.ProgressTag.HasValue)
                        statusSub.Tag = d.ProgressTag.Value;
                    item.SubItems.Add(d.Col4);
                    item.ForeColor = d.ForeColor;
                    item.ToolTipText = d.Tooltip;
                    item.Tag = d.Tag;
                    _activityListView.Items.Insert(i, item);
                }
            }
        }

        // Remove any trailing items no longer in the desired list
        while (_activityListView.Items.Count > desired.Count)
            _activityListView.Items.RemoveAt(_activityListView.Items.Count - 1);

        _activityListView.EndUpdate();

        // Ensure the list is scrolled to the top — item insertions/removals
        // can leave the scroll position so that items render above the viewport.
        if (_activityListView.Items.Count > 0)
            _activityListView.EnsureVisible(0);
    }

    private void UpdateItemFields(ListViewItem item,
        (string Key, string Col0, string Col1, string Col2, string Col3, string Col4,
         Color ForeColor, string Tooltip, int? ProgressTag, int SortProgress, bool IsRejected,
         ActivityItemTag? Tag) d)
    {
        if (item.Text != d.Col0) item.Text = d.Col0;
        if (item.SubItems[1].Text != d.Col1) item.SubItems[1].Text = d.Col1;
        if (item.SubItems[2].Text != d.Col2) item.SubItems[2].Text = d.Col2;
        if (item.SubItems[3].Text != d.Col3) item.SubItems[3].Text = d.Col3;

        var oldTag = item.SubItems[3].Tag as int?;
        if (oldTag != d.ProgressTag)
        {
            item.SubItems[3].Tag = d.ProgressTag.HasValue ? (object)d.ProgressTag.Value : null;
            _activityListView.Invalidate(item.SubItems[3].Bounds);
        }

        if (item.SubItems[4].Text != d.Col4) item.SubItems[4].Text = d.Col4;
        if (item.ForeColor != d.ForeColor) item.ForeColor = d.ForeColor;
        if (item.ToolTipText != d.Tooltip) item.ToolTipText = d.Tooltip;
        item.Tag = d.Tag;
    }

    private void OnActivityListMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _activityListView.HitTest(e.Location);
        if (hit.Item?.Tag is not ActivityItemTag tag) return;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Retry upload", null, async (_, _) =>
        {
            try { await _serviceClient.RetryRejectedAsync(tag.AccountId, tag.EntryId); }
            catch (Exception ex) { Log.Error($"Retry rejected failed: {ex.Message}"); }
        });
        menu.Items.Add("Dismiss", null, async (_, _) =>
        {
            try { await _serviceClient.DismissRejectedAsync(tag.AccountId, tag.EntryId); }
            catch (Exception ex) { Log.Error($"Dismiss rejected failed: {ex.Message}"); }
        });
        if (tag.LocalPath != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Delete local file", null, async (_, _) =>
            {
                try
                {
                    if (File.Exists(tag.LocalPath))
                        File.Delete(tag.LocalPath);
                    await _serviceClient.DismissRejectedAsync(tag.AccountId, tag.EntryId);
                }
                catch (Exception ex) { Log.Error($"Delete rejected file failed: {ex.Message}"); }
            });
        }
        menu.Show(_activityListView, e.Location);
    }

    private void AutoSizeActivityColumns()
    {
        if (_activityListView.Columns.Count < 5) return;
        var w = _activityListView.ClientSize.Width;
        var em = Font.Height;
        // Fixed columns: Size, Action, Status, Updated
        var sizeW = (int)(4.5 * em);
        var actionW = (int)(5.5 * em);
        var statusW = (int)(5.5 * em);
        var updatedW = (int)(6.5 * em);
        var nameW = Math.Max(4 * em, w - sizeW - actionW - statusW - updatedW);
        _activityListView.Columns[0].Width = nameW;
        _activityListView.Columns[1].Width = sizeW;
        _activityListView.Columns[2].Width = actionW;
        _activityListView.Columns[3].Width = statusW;
        _activityListView.Columns[4].Width = updatedW;
    }

    private void OnDrawActivitySubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex == 3 && e.SubItem?.Tag is int percent)
        {
            var g = e.Graphics!;
            var bounds = e.Bounds;

            using (var bgBrush = new SolidBrush(_activityListView.BackColor))
                g.FillRectangle(bgBrush, bounds);

            var barHeight = Math.Max(4, bounds.Height / 3);
            var barY = bounds.Y + (bounds.Height - barHeight) / 2;
            var trackRect = new Rectangle(bounds.X + 4, barY, bounds.Width - 8, barHeight);

            using (var trackBrush = new SolidBrush(Color.FromArgb(228, 228, 228)))
                g.FillRectangle(trackBrush, trackRect);

            if (percent > 0)
            {
                var fillWidth = (int)(trackRect.Width * percent / 100.0);
                var fillRect = new Rectangle(trackRect.X, trackRect.Y, fillWidth, trackRect.Height);
                using var fillBrush = new SolidBrush(Color.DodgerBlue);
                g.FillRectangle(fillBrush, fillRect);
            }
        }
        else
        {
            e.DrawDefault = true;
        }
    }

    private static string DeriveAction(OutboxEntry entry)
    {
        if (entry.IsDeleted)
            return "Delete";
        if (entry.IsFolder && entry.NodeId == null)
            return "Create folder";
        if (entry.IsDirtyContent)
            return "Upload";
        if (entry.IsDirtyLocation && !entry.IsDirtyContent)
            return "Move";
        return "Sync";
    }

    private static string DeriveStatus(OutboxEntry entry, DateTime now)
    {
        if (entry.IsRejected)
            return "Rejected";
        if (entry.AttemptCount > 0 && entry.LastError != null)
            return $"Error ({entry.AttemptCount})";
        if (entry.NextRetryAfter.HasValue && entry.NextRetryAfter.Value > now)
            return "Waiting";
        if (entry.AttemptCount > 0)
            return "Retrying";
        return "Pending";
    }

    private static string FormatFileSize(long? bytes)
    {
        if (bytes == null) return "";
        double b = bytes.Value;
        if (b < 1024) return $"{b:0} B";
        if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
        if (b < 1024 * 1024 * 1024) return $"{b / (1024 * 1024):0.#} MB";
        return $"{b / (1024 * 1024 * 1024):0.##} GB";
    }

    private static string FormatRelativeTime(DateTime utcTime, DateTime now)
    {
        var elapsed = now - utcTime;
        if (elapsed.TotalSeconds < 60)
            return "just now";
        if (elapsed.TotalMinutes < 2)
            return "1 min ago";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalHours < 2)
            return "1 hr ago";
        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours} hr ago";
        return utcTime.ToLocalTime().ToString("g");
    }

    private async Task RefreshAllLoginAccountsAsync()
    {
        // Include all known logins — not just those with synced accounts —
        // so that non-synced accounts are discovered for newly added logins.
        var loginIds = _serviceClient.Accounts.Select(a => a.LoginId)
            .Union(_serviceClient.ConnectingLoginIds)
            .Union(_serviceClient.FailedLogins.Select(f => f.LoginId))
            .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

        // Phase 1: cached accounts (fast, all logins in parallel)
        var cachedTasks = loginIds.Select(async loginId =>
        {
            try
            {
                var result = await _serviceClient.GetLoginAccountsAsync(loginId);
                return (loginId, result, (Exception?)null);
            }
            catch (Exception ex) { return (loginId, (LoginAccountsResult?)null, (Exception?)ex); }
        }).ToList();

        foreach (var (loginId, result, _) in await Task.WhenAll(cachedTasks))
        {
            if (result == null) continue;
            _vm.LoginAccountResults[loginId] = result;
            if (result.Accounts != null)
                _vm.DiscoveredAccounts[loginId] = result.Accounts;
        }
        SnapshotServiceState();
        ScheduleRender();

        // Phase 2: fresh from server (slow, all logins in parallel)
        var refreshTasks = loginIds.Select(async loginId =>
        {
            try
            {
                var result = await _serviceClient.RefreshLoginAccountsAsync(loginId);
                return (loginId, result, (Exception?)null);
            }
            catch (Exception ex) { return (loginId, (LoginAccountsResult?)null, (Exception?)ex); }
        }).ToList();

        foreach (var (loginId, result, ex) in await Task.WhenAll(refreshTasks))
        {
            if (result != null)
            {
                _vm.LoginAccountResults[loginId] = result;
                if (result.Accounts != null)
                    _vm.DiscoveredAccounts[loginId] = result.Accounts;
            }
            else if (ex != null)
            {
                Log.Error($"Failed to refresh accounts for {loginId}: {ex.Message}");
            }
            _vm.RefreshedLogins.Add(loginId);
        }
        SnapshotServiceState();
        ScheduleRender();
    }

    /// <summary>
    /// Fetch discovered accounts for specific logins and refresh the tree.
    /// </summary>
    private async Task DiscoverAccountsForLoginsAsync(List<string> loginIds)
    {
        bool changed = false;
        foreach (var loginId in loginIds)
        {
            try
            {
                var result = await _serviceClient.GetLoginAccountsAsync(loginId);
                _vm.LoginAccountResults[loginId] = result;
                if (result.Accounts != null)
                {
                    _vm.DiscoveredAccounts[loginId] = result.Accounts;
                    changed = true;
                }
            }
            catch { }
        }

        foreach (var loginId in loginIds)
        {
            try
            {
                var result = await _serviceClient.RefreshLoginAccountsAsync(loginId);
                _vm.LoginAccountResults[loginId] = result;
                if (result.Accounts != null)
                {
                    _vm.DiscoveredAccounts[loginId] = result.Accounts;
                    changed = true;
                }
                _vm.RefreshedLogins.Add(loginId);
            }
            catch
            {
                _vm.RefreshedLogins.Add(loginId);
            }
        }

        if (changed)
            ScheduleRender();
    }

    private void OnSelectionChanged()
    {
        var node = _treeView.SelectedNode;
        _vm.SelectedLoginId = (node?.Tag is LoginNode ln) ? ln.LoginId : null;
        _vm.SelectedAccountId = (node?.Tag is AccountNode an) ? an.AccountId : null;
        ScheduleRender();
    }

    private void UpdateSyncedAccountLabels(AccountNode accountNode)
    {
        var info = accountNode.SyncInfo!;
        _syncedAccountName.Text = info.DisplayName;

        if (accountNode.IsMissing)
        {
            _accountStatusLabel.Text = "This account is no longer available on the server.";
            _accountStatusLabel.ForeColor = Color.FromArgb(200, 120, 0);
            _refreshButton.Visible = false;
            _cleanButton.Visible = false;
            _openFolderButton.Visible = false;
        }
        else
        {
            var isDiskFull = info.PauseReason?.Contains("DiskFull") == true;
            var isUserPaused = info.PauseReason?.Contains("UserRequested") == true;
            var isMetered = info.PauseReason?.Contains("MeteredConnection") == true;
            var isPaused = info.Status == AccountStatus.Paused;
            var rejectedCount = _serviceClient.GetRejectedCount(accountNode.AccountId);

            _accountStatusLabel.Text = info.Status switch
            {
                AccountStatus.Paused when isDiskFull =>
                    "Status: Paused \u2014 disk space is low. Free up space to resume.",
                AccountStatus.Paused when isUserPaused && isMetered =>
                    "Status: Paused by user (metered connection)",
                AccountStatus.Paused when isUserPaused =>
                    "Status: Paused by user",
                AccountStatus.Paused when isMetered =>
                    "Status: Metered connection \u2014 uploads paused",
                AccountStatus.Idle when info.PendingCount > 0 => $"Status: {info.PendingCount} pending",
                AccountStatus.Idle when rejectedCount > 0 => rejectedCount == 1
                    ? "Status: 1 file rejected"
                    : $"Status: {rejectedCount} files rejected",
                AccountStatus.Idle => "Status: Up to date",
                AccountStatus.Syncing => "Status: Syncing",
                AccountStatus.Error => $"Status: Error \u2014 {info.StatusDetail}",
                AccountStatus.Disconnected => "Status: Offline",
                _ => "Status: Unknown",
            };
            _accountStatusLabel.ForeColor = isDiskFull ? Color.FromArgb(200, 50, 50)
                : rejectedCount > 0 ? Color.Red
                : default;
            _refreshButton.Visible = true;
            _cleanButton.Visible = true;
            _openFolderButton.Visible = true;

            // Show Pause when actively syncing, Resume when user-paused
            _pauseButton.Visible = !isPaused;
            _resumeButton.Visible = isUserPaused;
            // Sync Now available when paused (metered or user), but disabled when disk full
            _syncNowButton.Visible = isPaused && !isDiskFull;
        }
        _syncFolderLabel.Text = $"Folder: {info.SyncRootPath}";

        if (info.QuotaUsed.HasValue && info.QuotaLimit.HasValue && info.QuotaLimit.Value > 0)
        {
            _quotaLabel.Text = $"Storage: {FormatBytes(info.QuotaUsed.Value)} of {FormatBytes(info.QuotaLimit.Value)} used";
            _quotaLabel.Visible = true;
        }
        else
        {
            _quotaLabel.Visible = false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{bytes / (double)(1L << 30):0.#} GB";
        if (bytes >= 1L << 20)
            return $"{bytes / (double)(1L << 20):0.#} MB";
        if (bytes >= 1L << 10)
            return $"{bytes / (double)(1L << 10):0.#} KB";
        return $"{bytes} bytes";
    }

    private void OnAddLoginClicked(object? sender, EventArgs e)
    {
        _operationInProgress = true;
        try
        {
            using var addForm = new AddAccountForm(_serviceClient);
            if (addForm.ShowDialog(this) == DialogResult.OK)
            {
                // Mark selected accounts as "Setting up" until their supervisor appears
                if (addForm.EnabledAccountIds != null)
                    foreach (var id in addForm.EnabledAccountIds)
                        _vm.SettingUpAccounts.Add(id);
                _ = RefreshAllLoginAccountsAsync();
            }
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private async void OnUpdateCredentialsClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not LoginNode loginNode)
            return;

        var sessionUrl = _sessionUrlBox.Text.Trim();
        var token = _tokenBox.Text.Trim();

        if (string.IsNullOrEmpty(token))
        {
            MessageBox.Show("Please enter a token.", "Missing Token",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrEmpty(sessionUrl))
        {
            MessageBox.Show("Please enter a session URL.", "Missing URL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetAllButtonsEnabled(false);
        try
        {
            await _serviceClient.UpdateLoginAsync(loginNode.LoginId, sessionUrl, token);
            _tokenBox.Text = "";
            _ = RefreshAllLoginAccountsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to update credentials: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnReauthenticateClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not LoginNode loginNode)
            return;

        SetAllButtonsEnabled(false);
        _reauthStatusLabel.ForeColor = Color.DodgerBlue;
        _reauthStatusLabel.Text = "";

        try
        {
            using var flow = new OAuthLoginFlow();
            var progress = new Progress<string>(msg =>
            {
                if (InvokeRequired)
                    BeginInvoke(() => _reauthStatusLabel.Text = msg);
                else
                    _reauthStatusLabel.Text = msg;
            });

            // Preserve non-standard session URLs (e.g. beta) during reauth;
            // for standard api.fastmail.com logins, let discovery determine the URL
            string? sessionUrlOverride = null;
            var currentUrl = _sessionUrlBox.Text.Trim();
            if (!string.IsNullOrEmpty(currentUrl) &&
                !currentUrl.Contains("api.fastmail.com", StringComparison.OrdinalIgnoreCase))
                sessionUrlOverride = currentUrl;
            var cred = await flow.SignInAsync(progress,
                sessionUrlOverride: sessionUrlOverride);

            _reauthStatusLabel.Text = "Updating credentials...";
            await _serviceClient.UpdateLoginAsync(
                loginNode.LoginId, cred.SessionUrl, cred.AccessToken,
                cred.RefreshToken, cred.TokenEndpoint, cred.ClientId,
                cred.ExpiresAt.ToUnixTimeSeconds());

            _reauthStatusLabel.Text = "Re-authenticated successfully.";
            _reauthStatusLabel.ForeColor = Color.Green;
            _tokenBox.Text = "";
            _ = RefreshAllLoginAccountsAsync();
        }
        catch (Exception ex)
        {
            _reauthStatusLabel.Text = $"Error: {ex.Message}";
            _reauthStatusLabel.ForeColor = Color.Red;
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnRemoveLoginClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not LoginNode loginNode)
            return;

        SetAllButtonsEnabled(false);

        var accountsForLogin = _serviceClient.Accounts.Where(a => a.LoginId == loginNode.LoginId).ToList();
        var accountWord = accountsForLogin.Count == 1 ? "account" : $"{accountsForLogin.Count} accounts";

        var result = MessageBox.Show(
            $"Remove login {loginNode.LoginId}?\n\n" +
            $"This will stop syncing {accountWord}, unregister all sync roots, " +
            "delete local files, and forget the stored credential.",
            "Remove Login",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            SetAllButtonsEnabled(true);
            return;
        }

        // Mark the login and its account nodes as "Removing..." in the tree
        var selectedNode = _treeView.SelectedNode;
        if (selectedNode != null)
        {
            selectedNode.Text += " \u2014 Removing...";
            selectedNode.ForeColor = Color.Gray;
            foreach (TreeNode child in selectedNode.Nodes)
            {
                child.Text = child.Text.Split('\u2014')[0].TrimEnd() + " \u2014 Removing...";
                child.ForeColor = Color.Gray;
            }
        }

        try
        {
            await _serviceClient.RemoveLoginAsync(loginNode.LoginId);
            _vm.DiscoveredAccounts.Remove(loginNode.LoginId);
            _vm.LoginAccountResults.Remove(loginNode.LoginId);
            _vm.RefreshedLogins.Remove(loginNode.LoginId);
            SnapshotServiceState();
            ScheduleRender();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to remove login: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnDetachClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true, SyncInfo: not null } accountNode)
            return;

        SetAllButtonsEnabled(false);

        var info = accountNode.SyncInfo;
        var result = MessageBox.Show(
            $"Detach {info.DisplayName}?\n\n" +
            "This will stop syncing and remove dehydrated (cloud-only) files.\n" +
            "Fully downloaded files will remain on disk in:\n" +
            $"{info.SyncRootPath}",
            "Detach Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
        {
            SetAllButtonsEnabled(true);
            return;
        }
        if (_treeView.SelectedNode != null)
        {
            _treeView.SelectedNode.Text = _treeView.SelectedNode.Text.Split('\u2014')[0].TrimEnd() + " \u2014 Detaching...";
            _treeView.SelectedNode.ForeColor = Color.Gray;
        }
        try
        {
            await _serviceClient.DetachAccountAsync(accountNode.AccountId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to detach account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true, SyncInfo: not null } accountNode)
            return;

        SetAllButtonsEnabled(false);

        var info = accountNode.SyncInfo;
        var result = MessageBox.Show(
            $"Remove {info.DisplayName}?\n\n" +
            "This will stop syncing, unregister the sync root, and DELETE ALL local files in:\n" +
            $"{info.SyncRootPath}",
            "Remove Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            SetAllButtonsEnabled(true);
            return;
        }
        if (_treeView.SelectedNode != null)
        {
            _treeView.SelectedNode.Text = _treeView.SelectedNode.Text.Split('\u2014')[0].TrimEnd() + " \u2014 Removing...";
            _treeView.SelectedNode.ForeColor = Color.Gray;
        }
        try
        {
            await _serviceClient.CleanUpAccountAsync(accountNode.AccountId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to remove account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true } accountNode)
            return;

        SetAllButtonsEnabled(false);

        var result = MessageBox.Show(
            $"Force refresh {accountNode.Name}?\n\n" +
            "This will delete the local cache and re-sync all files from the server.",
            "Refresh Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
        {
            SetAllButtonsEnabled(true);
            return;
        }
        if (_treeView.SelectedNode != null)
        {
            _treeView.SelectedNode.Text = _treeView.SelectedNode.Text.Split('\u2014')[0].TrimEnd() + " \u2014 Refreshing...";
            _treeView.SelectedNode.ForeColor = Color.Gray;
        }
        try
        {
            await _serviceClient.RefreshAccountAsync(accountNode.AccountId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to refresh account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnCleanClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true, SyncInfo: not null } accountNode)
            return;

        SetAllButtonsEnabled(false);

        var info = accountNode.SyncInfo;
        var result = MessageBox.Show(
            $"Clean and rebuild {info.DisplayName}?\n\n" +
            "This will DELETE ALL local files and recreate everything from scratch.\n" +
            $"Folder: {info.SyncRootPath}",
            "Clean Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            SetAllButtonsEnabled(true);
            return;
        }

        // Mark the node as "Cleaning..." in the tree
        if (_treeView.SelectedNode != null)
        {
            _treeView.SelectedNode.Text = _treeView.SelectedNode.Text.Split('\u2014')[0].TrimEnd() + " \u2014 Cleaning...";
            _treeView.SelectedNode.ForeColor = Color.Gray;
        }

        try
        {
            await _serviceClient.CleanAccountAsync(accountNode.AccountId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to clean account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnAddAccountClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: false } accountNode)
            return;

        SetAllButtonsEnabled(false);
        _vm.SettingUpAccounts.Add(accountNode.AccountId);
        try
        {
            await _serviceClient.EnableAccountAsync(accountNode.LoginId, accountNode.AccountId);
            await RefreshAllLoginAccountsAsync();
        }
        catch (Exception ex)
        {
            _vm.SettingUpAccounts.Remove(accountNode.AccountId);
            MessageBox.Show($"Failed to add account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnPauseClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true } accountNode)
            return;

        try { await _serviceClient.PauseAccountAsync(accountNode.AccountId); }
        catch (Exception ex) { Log.Error($"Pause failed: {ex.Message}"); }
    }

    private async void OnResumeClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true } accountNode)
            return;

        try { await _serviceClient.ResumeAccountAsync(accountNode.AccountId); }
        catch (Exception ex) { Log.Error($"Resume failed: {ex.Message}"); }
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true } accountNode)
            return;

        try { await _serviceClient.SyncNowAsync(accountNode.AccountId); }
        catch (Exception ex) { Log.Error($"Sync now failed: {ex.Message}"); }
    }

    private void SetAllButtonsEnabled(bool enabled)
    {
        _operationInProgress = !enabled;
        _openFolderButton.Enabled = enabled;
        _detachButton.Enabled = enabled;
        _removeButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
        _cleanButton.Enabled = enabled;
        _pauseButton.Enabled = enabled;
        _resumeButton.Enabled = enabled;
        _syncNowButton.Enabled = enabled;
        _removeLoginButton.Enabled = enabled;
        _updateCredentialsButton.Enabled = enabled;
        _reauthenticateButton.Enabled = enabled;
        _addAccountButton.Enabled = enabled;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        AutoSizeToContent();
        PositionNearTray();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            SnapshotServiceState();
            Render();
            FetchInitialActivity();
            EnsureOnScreen();
        }
        else
        {
            _vm.ActivityCache.Clear();
        }
    }

    private void PositionNearTray()
    {
        var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        var x = screen.Right - Width - 8;
        var y = screen.Bottom - Height - 8;
        Location = new Point(x, y);
    }

    /// <summary>
    /// If the form is off-screen (e.g. monitor disconnected), reposition near tray.
    /// </summary>
    private void EnsureOnScreen()
    {
        var bounds = Bounds;
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(bounds))
                return; // at least partially visible
        }
        PositionNearTray();
    }

    private void AutoSizeToContent()
    {
        var em = Font.Height; // base unit — scales with font size

        // Measure the widest tree node text
        int maxNodeWidth = 0;
        int indent = _treeView.Indent + em; // expand glyph + padding
        using (var g = _treeView.CreateGraphics())
        {
            foreach (TreeNode root in _treeView.Nodes)
            {
                var rootFont = root.NodeFont ?? _treeView.Font;
                var w = (int)Math.Ceiling(g.MeasureString(root.Text, rootFont).Width);
                maxNodeWidth = Math.Max(maxNodeWidth, w);

                foreach (TreeNode child in root.Nodes)
                {
                    var childFont = child.NodeFont ?? _treeView.Font;
                    var cw = (int)Math.Ceiling(g.MeasureString(child.Text, childFont).Width);
                    maxNodeWidth = Math.Max(maxNodeWidth, cw + indent);
                }
            }
        }

        // Tree view width: measured content + scrollbar + padding
        var scrollBarWidth = SystemInformation.VerticalScrollBarWidth;
        var treeWidth = Math.Max(maxNodeWidth + scrollBarWidth + em, 20 * em);

        // Measure widest right-panel content (login IDs shown as bold headers)
        var minDetailWidth = 28 * em; // enough for typical session URLs
        int maxDetailWidth = minDetailWidth;
        using (var g2 = _detailPanel.CreateGraphics())
        {
            using var boldFont = new Font(Font, FontStyle.Bold);
            foreach (TreeNode root in _treeView.Nodes)
            {
                if (root.Tag is LoginNode ln)
                {
                    var w = (int)Math.Ceiling(g2.MeasureString(ln.LoginId, boldFont).Width);
                    maxDetailWidth = Math.Max(maxDetailWidth, w);
                }
            }
        }
        var detailWidth = maxDetailWidth + 2 * em; // breathing room

        // Total: tree + splitter + detail + form chrome
        var formChrome = Width - ClientSize.Width;
        var idealWidth = treeWidth + 4 + detailWidth + _detailPanel.Padding.Horizontal + formChrome;

        // Clamp to screen working area (leave some margin)
        var screen = Screen.FromControl(this).WorkingArea;
        var screenMargin = 5 * em;
        var maxWidth = screen.Width - screenMargin;
        var maxHeight = screen.Height - screenMargin;

        var finalWidth = Math.Clamp(idealWidth, MinimumSize.Width, maxWidth);
        var idealHeight = 32 * em; // ~32 lines tall
        var finalHeight = Math.Clamp(idealHeight, MinimumSize.Height, maxHeight);

        Size = new Size(finalWidth, finalHeight);
        _treeView.Width = treeWidth;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _renderTimer.Stop();
        _renderTimer.Dispose();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosing(e);
    }
}
