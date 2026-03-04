using System.Drawing;
using FilesClient.Jmap.Auth;

namespace FilesClient.App;

sealed class AddAccountForm : Form
{
    private const string DefaultSessionUrl = "https://api.fastmail.com/jmap/session";

    private readonly ServiceClient _serviceClient;

    // OAuth flow controls
    private readonly Button _signInButton;

    // Advanced (manual) flow controls
    private readonly GroupBox _advancedGroup;
    private readonly TextBox _tokenBox;
    private readonly TextBox _sessionUrlBox;
    private readonly Button _connectButton;

    // Shared
    private readonly TextBox _statusLabel;
    private readonly LinkLabel _advancedToggle;

    public AddAccountForm(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "Add Account";
        var em = Font.Height;
        Size = new Size(32 * em, 12 * em);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var pad = em;
        var inputWidth = ClientSize.Width - 2 * pad;

        // --- OAuth flow (primary) ---
        var y = em;

        _signInButton = new Button
        {
            Text = "Sign in with Fastmail",
            AutoSize = true,
            Height = (int)(em * 2.2),
            Location = new Point(pad, y),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        _signInButton.Click += OnSignInClicked;

        _advancedToggle = new LinkLabel
        {
            Text = "Advanced options...",
            AutoSize = true,
            Location = new Point(pad, y + (int)(em * 2.8)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        _advancedToggle.LinkClicked += OnAdvancedToggleClicked;

        y += (int)(em * 4.5);

        // --- Status label (shared, TextBox so text is selectable) ---
        _statusLabel = new TextBox
        {
            Location = new Point(pad, y),
            Width = inputWidth,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            ForeColor = Color.Gray,
            Multiline = true,
            WordWrap = true,
        };

        // --- Advanced group (collapsed by default) ---
        var advancedY = y + (int)(em * 1.5);
        _advancedGroup = new GroupBox
        {
            Text = "Manual Connection",
            Location = new Point(pad, advancedY),
            Size = new Size(inputWidth, (int)(em * 8.5)),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Visible = false,
        };

        var innerPad = em / 2;
        var innerY = (int)(em * 1.5);

        var urlLabel = new Label
        {
            Text = "Session URL:",
            Location = new Point(innerPad, innerY),
            AutoSize = true,
        };
        innerY += (int)(em * 1.3);

        _sessionUrlBox = new TextBox
        {
            Location = new Point(innerPad, innerY),
            Width = _advancedGroup.ClientSize.Width - 2 * innerPad,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Text = DefaultSessionUrl,
        };
        innerY += (int)(em * 2.2);

        var tokenLabel = new Label
        {
            Text = "App password (token):",
            Location = new Point(innerPad, innerY),
            AutoSize = true,
        };
        innerY += (int)(em * 1.3);

        _tokenBox = new TextBox
        {
            Location = new Point(innerPad, innerY),
            Width = _advancedGroup.ClientSize.Width - 2 * innerPad,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            UseSystemPasswordChar = true,
        };
        innerY += (int)(em * 2.2);

        _connectButton = new Button
        {
            Text = "Connect",
            AutoSize = true,
            Height = (int)(em * 1.8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _connectButton.Location = new Point(
            _advancedGroup.ClientSize.Width - innerPad - _connectButton.PreferredSize.Width, innerY);
        _connectButton.Click += OnConnectClicked;

        _advancedGroup.Controls.AddRange([urlLabel, _sessionUrlBox, tokenLabel, _tokenBox, _connectButton]);

        // --- Cancel button ---
        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Height = (int)(em * 1.8),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange([_signInButton, _advancedToggle,
            _statusLabel, _advancedGroup, cancelButton]);

        // Position cancel at bottom-right
        cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancelButton.Location = new Point(
            ClientSize.Width - pad - cancelButton.PreferredSize.Width,
            ClientSize.Height - pad - cancelButton.Height);

        AcceptButton = _signInButton;
        CancelButton = cancelButton;
    }

    private void OnAdvancedToggleClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        _advancedGroup.Visible = !_advancedGroup.Visible;
        _advancedToggle.Text = _advancedGroup.Visible
            ? "Hide advanced options"
            : "Advanced options...";

        // Resize form to fit
        var em = Font.Height;
        var extraHeight = _advancedGroup.Visible ? _advancedGroup.Height + em : 0;
        Size = new Size(Size.Width, (int)(12 * em) + extraHeight);
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        _signInButton.Enabled = false;
        _statusLabel.ForeColor = Color.DodgerBlue;

        try
        {
            using var flow = new OAuthLoginFlow();
            var progress = new Progress<string>(msg =>
            {
                if (InvokeRequired)
                    BeginInvoke(() => _statusLabel.Text = msg);
                else
                    _statusLabel.Text = msg;
            });

            var cred = await flow.SignInAsync(progress);

            // Discover accounts
            _statusLabel.Text = "Discovering accounts...";
            var discoverResult = await _serviceClient.DiscoverAccountsAsync(cred.SessionUrl, cred.AccessToken);

            if (!discoverResult.Success || discoverResult.Accounts == null)
            {
                _statusLabel.Text = discoverResult.Error ?? "Discovery failed";
                _statusLabel.ForeColor = Color.Red;
                _signInButton.Enabled = true;
                return;
            }

            if (discoverResult.Accounts.Count == 0)
            {
                _statusLabel.Text = "No FileNode accounts found";
                _statusLabel.ForeColor = Color.Red;
                _signInButton.Enabled = true;
                return;
            }

            // Account selection
            HashSet<string>? enabledAccountIds = null;
            if (discoverResult.Accounts.Count >= 1)
            {
                var accounts = discoverResult.Accounts
                    .Select(a => (a.AccountId, a.Name, a.IsPrimary)).ToList();

                using var selectForm = new SelectAccountsForm(accounts, null);
                if (selectForm.ShowDialog(this) != DialogResult.OK || selectForm.SelectedAccountIds == null)
                {
                    _signInButton.Enabled = true;
                    _statusLabel.Text = "";
                    return;
                }
                enabledAccountIds = selectForm.SelectedAccountIds;

                if (enabledAccountIds.Count == 0)
                {
                    _statusLabel.Text = "No accounts selected";
                    _statusLabel.ForeColor = Color.Red;
                    _signInButton.Enabled = true;
                    return;
                }
            }

            // Add login and close immediately
            var addResult = await _serviceClient.AddLoginAsync(
                cred.SessionUrl, cred.AccessToken, enabledAccountIds,
                cred.RefreshToken, cred.TokenEndpoint, cred.ClientId,
                cred.ExpiresAt.ToUnixTimeSeconds());

            if (!addResult.Success)
            {
                _statusLabel.Text = $"Error: {addResult.Error}";
                _statusLabel.ForeColor = Color.Red;
                _signInButton.Enabled = true;
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            _signInButton.Enabled = true;
        }
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

            // Phase 3: Add login via service (no OAuth fields — manual token)
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
