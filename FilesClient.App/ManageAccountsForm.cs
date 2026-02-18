using System.Drawing;
using FilesClient.Windows;

namespace FilesClient.App;

sealed class ManageAccountsForm : Form
{
    private readonly LoginManager _loginManager;
    private readonly string? _iconPath;
    private readonly ListView _listView;
    private readonly Button _addButton;
    private readonly Button _cleanUpButton;
    private readonly Button _removeLoginButton;
    private readonly Button _configureButton;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public ManageAccountsForm(LoginManager loginManager, string? iconPath)
    {
        _loginManager = loginManager;
        _iconPath = iconPath;

        Text = "Fastmail Files - Manage Accounts";
        Size = new Size(550, 350);
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
        _listView.Columns.Add("Sync Folder", 180);
        _listView.Columns.Add("Status", 80);
        _listView.SelectedIndexChanged += (_, _) => UpdateButtonState();

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Padding = new Padding(8),
        };

        _addButton = new Button
        {
            Text = "Add...",
            Width = 65,
            Height = 28,
            Location = new Point(8, 8),
        };
        _addButton.Click += OnAddClicked;

        _cleanUpButton = new Button
        {
            Text = "Clean up",
            Width = 75,
            Height = 28,
            Location = new Point(80, 8),
            Enabled = false,
        };
        _cleanUpButton.Click += OnCleanUpClicked;

        _removeLoginButton = new Button
        {
            Text = "Remove login",
            Width = 100,
            Height = 28,
            Location = new Point(162, 8),
            Enabled = false,
        };
        _removeLoginButton.Click += OnRemoveLoginClicked;

        _configureButton = new Button
        {
            Text = "Configure...",
            Width = 90,
            Height = 28,
            Location = new Point(269, 8),
            Enabled = false,
        };
        _configureButton.Click += OnConfigureClicked;

        var closeButton = new Button
        {
            Text = "Close",
            Width = 75,
            Height = 28,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        closeButton.Location = new Point(bottomPanel.Width - closeButton.Width - 12, 8);
        closeButton.Click += (_, _) => Close();

        bottomPanel.Controls.AddRange([_addButton, _cleanUpButton, _removeLoginButton, _configureButton, closeButton]);
        Controls.Add(bottomPanel);
        Controls.Add(_listView);

        _loginManager.AccountsChanged += () =>
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
        var supervisors = _loginManager.Supervisors;

        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (var s in supervisors)
        {
            var statusText = s.Status switch
            {
                SyncStatus.Idle when s.PendingCount > 0 => $"{s.PendingCount} pending",
                SyncStatus.Idle => "Up to date",
                SyncStatus.Syncing => "Syncing",
                SyncStatus.Error => "Error",
                SyncStatus.Disconnected => "Offline",
                _ => "Unknown",
            };

            var item = new ListViewItem(s.DisplayName);
            item.SubItems.Add(s.SyncRootPath);
            item.SubItems.Add(statusText);
            item.Tag = s;
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
        using var addForm = new AddAccountForm(_loginManager, _iconPath);
        addForm.ShowDialog(this);
    }

    private async void OnCleanUpClicked(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0)
            return;

        var supervisor = _listView.SelectedItems[0].Tag as AccountSupervisor;
        if (supervisor == null)
            return;

        var result = MessageBox.Show(
            $"Clean up {supervisor.DisplayName}?\n\n" +
            "This will stop syncing, unregister the sync root, and delete all local files in:\n" +
            $"{supervisor.SyncRootPath}",
            "Clean Up Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        try
        {
            _cleanUpButton.Enabled = false;
            _removeLoginButton.Enabled = false;
            await _loginManager.CleanUpAccountAsync(supervisor.AccountId);
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

        var supervisor = _listView.SelectedItems[0].Tag as AccountSupervisor;
        if (supervisor == null)
            return;

        var loginId = _loginManager.GetLoginIdForAccount(supervisor.AccountId);
        if (loginId == null)
        {
            MessageBox.Show("Could not find login for this account.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var activeIds = _loginManager.GetActiveAccountIds(loginId);
        var accountWord = activeIds.Count == 1 ? "account" : $"{activeIds.Count} accounts";

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
            await _loginManager.RemoveLoginAsync(loginId);
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

        var supervisor = _listView.SelectedItems[0].Tag as AccountSupervisor;
        if (supervisor == null)
            return;

        var loginId = _loginManager.GetLoginIdForAccount(supervisor.AccountId);
        if (loginId == null)
        {
            MessageBox.Show("Could not find login for this account.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var accounts = _loginManager.GetLoginAccounts(loginId);
        if (accounts == null || accounts.Count <= 1)
        {
            MessageBox.Show("This login has only one account — nothing to configure.", "Info",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var currentActive = _loginManager.GetActiveAccountIds(loginId);

        using var selectForm = new SelectAccountsForm(accounts, currentActive);
        if (selectForm.ShowDialog(this) != DialogResult.OK || selectForm.SelectedAccountIds == null)
            return;

        if (selectForm.SelectedAccountIds.Count == 0)
        {
            MessageBox.Show("At least one account must be selected.", "Warning",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _configureButton.Enabled = false;
            await _loginManager.ConfigureLoginAsync(loginId, selectForm.SelectedAccountIds, _iconPath);
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
