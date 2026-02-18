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

        Text = "Add Account";
        Size = new Size(450, 240);
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
            Location = new Point(12, 35),
            Width = 410,
            UseSystemPasswordChar = true,
        };

        var urlLabel = new Label
        {
            Text = "Session URL (optional):",
            Location = new Point(12, 65),
            AutoSize = true,
        };
        _sessionUrlBox = new TextBox
        {
            Location = new Point(12, 85),
            Width = 410,
            Text = DefaultSessionUrl,
        };

        _connectButton = new Button
        {
            Text = "Connect",
            Width = 90,
            Height = 28,
            Location = new Point(332, 120),
        };
        _connectButton.Click += OnConnectClicked;

        var cancelButton = new Button
        {
            Text = "Cancel",
            Width = 75,
            Height = 28,
            Location = new Point(245, 120),
            DialogResult = DialogResult.Cancel,
        };

        _statusLabel = new Label
        {
            Location = new Point(12, 125),
            Width = 220,
            AutoSize = false,
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
            var loginId = await _loginManager.AddLoginAsync(sessionUrl, token,
                persist: true, iconPath: _iconPath);

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
