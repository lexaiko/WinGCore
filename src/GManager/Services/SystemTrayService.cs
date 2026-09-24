using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GManager.Core;

namespace GManager.Services;

public sealed class SystemTrayService : IDisposable
{
    private readonly AppConfig _config;
    private readonly SyncService _syncService;
    private readonly IEventAggregator _eventAggregator;

    private Window? _mainWindow;
    private IntPtr _hwnd;
    private bool _isInitialized;
    private bool _isExiting;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 120;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public SystemTrayService(
        AppConfig config,
        SyncService syncService,
        IEventAggregator eventAggregator)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
    }

    public void Initialize(Window mainWindow)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _hwnd = new WindowInteropHelper(mainWindow).EnsureHandle();

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        CreateTrayIcon();
        _isInitialized = true;

        _mainWindow.Closing += (sender, e) =>
        {
            if (!_isExiting && _config.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                _mainWindow.Hide();
            }
        };
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_isInitialized || _hwnd == IntPtr.Zero) return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_TIP,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void CreateTrayIcon()
    {
        // 32512 = IDI_APPLICATION (default app icon)
        var hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512);

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "GManager - Google Services Runtime"
        };

        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseEvent = lParam.ToInt32();
            if (mouseEvent is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                RestoreMainWindow();
                handled = true;
            }
            else if (mouseEvent == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void RestoreMainWindow()
    {
        if (_mainWindow == null) return;

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        SetForegroundWindow(_hwnd);
        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    private void ShowContextMenu()
    {
        if (_mainWindow == null) return;

        var contextMenu = new ContextMenu();

        var openItem = new MenuItem
        {
            Header = "Open GManager",
            FontWeight = FontWeights.SemiBold
        };
        openItem.Click += (_, _) => RestoreMainWindow();
        contextMenu.Items.Add(openItem);

        var syncItem = new MenuItem
        {
            Header = "Sync Now"
        };
        syncItem.Click += async (_, _) =>
        {
            try { await _syncService.SyncAllNowAsync(); }
            catch { }
        };
        contextMenu.Items.Add(syncItem);

        contextMenu.Items.Add(new Separator());

        var exitItem = new MenuItem
        {
            Header = "Exit GManager"
        };
        exitItem.Click += (_, _) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        // Required so clicking outside the context menu dismisses it
        SetForegroundWindow(_hwnd);
        contextMenu.IsOpen = true;
    }

    public void ExitApplication()
    {
        _isExiting = true;
        Dispose();
        Application.Current?.Shutdown();
    }

    public void Dispose()
    {
        if (_isInitialized && _hwnd != IntPtr.Zero)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _isInitialized = false;
        }
    }
}
