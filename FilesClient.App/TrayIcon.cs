using System.Drawing;
using System.Runtime.InteropServices;
using FilesClient.Ipc;

namespace FilesClient.App;

sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly CancellationTokenSource _cts;
    private readonly string? _iconPath;
    private readonly ServiceClient _serviceClient;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private SynchronizationContext? _syncContext;
    private Icon? _baseIcon;
    private IntPtr _currentHIcon;
    private bool _disposed;
    private StatusForm? _statusForm;
    private ManageAccountsForm? _manageForm;

    public TrayIcon(CancellationTokenSource cts, string? iconPath, ServiceClient serviceClient)
    {
        _cts = cts;
        _iconPath = iconPath;
        _serviceClient = serviceClient;

        _serviceClient.AccountsChanged += OnAccountsChanged;
        _serviceClient.StatusChanged += OnStatusChanged;
        _serviceClient.ConnectionChanged += OnConnectionChanged;
    }

    public void Start()
    {
        _thread = new Thread(RunMessageLoop)
        {
            Name = "TrayIcon",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public void ShowManageAccounts()
    {
        _syncContext?.Post(_ => OpenManageAccountsForm(), null);
    }

    private void OnAccountsChanged()
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            RefreshTooltip();
        }, null);
    }

    private void OnStatusChanged()
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            RefreshTooltip();
        }, null);
    }

    private void OnConnectionChanged(bool connected)
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            RefreshTooltip();
        }, null);
    }

    private void RefreshTooltip()
    {
        var accounts = _serviceClient.Accounts;
        var status = _serviceClient.AggregateStatus;
        var pendingCount = _serviceClient.AggregatePendingCount;
        var connected = _serviceClient.IsConnected;

        Color color;
        string tooltip;

        if (!connected)
        {
            color = Color.Gray;
            tooltip = "Fastmail Files - Service not running";
        }
        else if (accounts.Count == 0)
        {
            var connecting = _serviceClient.ConnectingLoginIds;
            var failed = _serviceClient.FailedLogins;
            color = Color.Gray;
            if (connecting.Count > 0)
                tooltip = "Fastmail Files - Connecting...";
            else if (failed.Count > 0)
                tooltip = failed.Count == 1
                    ? "Fastmail Files - 1 login offline"
                    : $"Fastmail Files - {failed.Count} logins offline";
            else
                tooltip = "Fastmail Files - No accounts";
        }
        else if (accounts.Count == 1)
        {
            var a = accounts[0];
            (color, tooltip) = FormatSingleAccountTooltip(a.Username, a.Status, a.PendingCount, a.StatusDetail);
        }
        else
        {
            color = status switch
            {
                AccountStatus.Error => Color.Red,
                AccountStatus.Disconnected => Color.Gray,
                AccountStatus.Syncing => Color.DodgerBlue,
                AccountStatus.Idle when pendingCount > 0 => Color.DodgerBlue,
                AccountStatus.Idle => Color.LimeGreen,
                _ => Color.Gray,
            };

            var statusText = status switch
            {
                AccountStatus.Idle when pendingCount > 0 => $"{pendingCount} pending",
                AccountStatus.Idle => "Up to date",
                AccountStatus.Syncing => "Syncing...",
                AccountStatus.Error => "Error",
                AccountStatus.Disconnected => "Offline",
                _ => "",
            };

            tooltip = $"{accounts.Count} accounts - {statusText}";
        }

        if (tooltip.Length > 127)
            tooltip = tooltip.Substring(0, 127);

        _notifyIcon!.Text = tooltip;
        SetIconWithDot(color);
    }

    private static (Color color, string tooltip) FormatSingleAccountTooltip(
        string username, AccountStatus status, int pendingCount, string? detail)
    {
        var pendingSuffix = pendingCount > 0 ? $" ({pendingCount} pending)" : "";

        var (color, defaultTooltip) = status switch
        {
            AccountStatus.Idle when pendingCount > 0 =>
                (Color.DodgerBlue, $"{username} - {pendingCount} pending changes"),
            AccountStatus.Idle => (Color.LimeGreen, $"{username} - Up to date"),
            AccountStatus.Syncing => (Color.DodgerBlue, $"{username} - Syncing..."),
            AccountStatus.Error => (Color.Red, $"{username} - Error"),
            AccountStatus.Disconnected when pendingCount > 0 =>
                (Color.Gray, $"{username} - Offline ({pendingCount} pending)"),
            AccountStatus.Disconnected => (Color.Gray, $"{username} - Connection lost"),
            _ => (Color.Gray, username),
        };

        var tooltip = detail != null
            ? $"{username} - {detail}{pendingSuffix}"
            : defaultTooltip;

        return (color, tooltip);
    }

    private void RunMessageLoop()
    {
        _baseIcon = LoadBaseIcon();

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Opening += (_, _) =>
        {
            // Rebuild menu items with fresh state each time user opens the menu
            PopulateContextMenu();
            // Kick off a connection check — if the pipe is dead this will
            // update state and rebuild the menu immediately after
            _ = CheckAndRebuildMenuAsync();
        };

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "Fastmail Files - Starting...",
            ContextMenuStrip = _contextMenu,
        };

        SetIconWithDot(Color.Gray);

        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _ready.Set();

        // One-shot timer to refresh UI once the message loop is pumping
        // and the service client has had time to connect and receive status.
        var initTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        initTimer.Tick += (_, _) =>
        {
            initTimer.Stop();
            initTimer.Dispose();
            RefreshTooltip();
        };
        initTimer.Start();

        Application.Run();

        // Cleanup after Application.ExitThread()
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
        _contextMenu.Dispose();
        _contextMenu = null;
        DestroyCurrentHIcon();
        _baseIcon?.Dispose();
    }

    private Icon LoadBaseIcon()
    {
        if (_iconPath != null && File.Exists(_iconPath))
        {
            try { return new Icon(_iconPath, 16, 16); }
            catch { /* fall through to default */ }
        }

        return SystemIcons.Application;
    }

    private void SetIconWithDot(Color dotColor)
    {
        if (_notifyIcon == null || _baseIcon == null) return;

        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawIcon(_baseIcon, new Rectangle(0, 0, 16, 16));

            using var brush = new SolidBrush(dotColor);
            g.FillEllipse(brush, 9, 9, 7, 7);

            using var pen = new Pen(Color.FromArgb(80, 0, 0, 0), 1);
            g.DrawEllipse(pen, 9, 9, 6, 6);
        }

        var newHIcon = bmp.GetHicon();
        _notifyIcon.Icon = Icon.FromHandle(newHIcon);

        DestroyCurrentHIcon();
        _currentHIcon = newHIcon;
    }

    private void DestroyCurrentHIcon()
    {
        if (_currentHIcon != IntPtr.Zero)
        {
            DestroyIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
        }
    }

    private async Task CheckAndRebuildMenuAsync()
    {
        var wasPreviouslyConnected = _serviceClient.IsConnected;
        await _serviceClient.CheckConnectionAsync();
        // If the check changed the connection state, rebuild the open menu
        if (wasPreviouslyConnected != _serviceClient.IsConnected)
        {
            PopulateContextMenu();
            RefreshTooltip();
        }
    }

    private void PopulateContextMenu()
    {
        if (_contextMenu == null) return;
        _contextMenu.Items.Clear();

        var connected = _serviceClient.IsConnected;

        if (!connected)
        {
            _contextMenu.Items.Add(new ToolStripMenuItem("Service not running") { Enabled = false });
        }
        else
        {
            var accounts = _serviceClient.Accounts;
            var connecting = _serviceClient.ConnectingLoginIds;
            var failed = _serviceClient.FailedLogins;

            if (accounts.Count == 0 && connecting.Count == 0 && failed.Count == 0)
            {
                var noAccounts = new ToolStripMenuItem("No accounts configured") { Enabled = false };
                _contextMenu.Items.Add(noAccounts);
            }
            else if (accounts.Count == 0 && failed.Count == 0)
            {
                foreach (var loginId in connecting)
                    _contextMenu.Items.Add(new ToolStripMenuItem($"{loginId} \u2014 Connecting...") { Enabled = false });
            }
            else if (accounts.Count == 1 && connecting.Count == 0 && failed.Count == 0)
            {
                var a = accounts[0];
                _contextMenu.Items.Add($"Open {a.DisplayName}", null, (_, _) => OpenSyncFolder(a.SyncRootPath));
                _contextMenu.Items.Add("View pending changes...", null, (_, _) => ShowStatusForm(a));
            }
            else
            {
                foreach (var a in accounts)
                {
                    var sub = new ToolStripMenuItem(a.DisplayName);
                    var captured = a;
                    sub.DropDownItems.Add("Open sync folder", null, (_, _) => OpenSyncFolder(captured.SyncRootPath));
                    sub.DropDownItems.Add("View pending changes...", null, (_, _) => ShowStatusForm(captured));
                    _contextMenu.Items.Add(sub);
                }
                foreach (var loginId in connecting)
                    _contextMenu.Items.Add(new ToolStripMenuItem($"{loginId} \u2014 Connecting...") { Enabled = false });
                foreach (var f in failed)
                    _contextMenu.Items.Add(new ToolStripMenuItem($"{f.LoginId} \u2014 Offline") { Enabled = false, ForeColor = Color.Red });
            }
        }

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Manage accounts...", null, (_, _) => OpenManageAccountsForm());

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add("Exit", null, (_, _) =>
        {
            _cts.Cancel();
            Application.ExitThread();
        });
    }

    private static void OpenSyncFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    private void ShowStatusForm(AccountInfo account)
    {
        _syncContext?.Post(_ =>
        {
            if (_statusForm == null || _statusForm.IsDisposed)
                _statusForm = new StatusForm(account.Username, account.AccountId, _serviceClient);

            if (_statusForm.Visible)
                _statusForm.Activate();
            else
                _statusForm.Show();
        }, null);
    }

    private void OpenManageAccountsForm()
    {
        _syncContext?.Post(_ =>
        {
            if (_manageForm == null || _manageForm.IsDisposed)
                _manageForm = new ManageAccountsForm(_serviceClient);

            if (_manageForm.Visible)
                _manageForm.Activate();
            else
                _manageForm.Show();
        }, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_syncContext != null && _thread?.IsAlive == true)
        {
            _syncContext.Post(_ => Application.ExitThread(), null);
            _thread.Join(3000);
        }

        _ready.Dispose();
    }
}
