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
    private readonly string _syncRootPath;
    private readonly string _username;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private NotifyIcon? _notifyIcon;
    private SynchronizationContext? _syncContext;
    private Icon? _baseIcon;
    private IntPtr _currentHIcon;
    private SyncStatus _lastStatus;
    private string? _lastDetail;
    private int _pendingCount;
    private bool _disposed;
    private SyncOutbox? _outbox;
    private string? _accountLabel;
    private StatusForm? _statusForm;

    public TrayIcon(CancellationTokenSource cts, string? iconPath, string syncRootPath, string username)
    {
        _cts = cts;
        _iconPath = iconPath;
        _syncRootPath = syncRootPath;
        _username = username;
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

    public void SetOutbox(string accountLabel, SyncOutbox outbox)
    {
        _outbox = outbox;
        _accountLabel = accountLabel;
    }

    public void UpdateStatus(SyncStatus status)
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            _lastStatus = status;
            RefreshTooltip();
            _statusForm?.UpdateStatus(status);
        }, null);
    }

    public void UpdateStatusDetail(string? detail)
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            _lastDetail = detail;
            RefreshTooltip();
        }, null);
    }

    public void UpdatePendingCount(int count)
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;
            _pendingCount = count;
            RefreshTooltip();
        }, null);
    }

    private void RefreshTooltip()
    {
        var pendingSuffix = _pendingCount > 0
            ? $" ({_pendingCount} pending)"
            : "";

        var (color, defaultTooltip) = _lastStatus switch
        {
            SyncStatus.Idle when _pendingCount > 0 =>
                (Color.DodgerBlue, $"{_username} - {_pendingCount} pending changes"),
            SyncStatus.Idle => (Color.LimeGreen, $"{_username} - Up to date"),
            SyncStatus.Syncing => (Color.DodgerBlue, $"{_username} - Syncing..."),
            SyncStatus.Error => (Color.Red, $"{_username} - Error"),
            SyncStatus.Disconnected when _pendingCount > 0 =>
                (Color.Gray, $"{_username} - Offline ({_pendingCount} pending)"),
            SyncStatus.Disconnected => (Color.Gray, $"{_username} - Connection lost"),
            _ => (Color.Gray, _username),
        };

        var tooltip = _lastDetail != null
            ? $"{_username} - {_lastDetail}{pendingSuffix}"
            : defaultTooltip;

        // NotifyIcon.Text has a 127-character limit
        if (tooltip.Length > 127)
            tooltip = tooltip.Substring(0, 127);

        _notifyIcon!.Text = tooltip;
        SetIconWithDot(color);
    }

    private void RunMessageLoop()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        _baseIcon = LoadBaseIcon();

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = $"{_username} - Starting...",
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

            // Draw status dot in bottom-right corner
            using var brush = new SolidBrush(dotColor);
            g.FillEllipse(brush, 9, 9, 7, 7);

            // Thin dark outline for contrast
            using var pen = new Pen(Color.FromArgb(80, 0, 0, 0), 1);
            g.DrawEllipse(pen, 9, 9, 6, 6);
        }

        var newHIcon = bmp.GetHicon();
        _notifyIcon.Icon = Icon.FromHandle(newHIcon);

        // Destroy the previous HICON to avoid GDI handle leak
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

        menu.Items.Add("Open sync folder", null, (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _syncRootPath,
                    UseShellExecute = true,
                });
            }
            catch { /* best-effort */ }
        });

        menu.Items.Add("View pending changes...", null, (_, _) =>
        {
            ShowStatusForm();
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) =>
        {
            _cts.Cancel();
            Application.ExitThread();
        });

        return menu;
    }

    private void ShowStatusForm()
    {
        if (_outbox == null || _accountLabel == null)
            return;

        _syncContext?.Post(_ =>
        {
            if (_statusForm == null || _statusForm.IsDisposed)
                _statusForm = new StatusForm(_accountLabel, _outbox, _lastStatus);

            if (_statusForm.Visible)
            {
                _statusForm.Activate();
            }
            else
            {
                _statusForm.Show();
            }
        }, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Marshal shutdown to the STA thread if it's still running
        if (_syncContext != null && _thread?.IsAlive == true)
        {
            _syncContext.Post(_ => Application.ExitThread(), null);
            _thread.Join(3000);
        }

        _ready.Dispose();
    }
}
