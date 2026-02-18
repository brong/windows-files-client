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

        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Select Accounts to Sync";
        Size = new Size(420, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var label = new Label
        {
            Text = "Select which accounts to sync:",
            Location = new Point(12, 12),
            AutoSize = true,
        };

        _checkedListBox = new CheckedListBox
        {
            Location = new Point(12, 35),
            Size = new Size(380, 180),
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
            Height = 28,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.OK,
        };
        okButton.Location = new Point(310, 225);
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
            Height = 28,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
        };
        cancelButton.Location = new Point(220, 225);

        Controls.AddRange([label, _checkedListBox, okButton, cancelButton]);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }
}
