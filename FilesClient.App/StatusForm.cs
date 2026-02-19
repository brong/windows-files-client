using System.Drawing;
using FilesClient.Ipc;

namespace FilesClient.App;

sealed class StatusForm : Form
{
    private readonly string _accountId;
    private readonly ServiceClient _serviceClient;
    private readonly Label _headerLabel;
    private readonly ListView _listView;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _dirty = true;
    private bool _hasActiveUploads;

    public StatusForm(string accountLabel, string accountId, ServiceClient serviceClient)
    {
        _accountId = accountId;
        _serviceClient = serviceClient;

        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Fastmail Files - Pending Changes";
        Size = new Size(600, 400);
        MinimumSize = new Size(400, 250);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        // Account header
        _headerLabel = new Label
        {
            Text = FormatHeader(accountLabel),
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8, 8, 8, 0),
            Font = new Font("Segoe UI", 9.5f),
        };
        Controls.Add(_headerLabel);

        // ListView
        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Clickable,
            ShowItemToolTips = true,
            OwnerDraw = true,
        };
        _listView.Columns.Add("Name", 200);
        _listView.Columns.Add("Action", 100);
        _listView.Columns.Add("Status", 100);
        _listView.Columns.Add("Updated", 120);

        _listView.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _listView.DrawItem += (_, _) => { };
        _listView.DrawSubItem += OnDrawSubItem;

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
            AutoSize = true,
            Height = 28,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        closeButton.Location = new Point(
            bottomPanel.Width - closeButton.Width - 12,
            (bottomPanel.Height - closeButton.Height) / 2);
        closeButton.Click += (_, _) => Hide();
        bottomPanel.Controls.Add(closeButton);
        Controls.Add(bottomPanel);

        // Ensure correct Z-order
        bottomPanel.BringToFront();
        _listView.BringToFront();
        _headerLabel.BringToFront();

        _serviceClient.StatusChanged += () => _dirty = true;

        // Refresh timer
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    private async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (!_dirty && !_hasActiveUploads)
            return;

        _dirty = false;

        try
        {
            var snapshot = await _serviceClient.GetOutboxAsync(_accountId);
            RefreshList(snapshot.Entries);
        }
        catch
        {
            // IPC not available — leave list as-is
        }
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
            _dirty = true;
    }

    private void RefreshList(List<OutboxEntry> entries)
    {
        var now = DateTime.UtcNow;

        _listView.BeginUpdate();
        _listView.Items.Clear();

        // Sort: most upload progress first, then oldest first
        var sorted = entries.OrderByDescending(e =>
            e.IsProcessing ? (e.UploadProgress ?? -1) : -2)
            .ThenBy(e => e.CreatedAt);

        foreach (var entry in sorted)
        {
            var name = entry.LocalPath != null
                ? Path.GetFileName(entry.LocalPath)
                : entry.NodeId ?? "(unknown)";

            var action = DeriveAction(entry);
            string status;
            int? progress = null;
            if (entry.IsProcessing)
            {
                progress = entry.UploadProgress;
                status = progress.HasValue ? "" : "Syncing...";
            }
            else
            {
                status = DeriveStatus(entry, now);
            }

            var item = new ListViewItem(name);
            item.SubItems.Add(action);
            var statusSubItem = item.SubItems.Add(status);
            if (progress.HasValue)
                statusSubItem.Tag = progress.Value;
            item.SubItems.Add(FormatRelativeTime(entry.UpdatedAt, now));

            // Tooltip
            var tooltip = entry.LocalPath ?? entry.NodeId ?? "";
            if (progress.HasValue)
                tooltip += $"\nUploading {progress.Value}%";
            if (entry.LastError != null)
                tooltip += $"\nError: {entry.LastError}";
            item.ToolTipText = tooltip;

            // Color
            if (entry.IsProcessing)
                item.ForeColor = Color.DodgerBlue;
            else if (entry.LastError != null)
                item.ForeColor = Color.Red;

            _listView.Items.Add(item);
        }

        _listView.EndUpdate();
        _hasActiveUploads = entries.Any(e => e.IsProcessing);

        // Update header status from account info
        var account = _serviceClient.Accounts.FirstOrDefault(a => a.AccountId == _accountId);
        if (account != null)
        {
            var indicator = account.Status switch
            {
                AccountStatus.Idle => "\u25cf Connected",
                AccountStatus.Syncing => "\u25cf Syncing",
                AccountStatus.Disconnected => "\u25cb Offline",
                AccountStatus.Error => "\u25cf Error",
                _ => "",
            };
            _headerLabel.Text = $"  {account.Username}    {indicator}";
        }
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex == 2 && e.SubItem?.Tag is int percent)
        {
            var g = e.Graphics!;
            var bounds = e.Bounds;

            using (var bgBrush = new SolidBrush(_listView.BackColor))
                g.FillRectangle(bgBrush, bounds);

            var barHeight = Math.Max(4, bounds.Height / 3);
            var barY = bounds.Y + (bounds.Height - barHeight) / 2;
            var trackRect = new Rectangle(bounds.X + 4, barY, bounds.Width - 8, barHeight);

            using (var trackBrush = new SolidBrush(Color.FromArgb(228, 228, 228)))
                g.FillRectangle(trackBrush, trackRect);

            if (percent > 0)
            {
                var fillWidth = (int)(trackRect.Width * percent / 100.0);
                var fillRect = new Rectangle(trackRect.X, trackRect.Y, fillWidth, trackRect.Height);
                using var fillBrush = new SolidBrush(Color.DodgerBlue);
                g.FillRectangle(fillBrush, fillRect);
            }
        }
        else
        {
            e.DrawDefault = true;
        }
    }

    private static string DeriveAction(OutboxEntry entry)
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

    private static string DeriveStatus(OutboxEntry entry, DateTime now)
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

    private static string FormatHeader(string account)
    {
        return $"  {account}";
    }
}
