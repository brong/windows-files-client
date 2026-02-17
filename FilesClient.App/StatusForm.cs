using System.Drawing;
using FilesClient.Windows;

namespace FilesClient.App;

sealed class StatusForm : Form
{
    private readonly SyncOutbox _outbox;
    private readonly Label _headerLabel;
    private readonly ListView _listView;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _dirty = true;
    private SyncStatus _currentStatus;

    public StatusForm(string accountLabel, SyncOutbox outbox, SyncStatus initialStatus)
    {
        _outbox = outbox;
        _currentStatus = initialStatus;

        Text = "Fastmail Files - Pending Changes";
        Size = new Size(600, 400);
        MinimumSize = new Size(400, 250);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        // Account header
        _headerLabel = new Label
        {
            Text = FormatHeader(accountLabel, initialStatus),
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8, 8, 8, 0),
            Font = new Font(Font.FontFamily, 9.5f),
        };
        Controls.Add(_headerLabel);

        // ListView
        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            ShowItemToolTips = true,
        };
        _listView.Columns.Add("Name", 200);
        _listView.Columns.Add("Action", 100);
        _listView.Columns.Add("Status", 100);
        _listView.Columns.Add("Updated", 120);
        Controls.Add(_listView);

        // Bottom panel with Close button
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
        };
        var closeButton = new Button
        {
            Text = "Close",
            Width = 75,
            Height = 28,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        closeButton.Location = new Point(
            bottomPanel.Width - closeButton.Width - 12,
            (bottomPanel.Height - closeButton.Height) / 2);
        closeButton.Click += (_, _) => Hide();
        bottomPanel.Controls.Add(closeButton);
        Controls.Add(bottomPanel);

        // Ensure correct Z-order: header top, bottom panel bottom, list fills middle
        bottomPanel.BringToFront();
        _listView.BringToFront();
        _headerLabel.BringToFront();

        // Subscribe to outbox changes
        _outbox.PendingCountChanged += _ => _dirty = true;

        // Refresh timer
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_dirty)
            {
                _dirty = false;
                RefreshList();
            }
        };
        _refreshTimer.Start();
    }

    public void UpdateStatus(SyncStatus status)
    {
        _currentStatus = status;
        var parts = _headerLabel.Text.Split(new[] { "  " }, 2, StringSplitOptions.None);
        var account = parts.Length > 0 ? parts[0].Trim() : "";
        _headerLabel.Text = FormatHeader(account, status);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _dirty = false;
            RefreshList();
        }
    }

    private void RefreshList()
    {
        var (snapshot, processingIds) = _outbox.GetSnapshot();
        var now = DateTime.UtcNow;

        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (var entry in snapshot)
        {
            var name = entry.LocalPath != null
                ? Path.GetFileName(entry.LocalPath)
                : entry.NodeId ?? "(unknown)";

            var action = DeriveAction(entry);
            var isProcessing = processingIds.Contains(entry.Id);
            var status = isProcessing ? "Syncing..." : DeriveStatus(entry, now);

            var item = new ListViewItem(name);
            item.SubItems.Add(action);
            item.SubItems.Add(status);
            item.SubItems.Add(FormatRelativeTime(entry.UpdatedAt, now));

            // Tooltip: full path + last error
            var tooltip = entry.LocalPath ?? entry.NodeId ?? "";
            if (entry.LastError != null)
                tooltip += $"\nError: {entry.LastError}";
            item.ToolTipText = tooltip;

            // Color: blue for active sync, red for errors
            if (isProcessing)
                item.ForeColor = Color.DodgerBlue;
            else if (entry.LastError != null)
                item.ForeColor = Color.Red;

            _listView.Items.Add(item);
        }

        _listView.EndUpdate();
    }

    private static string DeriveAction(PendingChange entry)
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

    private static string DeriveStatus(PendingChange entry, DateTime now)
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

    private static string FormatHeader(string account, SyncStatus status)
    {
        var indicator = status switch
        {
            SyncStatus.Idle => "\u25cf Connected",
            SyncStatus.Syncing => "\u25cf Syncing",
            SyncStatus.Disconnected => "\u25cb Offline",
            SyncStatus.Error => "\u25cf Error",
            _ => "",
        };
        return $"  {account}    {indicator}";
    }
}
