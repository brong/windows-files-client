using System.Drawing;
using FilesClient.Ipc;

namespace FilesClient.App;

sealed class ManageAccountsForm : Form
{
    private readonly ServiceClient _serviceClient;
    private readonly ListView _listView;
    private readonly Button _addButton;
    private readonly Button _cleanUpButton;
    private readonly Button _removeLoginButton;
    private readonly Button _configureButton;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public ManageAccountsForm(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Fastmail Files - Manage Accounts";
        Size = new Size(620, 350);
        MinimumSize = new Size(500, 250);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
        };
        _listView.Columns.Add("Account", 200);
        _listView.Columns.Add("Sync Folder", 220);
        _listView.Columns.Add("Status", 100);
        _listView.SelectedIndexChanged += (_, _) => UpdateButtonState();

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Padding = new Padding(8),
        };

        var flowPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(4, 6),
            WrapContents = false,
        };

        _addButton = new Button
        {
            Text = "Add...",
            AutoSize = true,
            Height = 28,
            Margin = new Padding(4, 0, 4, 0),
        };
        _addButton.Click += OnAddClicked;

        _cleanUpButton = new Button
        {
            Text = "Clean up",
            AutoSize = true,
            Height = 28,
            Margin = new Padding(4, 0, 4, 0),
            Enabled = false,
        };
        _cleanUpButton.Click += OnCleanUpClicked;

        _removeLoginButton = new Button
        {
            Text = "Remove login",
            AutoSize = true,
            Height = 28,
            Margin = new Padding(4, 0, 4, 0),
            Enabled = false,
        };
        _removeLoginButton.Click += OnRemoveLoginClicked;

        _configureButton = new Button
        {
            Text = "Configure...",
            AutoSize = true,
            Height = 28,
            Margin = new Padding(4, 0, 4, 0),
            Enabled = false,
        };
        _configureButton.Click += OnConfigureClicked;

        flowPanel.Controls.AddRange([_addButton, _cleanUpButton, _removeLoginButton, _configureButton]);

        var closeButton = new Button
        {
            Text = "Close",
            AutoSize = true,
            Height = 28,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        closeButton.Location = new Point(bottomPanel.Width - closeButton.Width - 12, 8);
        closeButton.Click += (_, _) => Close();

        bottomPanel.Controls.Add(flowPanel);
        bottomPanel.Controls.Add(closeButton);
        Controls.Add(bottomPanel);
        Controls.Add(_listView);

        _serviceClient.AccountsChanged += () =>
        {
            if (InvokeRequired)
                BeginInvoke(RefreshList);
            else
                RefreshList();
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();

        RefreshList();
    }

    private void RefreshList()
    {
        var accounts = _serviceClient.Accounts;

        // Preserve selection across refresh
        var selectedAccountId = (_listView.SelectedItems.Count > 0
            ? (_listView.SelectedItems[0].Tag as AccountInfo)?.AccountId
            : null);

        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (var a in accounts)
        {
            var statusText = a.Status switch
            {
                AccountStatus.Idle when a.PendingCount > 0 => $"{a.PendingCount} pending",
                AccountStatus.Idle => "Up to date",
                AccountStatus.Syncing => "Syncing",
                AccountStatus.Error => "Error",
                AccountStatus.Disconnected => "Offline",
                _ => "Unknown",
            };

            var item = new ListViewItem(a.DisplayName);
            item.SubItems.Add(a.SyncRootPath);
            item.SubItems.Add(statusText);
            item.Tag = a;
            if (a.AccountId == selectedAccountId)
                item.Selected = true;
            _listView.Items.Add(item);
        }

        _listView.EndUpdate();
        UpdateButtonState();
    }

    private void UpdateButtonState()
    {
        var hasSelection = _listView.SelectedItems.Count > 0;
        _cleanUpButton.Enabled = hasSelection;
        _removeLoginButton.Enabled = hasSelection;
        _configureButton.Enabled = hasSelection;
    }

    private void OnAddClicked(object? sender, EventArgs e)
    {
        using var addForm = new AddAccountForm(_serviceClient);
        addForm.ShowDialog(this);
    }

    private async void OnCleanUpClicked(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0)
            return;

        var account = _listView.SelectedItems[0].Tag as AccountInfo;
        if (account == null)
            return;

        var result = MessageBox.Show(
            $"Clean up {account.DisplayName}?\n\n" +
            "This will stop syncing, unregister the sync root, and delete all local files in:\n" +
            $"{account.SyncRootPath}",
            "Clean Up Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        try
        {
            _cleanUpButton.Enabled = false;
            _removeLoginButton.Enabled = false;
            var cmdResult = await _serviceClient.CleanUpAccountAsync(account.AccountId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to clean up account: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to clean up account: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnRemoveLoginClicked(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0)
            return;

        var account = _listView.SelectedItems[0].Tag as AccountInfo;
        if (account == null)
            return;

        var loginId = account.LoginId;
        if (string.IsNullOrEmpty(loginId))
        {
            MessageBox.Show("Could not find login for this account.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var accountsForLogin = _serviceClient.Accounts.Where(a => a.LoginId == loginId).ToList();
        var accountWord = accountsForLogin.Count == 1 ? "account" : $"{accountsForLogin.Count} accounts";

        var result = MessageBox.Show(
            $"Remove login {loginId}?\n\n" +
            $"This will stop syncing {accountWord}, unregister all sync roots, " +
            "delete local files, and forget the stored credential.",
            "Remove Login",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        try
        {
            _cleanUpButton.Enabled = false;
            _removeLoginButton.Enabled = false;
            var cmdResult = await _serviceClient.RemoveLoginAsync(loginId);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to remove login: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to remove login: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnConfigureClicked(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0)
            return;

        var account = _listView.SelectedItems[0].Tag as AccountInfo;
        if (account == null)
            return;

        var loginId = account.LoginId;
        if (string.IsNullOrEmpty(loginId))
        {
            MessageBox.Show("Could not find login for this account.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            var loginResult = await _serviceClient.GetLoginAccountsAsync(loginId);
            if (loginResult.Error != null || loginResult.Accounts == null)
            {
                MessageBox.Show(loginResult.Error ?? "Login not found", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (loginResult.Accounts.Count <= 1)
            {
                MessageBox.Show("This login has only one account \u2014 nothing to configure.", "Info",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var accounts = loginResult.Accounts
                .Select(a => (a.AccountId, a.Name, a.IsPrimary)).ToList();

            using var selectForm = new SelectAccountsForm(accounts, loginResult.ActiveAccountIds);
            if (selectForm.ShowDialog(this) != DialogResult.OK || selectForm.SelectedAccountIds == null)
                return;

            if (selectForm.SelectedAccountIds.Count == 0)
            {
                MessageBox.Show("At least one account must be selected.", "Warning",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _configureButton.Enabled = false;
            var cmdResult = await _serviceClient.ConfigureLoginAsync(loginId, selectForm.SelectedAccountIds);
            if (!cmdResult.Success)
                MessageBox.Show($"Failed to configure accounts: {cmdResult.Error}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to configure accounts: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosing(e);
    }
}
