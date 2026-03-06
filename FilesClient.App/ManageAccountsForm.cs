using System.Drawing;
using FilesClient.Ipc;
using FilesClient.Jmap.Auth;

namespace FilesClient.App;

sealed class ManageAccountsForm : Form
{
    private readonly ServiceClient _serviceClient;
    private readonly CancellationTokenSource _appCts;
    private readonly TreeView _treeView;
    private readonly Panel _detailPanel;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _statusDebounceTimer;
    private readonly System.Windows.Forms.Timer _accountsDebounceTimer;
    private readonly System.Windows.Forms.Timer _outboxTimer;

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
    private readonly ListView _activityListView;

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

    // Track what's currently displayed in the detail panel to avoid resetting it
    private string? _displayedLoginId;
    private string? _displayedAccountId;
    private bool _suppressSelectionChanged;
    private bool _operationInProgress;
    private bool _outboxDirty = true;
    private bool _hasActiveUploads;
    private readonly Button _startServiceButton;
    private readonly Button _stopServiceButton;
    private readonly Button _restartServiceButton;

    // Discovered accounts per login (populated async on form open)
    private readonly Dictionary<string, List<DiscoveredAccount>> _discoveredAccounts = new();
    private readonly Dictionary<string, LoginAccountsResultEvent> _loginAccountResults = new();
    private readonly HashSet<string> _refreshedLogins = new();

    public ManageAccountsForm(ServiceClient serviceClient, CancellationTokenSource appCts)
    {
        _serviceClient = serviceClient;
        _appCts = appCts;

        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "Fastmail Files";
        MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
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
        _syncedAccountPanel = new Panel { Dock = DockStyle.Fill, Visible = false };

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

        _cleanButton = new Button { Text = "Clean", AutoSize = true, Height = 30, Margin = new Padding(0, 0, 0, 6) };
        _cleanButton.Click += OnCleanClicked;

        syncedButtonFlow.Controls.AddRange([_openFolderButton, _detachButton, _removeButton, _refreshButton, _cleanButton]);
        syncedTopLayout.Controls.Add(syncedButtonFlow);

        _syncedAccountPanel.Controls.Add(syncedTopLayout);

        // Activity list view — created here, added to _detailPanel later (always visible)
        _activityListView = new ListView
        {
            Dock = DockStyle.Bottom,
            Height = 160,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Clickable,
            ShowItemToolTips = true,
            OwnerDraw = true,
        };
        var em = Font.Height;
        _activityListView.Columns.Add("Account", 9 * em);
        _activityListView.Columns.Add("Name", 11 * em);
        _activityListView.Columns.Add("Action", 6 * em);
        _activityListView.Columns.Add("Status", 6 * em);
        _activityListView.Columns.Add("Updated", 7 * em);

        _activityListView.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _activityListView.DrawItem += (_, _) => { };
        _activityListView.DrawSubItem += OnDrawActivitySubItem;

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

        _detailPanel.Controls.AddRange([_loginPanel, _syncedAccountPanel, _availableAccountPanel, _noSelectionLabel, _activityListView]);

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

        _startServiceButton = new Button
        {
            Text = "Start Service",
            AutoSize = true,
            Height = 30,
            Visible = !_serviceClient.IsConnected,
        };
        _startServiceButton.Location = new Point(addLoginButton.Right + 8, 9);
        _startServiceButton.Click += OnStartServiceClicked;

        _stopServiceButton = new Button
        {
            Text = "Stop Service",
            AutoSize = true,
            Height = 30,
            Visible = _serviceClient.IsConnected,
        };
        _stopServiceButton.Location = new Point(addLoginButton.Right + 8, 9);
        _stopServiceButton.Click += OnStopServiceClicked;

        _restartServiceButton = new Button
        {
            Text = "Restart Service",
            AutoSize = true,
            Height = 30,
            Visible = _serviceClient.IsConnected,
        };
        _restartServiceButton.Location = new Point(_stopServiceButton.Right + 4, 9);
        _restartServiceButton.Click += OnRestartServiceClicked;

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

        bottomPanel.Controls.AddRange([addLoginButton, _startServiceButton, _stopServiceButton, _restartServiceButton, closeButton, exitButton]);

        // --- Assemble ---
        Controls.Add(_detailPanel);
        Controls.Add(splitter);
        Controls.Add(_treeView);
        Controls.Add(bottomPanel);

        // Debounce timers — coalesce rapid status/account changes so brief
        // SSE hiccups (Disconnected→Idle in <1s) don't cause visible flicker.
        _statusDebounceTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusDebounceTimer.Tick += (_, _) =>
        {
            _statusDebounceTimer.Stop();
            UpdateStatusText();
        };

        _accountsDebounceTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _accountsDebounceTimer.Tick += (_, _) =>
        {
            _accountsDebounceTimer.Stop();
            RefreshTree();
        };

        _serviceClient.AccountsChanged += () =>
        {
            if (InvokeRequired)
                BeginInvoke(() => _accountsDebounceTimer.Start());
            else
                _accountsDebounceTimer.Start();
        };

        _serviceClient.StatusChanged += () =>
        {
            _outboxDirty = true;
            if (InvokeRequired)
                BeginInvoke(() => _statusDebounceTimer.Start());
            else
                _statusDebounceTimer.Start();
        };

        _serviceClient.ConnectionChanged += connected =>
        {
            if (InvokeRequired)
                BeginInvoke(() => UpdateServiceButtons());
            else
                UpdateServiceButtons();
        };

        // Safety-net timer
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        _refreshTimer.Tick += (_, _) => UpdateStatusText();
        _refreshTimer.Start();

        // Outbox polling timer for inline activity display
        _outboxTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _outboxTimer.Tick += OnOutboxTick;

        RefreshTree();
        _ = RefreshAllLoginAccountsAsync();
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _operationInProgress = true;
        var result = MessageBox.Show(
            "This will close the Fastmail Files tray icon.\n\n" +
            "Your files will continue to sync in the background.",
            "Fastmail Files",
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
            MessageBox.Show("Could not start the service.\nFilesClient.Service.exe was not found.",
                "Fastmail Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            MessageBox.Show("Could not stop the service.", "Fastmail Files",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Brief pause to let the pipe fully close before restarting
        await Task.Delay(500);

        var started = ServiceLauncher.TryStartService();
        if (!started)
        {
            UpdateServiceButtons();
            MessageBox.Show("Service stopped but could not restart.\nFilesClient.Service.exe was not found.",
                "Fastmail Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private async void OnOutboxTick(object? sender, EventArgs e)
    {
        if (!_outboxDirty && !_hasActiveUploads)
            return;

        _outboxDirty = false;

        // Poll all synced accounts concurrently
        var syncedAccounts = _serviceClient.Accounts
            .Where(a => a.Status != AccountStatus.Idle || a.PendingCount > 0 || _hasActiveUploads)
            .ToList();

        // If nothing looks active, still poll all synced accounts (they may have queued items)
        if (syncedAccounts.Count == 0)
            syncedAccounts = _serviceClient.Accounts.ToList();

        if (syncedAccounts.Count == 0)
        {
            if (_activityListView.Items.Count > 0)
                RefreshActivityList(new());
            return;
        }

        try
        {
            var tasks = syncedAccounts.Select(a => _serviceClient.GetOutboxAsync(a.AccountId)).ToList();
            var snapshots = await Task.WhenAll(tasks);

            var allEntries = new List<(string DisplayName, OutboxEntry Entry)>();
            for (int i = 0; i < snapshots.Length; i++)
            {
                var displayName = syncedAccounts[i].DisplayName;
                foreach (var entry in snapshots[i].Entries)
                    allEntries.Add((displayName, entry));
            }

            RefreshActivityList(allEntries);
        }
        catch
        {
            // IPC not available — leave list as-is
        }
    }

    private void RefreshActivityList(List<(string DisplayName, OutboxEntry Entry)> entries)
    {
        var now = DateTime.UtcNow;

        _activityListView.BeginUpdate();
        _activityListView.Items.Clear();

        var sorted = entries.OrderByDescending(e =>
            e.Entry.IsProcessing ? (e.Entry.UploadProgress ?? -1) : -2)
            .ThenBy(e => e.Entry.CreatedAt);

        foreach (var (displayName, entry) in sorted)
        {
            var name = entry.LocalPath != null
                ? Path.GetFileName(entry.LocalPath)
                : entry.NodeId ?? "(unknown)";

            var action = DeriveAction(entry);
            string status;
            int? progress = null;
            if (entry.IsProcessing)
            {
                progress = entry.UploadProgress;
                status = progress.HasValue ? "" : "Syncing...";
            }
            else
            {
                status = DeriveStatus(entry, now);
            }

            var item = new ListViewItem(displayName);
            item.SubItems.Add(name);
            item.SubItems.Add(action);
            var statusSubItem = item.SubItems.Add(status);
            if (progress.HasValue)
                statusSubItem.Tag = progress.Value;
            item.SubItems.Add(FormatRelativeTime(entry.UpdatedAt, now));

            var tooltip = entry.LocalPath ?? entry.NodeId ?? "";
            if (progress.HasValue)
                tooltip += $"\nUploading {progress.Value}%";
            if (entry.LastError != null)
                tooltip += $"\nError: {entry.LastError}";
            item.ToolTipText = tooltip;

            if (entry.IsProcessing)
                item.ForeColor = Color.DodgerBlue;
            else if (entry.LastError != null)
                item.ForeColor = Color.Red;

            _activityListView.Items.Add(item);
        }

        _activityListView.EndUpdate();
        _hasActiveUploads = entries.Any(e => e.Entry.IsProcessing);
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
        if (entry.AttemptCount > 0 && entry.LastError != null)
            return $"Error ({entry.AttemptCount})";
        if (entry.NextRetryAfter.HasValue && entry.NextRetryAfter.Value > now)
            return "Waiting";
        if (entry.AttemptCount > 0)
            return "Retrying";
        return "Pending";
    }

    private static string FormatRelativeTime(DateTime utcTime, DateTime now)
    {
        var elapsed = now - utcTime;
        if (elapsed.TotalSeconds < 10)
            return "just now";
        if (elapsed.TotalSeconds < 60)
            return $"{(int)elapsed.TotalSeconds} sec ago";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes} min ago";
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
            catch (Exception ex) { return (loginId, (LoginAccountsResultEvent?)null, (Exception?)ex); }
        }).ToList();

        foreach (var (loginId, result, _) in await Task.WhenAll(cachedTasks))
        {
            if (result == null) continue;
            _loginAccountResults[loginId] = result;
            if (result.Accounts != null)
                _discoveredAccounts[loginId] = result.Accounts;
        }
        if (InvokeRequired) BeginInvoke(RefreshTree); else RefreshTree();

        // Phase 2: fresh from server (slow, all logins in parallel)
        var refreshTasks = loginIds.Select(async loginId =>
        {
            try
            {
                var result = await _serviceClient.RefreshLoginAccountsAsync(loginId);
                return (loginId, result, (Exception?)null);
            }
            catch (Exception ex) { return (loginId, (LoginAccountsResultEvent?)null, (Exception?)ex); }
        }).ToList();

        foreach (var (loginId, result, ex) in await Task.WhenAll(refreshTasks))
        {
            if (result != null)
            {
                _loginAccountResults[loginId] = result;
                if (result.Accounts != null)
                    _discoveredAccounts[loginId] = result.Accounts;
            }
            else if (ex != null)
            {
                Console.Error.WriteLine($"Failed to refresh accounts for {loginId}: {ex.Message}");
            }
            _refreshedLogins.Add(loginId);
        }
        if (InvokeRequired) BeginInvoke(RefreshTree); else RefreshTree();
    }

    private void RefreshTree()
    {
        var accounts = _serviceClient.Accounts;
        var connectingIds = _serviceClient.ConnectingLoginIds;
        var failedLogins = _serviceClient.FailedLogins;
        var serviceConnected = _serviceClient.IsConnected;

        // Preserve selection
        string? selectedLoginId = null;
        string? selectedAccountId = null;
        if (_treeView.SelectedNode?.Tag is LoginNode ln)
            selectedLoginId = ln.LoginId;
        else if (_treeView.SelectedNode?.Tag is AccountNode an)
            selectedAccountId = an.AccountId;

        // Suppress AfterSelect during tree rebuild so Nodes.Clear() and
        // SelectedNode assignment don't trigger OnSelectionChanged()
        _suppressSelectionChanged = true;

        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();

        if (!serviceConnected)
        {
            _treeView.Nodes.Add(new TreeNode("Service not running")
                { ForeColor = Color.Gray });
            _treeView.EndUpdate();
            _suppressSelectionChanged = false;
            OnSelectionChanged();
            return;
        }

        // Group synced accounts by loginId
        var byLogin = accounts
            .Where(a => !string.IsNullOrEmpty(a.LoginId))
            .GroupBy(a => a.LoginId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allLoginIds = byLogin.Keys
            .Union(_discoveredAccounts.Keys)
            .Union(connectingIds)
            .Union(failedLogins.Select(f => f.LoginId))
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

            var failedInfo = failedLogins.FirstOrDefault(f => f.LoginId == loginId);

            if (connectingIds.Contains(loginId) && syncedAccounts.Count == 0)
            {
                loginTreeNode.Nodes.Add(new TreeNode("Connecting...") { ForeColor = Color.Gray });
            }
            else if (failedInfo != null && syncedAccounts.Count == 0)
            {
                loginTreeNode.Nodes.Add(new TreeNode($"Connection failed: {failedInfo.Error}")
                    { ForeColor = Color.Red });
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
        _suppressSelectionChanged = false;

        // Always refresh the detail panel — the same account may have changed
        // state (e.g. non-synced → synced) even if the selection ID is the same
        OnSelectionChanged();

        // If any login doesn't have discovered accounts yet, kick off discovery.
        // This handles new logins arriving via AccountsChanged after AddAccountForm.
        var undiscoveredLogins = allLoginIds
            .Where(id => !_discoveredAccounts.ContainsKey(id) && !connectingIds.Contains(id))
            .ToList();
        if (undiscoveredLogins.Count > 0)
            _ = DiscoverAccountsForLoginsAsync(undiscoveredLogins);
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
                _loginAccountResults[loginId] = result;
                if (result.Accounts != null)
                {
                    _discoveredAccounts[loginId] = result.Accounts;
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
                _loginAccountResults[loginId] = result;
                if (result.Accounts != null)
                {
                    _discoveredAccounts[loginId] = result.Accounts;
                    changed = true;
                }
                _refreshedLogins.Add(loginId);
            }
            catch
            {
                _refreshedLogins.Add(loginId);
            }
        }

        if (changed)
        {
            if (InvokeRequired) BeginInvoke(RefreshTree); else RefreshTree();
        }
    }

    /// <summary>
    /// Lightweight update: refreshes only the status text on existing tree nodes
    /// and the detail panel status label, without rebuilding the tree or resetting
    /// text boxes. This preserves focus and user input.
    /// </summary>
    private void UpdateStatusText()
    {
        var accounts = _serviceClient.Accounts;
        var accountLookup = accounts.ToDictionary(a => a.AccountId);

        foreach (TreeNode loginTn in _treeView.Nodes)
        {
            foreach (TreeNode childTn in loginTn.Nodes)
            {
                if (childTn.Tag is not AccountNode an || !an.IsSynced)
                    continue;

                if (!accountLookup.TryGetValue(an.AccountId, out var info))
                    continue;

                var statusText = an.IsMissing ? "Missing on server" : info.Status switch
                {
                    AccountStatus.Idle when info.PendingCount > 0 => $"{info.PendingCount} pending",
                    AccountStatus.Idle => "Up to date",
                    AccountStatus.Syncing => "Syncing",
                    AccountStatus.Error => "Error",
                    AccountStatus.Disconnected => "Offline",
                    _ => "Unknown",
                };

                var newText = $"{an.Name} \u2014 {statusText}";
                if (childTn.Text != newText)
                    childTn.Text = newText;

                // Update the tag with fresh SyncInfo
                childTn.Tag = an with { SyncInfo = info };
            }
        }

        // If the selected node is a synced account, update the detail panel status
        if (_treeView.SelectedNode?.Tag is AccountNode selAn
            && selAn.IsSynced && selAn.SyncInfo != null && !selAn.IsMissing)
        {
            var selInfo = selAn.SyncInfo;
            _accountStatusLabel.Text = selInfo.Status switch
            {
                AccountStatus.Idle when selInfo.PendingCount > 0 => $"Status: {selInfo.PendingCount} pending",
                AccountStatus.Idle => "Status: Up to date",
                AccountStatus.Syncing => "Status: Syncing",
                AccountStatus.Error => $"Status: Error \u2014 {selInfo.StatusDetail}",
                AccountStatus.Disconnected => "Status: Offline",
                _ => "Status: Unknown",
            };
        }
    }

    private void OnSelectionChanged()
    {
        var node = _treeView.SelectedNode;

        // Determine what the new selection refers to
        string? newLoginId = null;
        string? newAccountId = null;
        if (node?.Tag is LoginNode ln)
            newLoginId = ln.LoginId;
        else if (node?.Tag is AccountNode an)
            newAccountId = an.AccountId;

        // If the same synced account is still selected AND we're already showing
        // the synced panel, just update labels without clearing the activity list.
        if (newAccountId != null && newAccountId == _displayedAccountId
            && _syncedAccountPanel.Visible
            && node?.Tag is AccountNode sameAn && sameAn.IsSynced && sameAn.SyncInfo != null)
        {
            UpdateSyncedAccountLabels(sameAn);
            return;
        }

        _loginPanel.Visible = false;
        _syncedAccountPanel.Visible = false;
        _availableAccountPanel.Visible = false;
        _noSelectionLabel.Visible = false;

        // Update tracking fields
        _displayedLoginId = null;
        _displayedAccountId = null;

        // (outbox timer runs globally, not tied to selection)

        if (node == null)
        {
            _noSelectionLabel.Visible = true;
            return;
        }

        if (node.Tag is LoginNode loginNode)
        {
            _displayedLoginId = loginNode.LoginId;
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
            _displayedAccountId = accountNode.AccountId;
            if (accountNode.IsSynced && accountNode.SyncInfo != null)
            {
                _syncedAccountPanel.Visible = true;
                UpdateSyncedAccountLabels(accountNode);
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
            _openFolderButton.Visible = true;
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
                _ = RefreshAllLoginAccountsAsync();
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

            var cred = await flow.SignInAsync(progress);

            _reauthStatusLabel.Text = "Updating credentials...";
            var result = await _serviceClient.UpdateLoginAsync(
                loginNode.LoginId, cred.SessionUrl, cred.AccessToken,
                cred.RefreshToken, cred.TokenEndpoint, cred.ClientId,
                cred.ExpiresAt.ToUnixTimeSeconds());

            if (!result.Success)
            {
                _reauthStatusLabel.Text = $"Failed: {result.Error}";
                _reauthStatusLabel.ForeColor = Color.Red;
            }
            else
            {
                _reauthStatusLabel.Text = "Re-authenticated successfully.";
                _reauthStatusLabel.ForeColor = Color.Green;
                _tokenBox.Text = "";
                _ = RefreshAllLoginAccountsAsync();
            }
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
            var cmdResult = await _serviceClient.RemoveLoginAsync(loginNode.LoginId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to remove login: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
            {
                _discoveredAccounts.Remove(loginNode.LoginId);
                _loginAccountResults.Remove(loginNode.LoginId);
                _refreshedLogins.Remove(loginNode.LoginId);
            }

            RefreshTree();
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
            SetAllButtonsEnabled(true);
        }
    }

    private async void OnAddAccountClicked(object? sender, EventArgs e)
    {
        if (_operationInProgress) return;
        if (_treeView.SelectedNode?.Tag is not AccountNode { IsSynced: false } accountNode)
            return;

        SetAllButtonsEnabled(false);
        try
        {
            var cmdResult = await _serviceClient.EnableAccountAsync(accountNode.LoginId, accountNode.AccountId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to add account: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                await RefreshAllLoginAccountsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to add account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private void SetAllButtonsEnabled(bool enabled)
    {
        _operationInProgress = !enabled;
        _openFolderButton.Enabled = enabled;
        _detachButton.Enabled = enabled;
        _removeButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
        _cleanButton.Enabled = enabled;
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
            _outboxDirty = true;
            _outboxTimer.Start();
            EnsureOnScreen();
            // Briefly set TopMost to ensure the panel appears above other windows
            TopMost = true;
            BeginInvoke(() => TopMost = false);
        }
        else
        {
            _outboxTimer.Stop();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Dropbox-style: hide when user clicks elsewhere.
        // Don't hide during operations (OAuth, re-auth), modal dialogs, or
        // when a child form (AddAccountForm, MessageBox) owns focus.
        BeginInvoke(() =>
        {
            if (_operationInProgress)
                return;
            if (ContainsFocus || OwnedForms.Length > 0)
                return;
            Hide();
        });
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
        _outboxTimer.Stop();
        _outboxTimer.Dispose();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _statusDebounceTimer.Stop();
        _statusDebounceTimer.Dispose();
        _accountsDebounceTimer.Stop();
        _accountsDebounceTimer.Dispose();
        base.OnFormClosing(e);
    }
}
