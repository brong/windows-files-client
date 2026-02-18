using System.Drawing;

namespace FilesClient.App;

sealed class AddAccountForm : Form
{
    private const string DefaultSessionUrl = "https://api.fastmail.com/jmap/session";

    private readonly LoginManager _loginManager;
    private readonly string? _iconPath;
    private readonly TextBox _tokenBox;
    private readonly TextBox _sessionUrlBox;
    private readonly Button _connectButton;
    private readonly Label _statusLabel;

    public AddAccountForm(LoginManager loginManager, string? iconPath)
    {
        _loginManager = loginManager;
        _iconPath = iconPath;

        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Add Account";
        Size = new Size(470, 260);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var tokenLabel = new Label
        {
            Text = "App password (token):",
            Location = new Point(12, 15),
            AutoSize = true,
        };
        _tokenBox = new TextBox
        {
            Location = new Point(12, 38),
            Width = 430,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            UseSystemPasswordChar = true,
        };

        var urlLabel = new Label
        {
            Text = "Session URL (optional):",
            Location = new Point(12, 68),
            AutoSize = true,
        };
        _sessionUrlBox = new TextBox
        {
            Location = new Point(12, 91),
            Width = 430,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Text = DefaultSessionUrl,
        };

        _connectButton = new Button
        {
            Text = "Connect",
            AutoSize = true,
            Height = 28,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        _connectButton.Location = new Point(370, 135);
        _connectButton.Click += OnConnectClicked;

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Height = 28,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
        };
        cancelButton.Location = new Point(280, 135);

        _statusLabel = new Label
        {
            Location = new Point(12, 140),
            Width = 250,
            AutoSize = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            ForeColor = Color.Gray,
        };

        Controls.AddRange([tokenLabel, _tokenBox, urlLabel, _sessionUrlBox,
            _connectButton, cancelButton, _statusLabel]);

        AcceptButton = _connectButton;
        CancelButton = cancelButton;
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        var token = _tokenBox.Text.Trim();
        var sessionUrl = _sessionUrlBox.Text.Trim();

        if (string.IsNullOrEmpty(token))
        {
            MessageBox.Show("Please enter an app password.", "Missing Token",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _tokenBox.Focus();
            return;
        }

        if (string.IsNullOrEmpty(sessionUrl))
            sessionUrl = DefaultSessionUrl;

        _connectButton.Enabled = false;
        _statusLabel.Text = "Connecting...";
        _statusLabel.ForeColor = Color.DodgerBlue;

        try
        {
            // Phase 1: Discover accounts
            var accounts = await _loginManager.DiscoverAccountsAsync(sessionUrl, token);

            if (accounts.Count == 0)
            {
                _statusLabel.Text = "No FileNode accounts found";
                _statusLabel.ForeColor = Color.Red;
                _connectButton.Enabled = true;
                return;
            }

            // Phase 2: If multiple accounts, show account picker
            HashSet<string>? enabledAccountIds = null;
            if (accounts.Count > 1)
            {
                using var selectForm = new SelectAccountsForm(accounts, null);
                if (selectForm.ShowDialog(this) != DialogResult.OK || selectForm.SelectedAccountIds == null)
                {
                    _connectButton.Enabled = true;
                    _statusLabel.Text = "";
                    return;
                }
                enabledAccountIds = selectForm.SelectedAccountIds;

                if (enabledAccountIds.Count == 0)
                {
                    _statusLabel.Text = "No accounts selected";
                    _statusLabel.ForeColor = Color.Red;
                    _connectButton.Enabled = true;
                    return;
                }
            }

            // Phase 3: Connect and start selected accounts
            _statusLabel.Text = "Starting sync...";
            var loginId = await _loginManager.AddLoginAsync(sessionUrl, token,
                persist: true, iconPath: _iconPath, enabledAccountIds: enabledAccountIds);

            _statusLabel.Text = $"Connected: {loginId}";
            _statusLabel.ForeColor = Color.Green;

            // Brief pause so the user sees success, then close
            await Task.Delay(500);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            _connectButton.Enabled = true;
        }
    }
}
