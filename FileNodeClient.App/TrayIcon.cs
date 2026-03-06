using System.Drawing;
using System.Runtime.InteropServices;
using FileNodeClient.Ipc;

namespace FileNodeClient.App;

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
    private SynchronizationContext? _syncContext;
    private Icon? _baseIcon;
    private IntPtr _currentHIcon;
    private bool _disposed;
    private ManageAccountsForm? _manageForm;
    private Color _lastDotColor;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 300 };

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
        _syncContext?.Post(_ => ToggleManageAccountsForm(), null);
    }

    private void OnAccountsChanged()
    {
        ScheduleRefresh();
    }

    private void OnStatusChanged()
    {
        ScheduleRefresh();
    }

    private void OnConnectionChanged(bool connected)
    {
        ScheduleRefresh();
    }

    /// <summary>
    /// Coalesce rapid-fire status/account events into a single UI refresh.
    /// The timer runs on the STA thread so the Tick handler is safe for UI work.
    /// </summary>
    private void ScheduleRefresh()
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            // Restart the timer — only the last event in a burst actually fires
            _refreshTimer.Stop();
            _refreshTimer.Start();
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
            tooltip = "FileNodeClient - Service not running";
        }
        else if (accounts.Count == 0)
        {
            var connecting = _serviceClient.ConnectingLoginIds;
            var failed = _serviceClient.FailedLogins;
            if (connecting.Count > 0)
            {
                color = Color.Gray;
                tooltip = "FileNodeClient - Connecting...";
            }
            else if (failed.Count > 0)
            {
                color = Color.FromArgb(200, 120, 0);
                tooltip = failed.Count == 1
                    ? "FileNodeClient - 1 login offline"
                    : $"FileNodeClient - {failed.Count} logins offline";
            }
            else
            {
                color = Color.Gray;
                tooltip = "FileNodeClient - No accounts";
            }
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

        // Override to orange when active accounts exist but some logins failed
        if (connected && accounts.Count > 0)
        {
            var failed = _serviceClient.FailedLogins;
            if (failed.Count > 0)
            {
                if (color == Color.LimeGreen || color == Color.DodgerBlue)
                    color = Color.FromArgb(200, 120, 0);
                tooltip += $" ({failed.Count} offline)";
            }
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

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "FileNodeClient - Starting...",
        };

        var contextMenu = new ContextMenuStrip();
        var manageItem = new ToolStripMenuItem("Manage Accounts");
        manageItem.Click += (_, _) => ToggleManageAccountsForm();
        var startServiceItem = new ToolStripMenuItem("Start Service");
        startServiceItem.Click += (_, _) => ServiceLauncher.TryStartService();
        var stopServiceItem = new ToolStripMenuItem("Stop Service");
        stopServiceItem.Click += (_, _) => ServiceLauncher.StopService();
        var restartServiceItem = new ToolStripMenuItem("Restart Service");
        restartServiceItem.Click += async (_, _) =>
        {
            ServiceLauncher.StopService();
            await ServiceLauncher.WaitForExitAsync();
            await Task.Delay(500);
            ServiceLauncher.TryStartService();
        };
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _cts.Cancel();
            Application.ExitThread();
        };
        contextMenu.Items.Add(manageItem);
        contextMenu.Items.Add(startServiceItem);
        contextMenu.Items.Add(stopServiceItem);
        contextMenu.Items.Add(restartServiceItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);
        contextMenu.Opening += (_, _) =>
        {
            var connected = _serviceClient.IsConnected;
            startServiceItem.Visible = !connected;
            stopServiceItem.Visible = connected;
            restartServiceItem.Visible = connected;
        };
        _notifyIcon.ContextMenuStrip = contextMenu;

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleManageAccountsForm();
        };

        SetIconWithDot(Color.Gray);

        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        // Debounce timer for coalescing rapid status events
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            if (_notifyIcon != null)
                RefreshTooltip();
        };

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
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
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
        if (dotColor == _lastDotColor && _currentHIcon != IntPtr.Zero) return;
        _lastDotColor = dotColor;

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

    public static void OpenSyncFolder(string path)
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

    private void ToggleManageAccountsForm()
    {
        if (_manageForm != null && !_manageForm.IsDisposed && _manageForm.Visible)
        {
            _manageForm.Hide();
            return;
        }

        if (_manageForm == null || _manageForm.IsDisposed)
            _manageForm = new ManageAccountsForm(_serviceClient, _cts);

        _manageForm.Show();
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
