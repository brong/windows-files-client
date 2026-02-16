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
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private NotifyIcon? _notifyIcon;
    private SynchronizationContext? _syncContext;
    private Icon? _baseIcon;
    private IntPtr _currentHIcon;
    private bool _disposed;

    public TrayIcon(CancellationTokenSource cts, string? iconPath, string syncRootPath)
    {
        _cts = cts;
        _iconPath = iconPath;
        _syncRootPath = syncRootPath;
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

    public void UpdateStatus(SyncStatus status)
    {
        _syncContext?.Post(_ =>
        {
            if (_notifyIcon == null) return;

            var (color, tooltip) = status switch
            {
                SyncStatus.Idle => (Color.LimeGreen, "Fastmail Files - Up to date"),
                SyncStatus.Syncing => (Color.DodgerBlue, "Fastmail Files - Syncing..."),
                SyncStatus.Error => (Color.Red, "Fastmail Files - Error"),
                SyncStatus.Disconnected => (Color.Gray, "Fastmail Files - Connection lost"),
                _ => (Color.Gray, "Fastmail Files"),
            };

            _notifyIcon.Text = tooltip;
            SetIconWithDot(color);
        }, null);
    }

    private void RunMessageLoop()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

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

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) =>
        {
            _cts.Cancel();
            Application.ExitThread();
        });

        return menu;
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
