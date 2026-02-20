using System.Drawing;

namespace FilesClient.App;

sealed class SelectAccountsForm : Form
{
    private readonly CheckedListBox _checkedListBox;
    private readonly List<(string AccountId, string Name, bool IsPrimary)> _accounts;

    public HashSet<string>? SelectedAccountIds { get; private set; }

    public SelectAccountsForm(
        List<(string AccountId, string Name, bool IsPrimary)> accounts,
        HashSet<string>? preselected)
    {
        _accounts = accounts;

        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "Select Accounts to Sync";
        var em = Font.Height;
        Size = new Size(28 * em, 21 * em);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var pad = em;
        var labelY = em;
        var listY = labelY + (int)(em * 1.6);
        var buttonHeight = (int)(em * 1.8);
        var buttonY = ClientSize.Height - pad - buttonHeight;
        var listHeight = buttonY - listY - em;
        var inputWidth = ClientSize.Width - 2 * pad;

        var label = new Label
        {
            Text = "Select which accounts to sync:",
            Location = new Point(pad, labelY),
            AutoSize = true,
        };

        _checkedListBox = new CheckedListBox
        {
            Location = new Point(pad, listY),
            Size = new Size(inputWidth, listHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            CheckOnClick = true,
        };

        for (int i = 0; i < accounts.Count; i++)
        {
            var (accountId, name, isPrimary) = accounts[i];
            var displayText = isPrimary ? $"{name} (primary)" : name;
            _checkedListBox.Items.Add(displayText);

            // null preselected = all checked (new login), otherwise check only those in set
            var isChecked = preselected == null || preselected.Contains(accountId);
            _checkedListBox.SetItemChecked(i, isChecked);
        }

        var okButton = new Button
        {
            Text = "OK",
            AutoSize = true,
            Height = buttonHeight,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.OK,
        };
        okButton.Location = new Point(ClientSize.Width - pad - okButton.PreferredSize.Width, buttonY);
        okButton.Click += (_, _) =>
        {
            SelectedAccountIds = new HashSet<string>();
            for (int i = 0; i < _accounts.Count; i++)
            {
                if (_checkedListBox.GetItemChecked(i))
                    SelectedAccountIds.Add(_accounts[i].AccountId);
            }
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            Height = buttonHeight,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
        };
        cancelButton.Location = new Point(okButton.Left - cancelButton.PreferredSize.Width - em / 2, buttonY);

        Controls.AddRange([label, _checkedListBox, okButton, cancelButton]);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }
}
