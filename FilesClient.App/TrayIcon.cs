using System.Drawing;
using System.Runtime.InteropServices;
using FilesClient.Windows;

namespace FilesClient.App;

sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly CancellationTokenSource _cts;
    private readonly string? _iconPath;
    private readonly LoginManager _loginManager;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private NotifyIcon? _notifyIcon;
    private SynchronizationContext? _syncContext;
    private Icon? _baseIcon;
    private IntPtr _currentHIcon;
    private bool _disposed;
    private StatusForm? _statusForm;
    private ManageAccountsForm? _manageForm;

    public TrayIcon(CancellationTokenSource cts, string? iconPath, LoginManager loginManager)
    {
        _cts = cts;
        _iconPath = iconPath;
        _loginManager = loginManager;

        _loginManager.AccountsChanged += OnAccountsChanged;
        _loginManager.AggregateStatusChanged += OnAggregateStatusChanged;
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
            _notifyIcon.ContextMenuStrip = BuildContextMenu();
            RefreshTooltip();
        }, null);
    }

    private void OnAggregateStatusChanged(SyncStatus status)
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            RefreshTooltip();
        }, null);
    }

    private void RefreshTooltip()
    {
        var supervisors = _loginManager.Supervisors;
        var status = _loginManager.GetAggregateStatus();
        var pendingCount = _loginManager.GetAggregatePendingCount();

        Color color;
        string tooltip;

        if (supervisors.Count == 0)
        {
            color = Color.Gray;
            tooltip = "Fastmail Files - No accounts";
        }
        else if (supervisors.Count == 1)
        {
            var s = supervisors[0];
            (color, tooltip) = FormatSingleAccountTooltip(s.Username, s.Status, s.PendingCount, s.StatusDetail);
        }
        else
        {
            color = status switch
            {
                SyncStatus.Error => Color.Red,
                SyncStatus.Disconnected => Color.Gray,
                SyncStatus.Syncing => Color.DodgerBlue,
                SyncStatus.Idle when pendingCount > 0 => Color.DodgerBlue,
                SyncStatus.Idle => Color.LimeGreen,
                _ => Color.Gray,
            };

            var statusText = status switch
            {
                SyncStatus.Idle when pendingCount > 0 => $"{pendingCount} pending",
                SyncStatus.Idle => "Up to date",
                SyncStatus.Syncing => "Syncing...",
                SyncStatus.Error => "Error",
                SyncStatus.Disconnected => "Offline",
                _ => "",
            };

            tooltip = $"{supervisors.Count} accounts - {statusText}";
        }

        if (tooltip.Length > 127)
            tooltip = tooltip.Substring(0, 127);

        _notifyIcon!.Text = tooltip;
        SetIconWithDot(color);
    }

    private static (Color color, string tooltip) FormatSingleAccountTooltip(
        string username, SyncStatus status, int pendingCount, string? detail)
    {
        var pendingSuffix = pendingCount > 0 ? $" ({pendingCount} pending)" : "";

        var (color, defaultTooltip) = status switch
        {
            SyncStatus.Idle when pendingCount > 0 =>
                (Color.DodgerBlue, $"{username} - {pendingCount} pending changes"),
            SyncStatus.Idle => (Color.LimeGreen, $"{username} - Up to date"),
            SyncStatus.Syncing => (Color.DodgerBlue, $"{username} - Syncing..."),
            SyncStatus.Error => (Color.Red, $"{username} - Error"),
            SyncStatus.Disconnected when pendingCount > 0 =>
                (Color.Gray, $"{username} - Offline ({pendingCount} pending)"),
            SyncStatus.Disconnected => (Color.Gray, $"{username} - Connection lost"),
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
            Text = "Fastmail Files - Starting...",
            ContextMenuStrip = BuildContextMenu(),
        };

        SetIconWithDot(Color.Gray);

        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _ready.Set();

        Application.Run();

        // Cleanup after Application.ExitThread()
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

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var supervisors = _loginManager.Supervisors;

        var connecting = _loginManager.ConnectingLoginIds;

        if (supervisors.Count == 0 && connecting.Count == 0)
        {
            var noAccounts = new ToolStripMenuItem("No accounts configured") { Enabled = false };
            menu.Items.Add(noAccounts);
        }
        else if (supervisors.Count == 0)
        {
            foreach (var loginId in connecting)
                menu.Items.Add(new ToolStripMenuItem($"{loginId} \u2014 Connecting...") { Enabled = false });
        }
        else if (supervisors.Count == 1 && connecting.Count == 0)
        {
            var s = supervisors[0];
            menu.Items.Add($"Open {s.DisplayName}", null, (_, _) => OpenSyncFolder(s.SyncRootPath));
            menu.Items.Add("View pending changes...", null, (_, _) => ShowStatusForm(s));
        }
        else
        {
            foreach (var s in supervisors)
            {
                var sub = new ToolStripMenuItem(s.DisplayName);
                var captured = s;
                sub.DropDownItems.Add("Open sync folder", null, (_, _) => OpenSyncFolder(captured.SyncRootPath));
                sub.DropDownItems.Add("View pending changes...", null, (_, _) => ShowStatusForm(captured));
                menu.Items.Add(sub);
            }
            foreach (var loginId in connecting)
                menu.Items.Add(new ToolStripMenuItem($"{loginId} \u2014 Connecting...") { Enabled = false });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Manage accounts...", null, (_, _) => OpenManageAccountsForm());

        if (Program.IsDebugMode)
        {
            var consoleLabel = Program.IsDebugConsoleVisible()
                ? "Hide debug console"
                : "Show debug console";
            menu.Items.Add(consoleLabel, null, (_, _) =>
            {
                if (Program.IsDebugConsoleVisible())
                    Program.HideDebugConsole();
                else
                    Program.ShowDebugConsole();

                // Rebuild menu to update the label
                if (_notifyIcon != null)
                    _notifyIcon.ContextMenuStrip = BuildContextMenu();
            });
        }

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) =>
        {
            _cts.Cancel();
            Application.ExitThread();
        });

        return menu;
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

    private void ShowStatusForm(AccountSupervisor supervisor)
    {
        if (supervisor.Outbox == null)
            return;

        _syncContext?.Post(_ =>
        {
            if (_statusForm == null || _statusForm.IsDisposed)
                _statusForm = new StatusForm(supervisor.Username, supervisor.Outbox, supervisor.Status);

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
                _manageForm = new ManageAccountsForm(_loginManager, _iconPath);

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
