using System.Drawing;

namespace FilesClient.App;

sealed class AddAccountForm : Form
{
    private const string DefaultSessionUrl = "https://api.fastmail.com/jmap/session";

    private readonly ServiceClient _serviceClient;
    private readonly TextBox _tokenBox;
    private readonly TextBox _sessionUrlBox;
    private readonly Button _connectButton;
    private readonly Label _statusLabel;

    public AddAccountForm(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "Add Account";
        var em = Font.Height;
        Size = new Size(32 * em, 17 * em);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var pad = em;
        var row1 = em;
        var row1Input = row1 + (int)(em * 1.4);
        var row2 = row1Input + (int)(em * 2.2);
        var row2Input = row2 + (int)(em * 1.4);
        var buttonY = row2Input + (int)(em * 2.5);
        var inputWidth = ClientSize.Width - 2 * pad;

        var urlLabel = new Label
        {
            Text = "Session URL (optional):",
            Location = new Point(pad, row1),
            AutoSize = true,
        };
        _sessionUrlBox = new TextBox
        {
            Location = new Point(pad, row1Input),
            Width = inputWidth,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Text = DefaultSessionUrl,
        };

        var tokenLabel = new Label
        {
            Text = "App password (token):",
            Location = new Point(pad, row2),
            AutoSize = true,
        };
        _tokenBox = new TextBox
        {
            Location = new Point(pad, row2Input),
            Width = inputWidth,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            UseSystemPasswordChar = true,
        };

        _connectButton = new Button
        {
            Text = "Connect",
            AutoSize = true,
            Height = (int)(em * 1.8),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        _connectButton.Location = new Point(ClientSize.Width - pad - _connectButton.PreferredSize.Width, buttonY);
        _connectButton.Click += OnConnectClicked;

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Height = (int)(em * 1.8),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
        };
        cancelButton.Location = new Point(_connectButton.Left - cancelButton.PreferredSize.Width - em / 2, buttonY);

        _statusLabel = new Label
        {
            Location = new Point(pad, buttonY),
            AutoSize = true,
            MaximumSize = new Size(cancelButton.Left - 2 * pad, 0),
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
            var discoverResult = await _serviceClient.DiscoverAccountsAsync(sessionUrl, token);

            if (!discoverResult.Success || discoverResult.Accounts == null)
            {
                _statusLabel.Text = discoverResult.Error ?? "Discovery failed";
                _statusLabel.ForeColor = Color.Red;
                _connectButton.Enabled = true;
                return;
            }

            if (discoverResult.Accounts.Count == 0)
            {
                _statusLabel.Text = "No FileNode accounts found";
                _statusLabel.ForeColor = Color.Red;
                _connectButton.Enabled = true;
                return;
            }

            // Phase 2: If multiple accounts, show account picker
            HashSet<string>? enabledAccountIds = null;
            if (discoverResult.Accounts.Count >= 1)
            {
                var accounts = discoverResult.Accounts
                    .Select(a => (a.AccountId, a.Name, a.IsPrimary)).ToList();

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

            // Phase 3: Add login via service
            _statusLabel.Text = "Starting sync...";
            var addResult = await _serviceClient.AddLoginAsync(sessionUrl, token, enabledAccountIds);

            if (!addResult.Success)
            {
                _statusLabel.Text = $"Error: {addResult.Error}";
                _statusLabel.ForeColor = Color.Red;
                _connectButton.Enabled = true;
                return;
            }

            _statusLabel.Text = $"Connected: {addResult.LoginId}";
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
