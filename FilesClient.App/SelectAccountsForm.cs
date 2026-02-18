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

        Text = "Select Accounts to Sync";
        Size = new Size(400, 300);
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
            Width = 360,
            Height = 160,
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
            Width = 80,
            Height = 28,
            Location = new Point(292, 205),
            DialogResult = DialogResult.OK,
        };
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
            Width = 80,
            Height = 28,
            Location = new Point(200, 205),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange([label, _checkedListBox, okButton, cancelButton]);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }
}
