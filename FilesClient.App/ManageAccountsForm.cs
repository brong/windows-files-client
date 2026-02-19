using System.Drawing;
using FilesClient.Ipc;

namespace FilesClient.App;

sealed class ManageAccountsForm : Form
{
    private readonly ServiceClient _serviceClient;
    private readonly TreeView _treeView;
    private readonly Panel _detailPanel;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // Detail panel controls — login selected
    private readonly Panel _loginPanel;
    private readonly Label _loginIdLabel;
    private readonly TextBox _sessionUrlBox;
    private readonly TextBox _tokenBox;
    private readonly Button _updateCredentialsButton;
    private readonly Button _removeLoginButton;

    // Detail panel controls — synced account selected
    private readonly Panel _syncedAccountPanel;
    private readonly Label _syncedAccountName;
    private readonly Label _accountStatusLabel;
    private readonly Label _syncFolderLabel;
    private readonly Button _detachButton;
    private readonly Button _removeButton;
    private readonly Button _refreshButton;
    private readonly Button _cleanButton;

    // Detail panel controls — non-synced account selected
    private readonly Panel _availableAccountPanel;
    private readonly Label _availableAccountLabel;
    private readonly Button _addAccountButton;

    // No selection panel
    private readonly Label _noSelectionLabel;

    // Data model
    private record LoginNode(string LoginId);
    private record AccountNode(string LoginId, string AccountId, string Name,
        bool IsSynced, bool IsMissing, AccountInfo? SyncInfo);

    // Discovered accounts per login (populated async on form open)
    private readonly Dictionary<string, List<DiscoveredAccount>> _discoveredAccounts = new();
    private readonly Dictionary<string, LoginAccountsResultEvent> _loginAccountResults = new();
    private readonly HashSet<string> _refreshedLogins = new();

    public ManageAccountsForm(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "Fastmail Files - Manage Accounts";
        MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

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
        _treeView.AfterSelect += (_, _) => OnSelectionChanged();

        var splitter = new Splitter { Dock = DockStyle.Left, Width = 4 };

        // --- Detail panel on the right ---
        _detailPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 12),
        };

        // ======= Login detail panel =======
        _loginPanel = new Panel { Dock = DockStyle.Fill, Visible = false };

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

        _loginPanel.Controls.Add(loginLayout);

        // ======= Synced account detail panel =======
        _syncedAccountPanel = new Panel { Dock = DockStyle.Fill, Visible = false };

        var syncedLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = Padding.Empty,
        };
        syncedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _syncedAccountName = new Label
        {
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        syncedLayout.Controls.Add(_syncedAccountName);

        _accountStatusLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };
        syncedLayout.Controls.Add(_accountStatusLabel);

        _syncFolderLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80),
            Margin = new Padding(0, 0, 0, 16),
        };
        syncedLayout.Controls.Add(_syncFolderLabel);

        var syncedButtonFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 0),
            Padding = Padding.Empty,
        };

        _detachButton = new Button { Text = "Detach", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _detachButton.Click += OnDetachClicked;

        _removeButton = new Button { Text = "Remove", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _removeButton.Click += OnRemoveClicked;

        _refreshButton = new Button { Text = "Refresh", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 8, 6) };
        _refreshButton.Click += OnRefreshClicked;

        _cleanButton = new Button { Text = "Clean", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 0, 6) };
        _cleanButton.Click += OnCleanClicked;

        syncedButtonFlow.Controls.AddRange([_detachButton, _removeButton, _refreshButton, _cleanButton]);
        syncedLayout.Controls.Add(syncedButtonFlow);

        _syncedAccountPanel.Controls.Add(syncedLayout);

        // ======= Available (non-synced) account panel =======
        _availableAccountPanel = new Panel { Dock = DockStyle.Fill, Visible = false };

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

        _detailPanel.Controls.AddRange([_loginPanel, _syncedAccountPanel, _availableAccountPanel, _noSelectionLabel]);

        // --- Bottom bar ---
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
        };

        var addLoginButton = new Button
        {
            Text = "Add Login...",
            AutoSize = true,
            Height = 30,
            Location = new Point(10, 9),
        };
        addLoginButton.Click += OnAddLoginClicked;

        var closeButton = new Button
        {
            Text = "Close",
            AutoSize = true,
            Height = 30,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        closeButton.Location = new Point(bottomPanel.Width - closeButton.Width - 14, 9);
        closeButton.Click += (_, _) => Close();

        bottomPanel.Controls.AddRange([addLoginButton, closeButton]);

        // --- Assemble ---
        Controls.Add(_detailPanel);
        Controls.Add(splitter);
        Controls.Add(_treeView);
        Controls.Add(bottomPanel);

        _serviceClient.AccountsChanged += () =>
        {
            if (InvokeRequired)
                BeginInvoke(RefreshTree);
            else
                RefreshTree();
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshTree();
        _refreshTimer.Start();

        RefreshTree();
        _ = RefreshAllLoginAccountsAsync();
    }

    private async Task RefreshAllLoginAccountsAsync()
    {
        var loginIds = _serviceClient.Accounts
            .Select(a => a.LoginId)
            .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

        // Phase 1: cached accounts (fast)
        foreach (var loginId in loginIds)
        {
            try
            {
                var result = await _serviceClient.GetLoginAccountsAsync(loginId);
                _loginAccountResults[loginId] = result;
                if (result.Accounts != null)
                    _discoveredAccounts[loginId] = result.Accounts;
            }
            catch { }
        }
        if (InvokeRequired) BeginInvoke(RefreshTree); else RefreshTree();

        // Phase 2: fresh from server (slow)
        foreach (var loginId in loginIds)
        {
            try
            {
                var result = await _serviceClient.RefreshLoginAccountsAsync(loginId);
                _loginAccountResults[loginId] = result;
                if (result.Accounts != null)
                    _discoveredAccounts[loginId] = result.Accounts;
                _refreshedLogins.Add(loginId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to refresh accounts for {loginId}: {ex.Message}");
                _refreshedLogins.Add(loginId);
            }
        }
        if (InvokeRequired) BeginInvoke(RefreshTree); else RefreshTree();
    }

    private void RefreshTree()
    {
        var accounts = _serviceClient.Accounts;
        var connectingIds = _serviceClient.ConnectingLoginIds;

        // Preserve selection
        string? selectedLoginId = null;
        string? selectedAccountId = null;
        if (_treeView.SelectedNode?.Tag is LoginNode ln)
            selectedLoginId = ln.LoginId;
        else if (_treeView.SelectedNode?.Tag is AccountNode an)
            selectedAccountId = an.AccountId;

        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();

        // Group synced accounts by loginId
        var byLogin = accounts
            .Where(a => !string.IsNullOrEmpty(a.LoginId))
            .GroupBy(a => a.LoginId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allLoginIds = byLogin.Keys
            .Union(_discoveredAccounts.Keys)
            .Union(connectingIds)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        foreach (var loginId in allLoginIds)
        {
            var syncedAccounts = byLogin.GetValueOrDefault(loginId) ?? new List<AccountInfo>();
            var syncedIds = syncedAccounts.Select(a => a.AccountId).ToHashSet();

            // Show the login ID — split "user@host" for readability
            var parts = loginId.Split('@');
            var displayText = parts.Length >= 3
                ? $"{parts[0]}@{parts[1]} ({parts[2]})"  // user@domain@host -> user@domain (host)
                : loginId;

            var loginTreeNode = new TreeNode(displayText)
            {
                Tag = new LoginNode(loginId),
                NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            };

            if (connectingIds.Contains(loginId) && syncedAccounts.Count == 0)
            {
                loginTreeNode.Nodes.Add(new TreeNode("Connecting...") { ForeColor = Color.Gray });
            }
            else
            {
                var discoveredIds = _discoveredAccounts.TryGetValue(loginId, out var disc)
                    ? disc.Select(d => d.AccountId).ToHashSet() : null;
                var loginRefreshed = _refreshedLogins.Contains(loginId);

                foreach (var account in syncedAccounts)
                {
                    var isMissing = loginRefreshed && discoveredIds != null
                        && !discoveredIds.Contains(account.AccountId);

                    var statusText = isMissing ? "Missing on server" : account.Status switch
                    {
                        AccountStatus.Idle when account.PendingCount > 0 => $"{account.PendingCount} pending",
                        AccountStatus.Idle => "Up to date",
                        AccountStatus.Syncing => "Syncing",
                        AccountStatus.Error => "Error",
                        AccountStatus.Disconnected => "Offline",
                        _ => "Unknown",
                    };

                    var node = new TreeNode($"{account.DisplayName} \u2014 {statusText}")
                    {
                        Tag = new AccountNode(loginId, account.AccountId, account.DisplayName,
                            IsSynced: true, IsMissing: isMissing, SyncInfo: account),
                        ForeColor = isMissing ? Color.FromArgb(200, 120, 0) : default,
                    };
                    loginTreeNode.Nodes.Add(node);
                }

                if (disc != null)
                {
                    foreach (var d in disc.Where(d => !syncedIds.Contains(d.AccountId)))
                    {
                        var node = new TreeNode($"{d.Name} \u2014 Not synced")
                        {
                            Tag = new AccountNode(loginId, d.AccountId, d.Name,
                                IsSynced: false, IsMissing: false, SyncInfo: null),
                            ForeColor = Color.Gray,
                        };
                        loginTreeNode.Nodes.Add(node);
                    }
                }
            }

            _treeView.Nodes.Add(loginTreeNode);
        }

        _treeView.ExpandAll();

        // Restore selection
        if (selectedLoginId != null || selectedAccountId != null)
        {
            foreach (TreeNode loginTn in _treeView.Nodes)
            {
                if (loginTn.Tag is LoginNode restoredLn && restoredLn.LoginId == selectedLoginId)
                {
                    _treeView.SelectedNode = loginTn;
                    break;
                }
                foreach (TreeNode accountTn in loginTn.Nodes)
                {
                    if (accountTn.Tag is AccountNode restoredAn && restoredAn.AccountId == selectedAccountId)
                    {
                        _treeView.SelectedNode = accountTn;
                        break;
                    }
                }
            }
        }

        _treeView.EndUpdate();
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        _loginPanel.Visible = false;
        _syncedAccountPanel.Visible = false;
        _availableAccountPanel.Visible = false;
        _noSelectionLabel.Visible = false;

        var node = _treeView.SelectedNode;
        if (node == null)
        {
            _noSelectionLabel.Visible = true;
            return;
        }

        if (node.Tag is LoginNode loginNode)
        {
            _loginPanel.Visible = true;
            _loginIdLabel.Text = loginNode.LoginId;
            // Derive a default session URL from the login ID host
            var atIdx = loginNode.LoginId.LastIndexOf('@');
            var host = atIdx >= 0 ? loginNode.LoginId.Substring(atIdx + 1) : "";
            _sessionUrlBox.Text = !string.IsNullOrEmpty(host)
                ? $"https://{host}/jmap/session"
                : "";
            _tokenBox.Text = "";
        }
        else if (node.Tag is AccountNode accountNode)
        {
            if (accountNode.IsSynced && accountNode.SyncInfo != null)
            {
                _syncedAccountPanel.Visible = true;
                var info = accountNode.SyncInfo;
                _syncedAccountName.Text = info.DisplayName;

                if (accountNode.IsMissing)
                {
                    _accountStatusLabel.Text = "This account is no longer available on the server.";
                    _accountStatusLabel.ForeColor = Color.FromArgb(200, 120, 0);
                    _refreshButton.Visible = false;
                    _cleanButton.Visible = false;
                }
                else
                {
                    _accountStatusLabel.Text = info.Status switch
                    {
                        AccountStatus.Idle when info.PendingCount > 0 => $"Status: {info.PendingCount} pending",
                        AccountStatus.Idle => "Status: Up to date",
                        AccountStatus.Syncing => "Status: Syncing",
                        AccountStatus.Error => $"Status: Error \u2014 {info.StatusDetail}",
                        AccountStatus.Disconnected => "Status: Offline",
                        _ => "Status: Unknown",
                    };
                    _accountStatusLabel.ForeColor = default;
                    _refreshButton.Visible = true;
                    _cleanButton.Visible = true;
                }
                _syncFolderLabel.Text = $"Folder: {info.SyncRootPath}";
            }
            else
            {
                _availableAccountPanel.Visible = true;
                _availableAccountLabel.Text = $"{accountNode.Name} is not currently synced.";
            }
        }
        else
        {
            _noSelectionLabel.Visible = true;
        }
    }

    private void OnAddLoginClicked(object? sender, EventArgs e)
    {
        using var addForm = new AddAccountForm(_serviceClient);
        if (addForm.ShowDialog(this) == DialogResult.OK)
            _ = RefreshAllLoginAccountsAsync();
    }

    private async void OnUpdateCredentialsClicked(object? sender, EventArgs e)
    {
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

        _updateCredentialsButton.Enabled = false;
        try
        {
            var result = await _serviceClient.UpdateLoginAsync(loginNode.LoginId, sessionUrl, token);
            if (!result.Success)
                MessageBox.Show($"Failed to update credentials: {result.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
            {
                _tokenBox.Text = "";
                _ = RefreshAllLoginAccountsAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to update credentials: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _updateCredentialsButton.Enabled = true;
        }
    }

    private async void OnRemoveLoginClicked(object? sender, EventArgs e)
    {
        if (_treeView.SelectedNode?.Tag is not LoginNode loginNode)
            return;

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
            return;

        _removeLoginButton.Enabled = false;
        try
        {
            var cmdResult = await _serviceClient.RemoveLoginAsync(loginNode.LoginId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to remove login: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
            {
                _discoveredAccounts.Remove(loginNode.LoginId);
                _loginAccountResults.Remove(loginNode.LoginId);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to remove login: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _removeLoginButton.Enabled = true;
        }
    }

    private async void OnDetachClicked(object? sender, EventArgs e)
    {
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true, SyncInfo: not null } accountNode)
            return;

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
            return;

        SetSyncedButtonsEnabled(false);
        try
        {
            var cmdResult = await _serviceClient.DetachAccountAsync(accountNode.AccountId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to detach account: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to detach account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetSyncedButtonsEnabled(true);
        }
    }

    private async void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true, SyncInfo: not null } accountNode)
            return;

        var info = accountNode.SyncInfo;
        var result = MessageBox.Show(
            $"Remove {info.DisplayName}?\n\n" +
            "This will stop syncing, unregister the sync root, and DELETE ALL local files in:\n" +
            $"{info.SyncRootPath}",
            "Remove Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        SetSyncedButtonsEnabled(false);
        try
        {
            var cmdResult = await _serviceClient.CleanUpAccountAsync(accountNode.AccountId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to remove account: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to remove account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetSyncedButtonsEnabled(true);
        }
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true } accountNode)
            return;

        var result = MessageBox.Show(
            $"Force refresh {accountNode.Name}?\n\n" +
            "This will delete the local cache and re-sync all files from the server.",
            "Refresh Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        SetSyncedButtonsEnabled(false);
        try
        {
            var cmdResult = await _serviceClient.RefreshAccountAsync(accountNode.AccountId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to refresh account: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to refresh account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetSyncedButtonsEnabled(true);
        }
    }

    private async void OnCleanClicked(object? sender, EventArgs e)
    {
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: true, SyncInfo: not null } accountNode)
            return;

        var info = accountNode.SyncInfo;
        var result = MessageBox.Show(
            $"Clean and rebuild {info.DisplayName}?\n\n" +
            "This will DELETE ALL local files and recreate everything from scratch.\n" +
            $"Folder: {info.SyncRootPath}",
            "Clean Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        SetSyncedButtonsEnabled(false);
        try
        {
            var cmdResult = await _serviceClient.CleanAccountAsync(accountNode.AccountId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to clean account: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to clean account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetSyncedButtonsEnabled(true);
        }
    }

    private async void OnAddAccountClicked(object? sender, EventArgs e)
    {
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: false } accountNode)
            return;

        _addAccountButton.Enabled = false;
        try
        {
            var cmdResult = await _serviceClient.EnableAccountAsync(accountNode.LoginId, accountNode.AccountId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to add account: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to add account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _addAccountButton.Enabled = true;
        }
    }

    private void SetSyncedButtonsEnabled(bool enabled)
    {
        _detachButton.Enabled = enabled;
        _removeButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
        _cleanButton.Enabled = enabled;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        AutoSizeToContent();
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

        // Re-center after resize
        if (StartPosition == FormStartPosition.CenterScreen)
        {
            Location = new Point(
                screen.X + (screen.Width - finalWidth) / 2,
                screen.Y + (screen.Height - finalHeight) / 2);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosing(e);
    }
}
