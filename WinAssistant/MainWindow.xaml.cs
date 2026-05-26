using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace WinAssistant;

public sealed partial class MainWindow : Window
{
    private nint _oldWndProc;
    private WndProcDelegate? _wndProcHook;
    private nint _trayIconHandle;
    private bool _trayIconAdded;
    private nint _hwnd;
    private bool _wasInTray;
    private Pages.LaunchpadPage? _launchpadPage;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    private const int WM_TRAY_CALLBACK = 0x0400 + 1001;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DESTROY = 0x0002;
    private const int GWLP_WNDPROC = -4;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint MF_STRING = 0;
    private const uint TPM_RETURNCMD = 0x0100;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1500, 1500));

        RootFrame.Navigate(typeof(MainPage));

        // Subclass the window for hotkey + tray messages
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _wndProcHook = WndProc;
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcHook);
        SetLastError(0);
        _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newProc);
        if (_oldWndProc == nint.Zero && Marshal.GetLastWin32Error() != 0)
        {
            // Subclassing genuinely failed — WndProc will fall back to DefWindowProcW
            _wndProcHook = null;
        }

        InitializeTrayIcon(_hwnd);

        // Intercept close → hide to tray
        AppWindow.Closing += (s, e) =>
        {
            e.Cancel = true;
            _wasInTray = true;
            // Set toolwindow before hiding so next SW_SHOW doesn't create taskbar entry.
            MakeToolWindow();
            ShowWindow(_hwnd, SW_HIDE);
        };
    }

    private void InitializeTrayIcon(nint hwnd)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _trayIconHandle = LoadImageW(nint.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (_trayIconHandle == nint.Zero)
            _trayIconHandle = LoadIcon(nint.Zero, (nint)32512); // IDI_APPLICATION fallback

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = 100,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon = _trayIconHandle,
            szTip = "WinAssistant - 全局快捷键工具"
        };

        _trayIconAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
        Debug.WriteLine($"Tray icon added: {_trayIconAdded}, icon handle: {_trayIconHandle}");
    }

    private void CleanupTrayIcon()
    {
        if (_trayIconAdded)
        {
            var nid = new NOTIFYICONDATAW { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = _hwnd, uID = 100 };
            Shell_NotifyIconW(NIM_DELETE, ref nid);
            _trayIconAdded = false;
        }
        if (_trayIconHandle != nint.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = nint.Zero;
        }
    }

    public void ShowLaunchpad()
    {
        _wasInTray = !IsWindowVisible(_hwnd);

        if (_wasInTray)
        {
            // Window was hidden (tray mode). Set toolwindow so show creates no taskbar entry.
            MakeToolWindow();
            // SW_HIDE + SW_SHOW forces taskbar to re-evaluate extended styles.
            ShowWindow(_hwnd, SW_HIDE);
            ShowWindow(_hwnd, SW_SHOW);
        }
        // If window was visible, its existing taskbar entry stays — no new one appears.

        // Create LaunchpadPage if first time.
        if (_launchpadPage == null)
        {
            _launchpadPage = new Pages.LaunchpadPage();
            _launchpadPage.CloseRequested += (_, _) => App.DispatcherQueue.TryEnqueue(HideLaunchpad);
            LaunchpadOverlay.Children.Add(_launchpadPage);
        }

        // Full-screen to cover the taskbar.
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        // Re-assert toolwindow after presenter changes.
        MakeToolWindow();

        // Show overlay and activate.
        LaunchpadOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        _launchpadPage.Activate();

        // Bring to foreground for keyboard input.
        SetForegroundWindow(_hwnd);

        // Final re-assert after SetForegroundWindow.
        MakeToolWindow();

        // FullScreenPresenter may create a brief taskbar entry.
        // Flush it after a short delay so the user never sees it.
        _ = Task.Delay(120).ContinueWith(_ =>
            App.DispatcherQueue.TryEnqueue(MakeToolWindow));
    }

    private void HideLaunchpad()
    {
        LaunchpadOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

        // Restore from fullscreen.
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);

        // Reattach the custom title bar.
        SetTitleBar(AppTitleBar);

        if (_wasInTray)
        {
            // Go back to tray — no taskbar entry because TOOLWINDOW was set.
            ShowWindow(_hwnd, SW_HIDE);
        }
        else
        {
            // Restore normal app window style.
            MakeAppWindow();
        }
    }

    private void MakeToolWindow()
    {
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle = (exStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
    }

    private void MakeAppWindow()
    {
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle = (exStyle & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
    }

    private void ShowContextMenu(nint hwnd)
    {
        try
        {
            var menu = CreatePopupMenu();
            const uint showItem = 1;
            const uint exitItem = 2;

            InsertMenuW(menu, 0, MF_STRING, showItem, "显示窗口");
            InsertMenuW(menu, 1, MF_STRING, exitItem, "退出");

            SetForegroundWindow(hwnd);
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD, pt.x, pt.y, 0, hwnd, nint.Zero);
            DestroyMenu(menu);

            if (cmd == showItem)
            {
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    MakeAppWindow();
                    ShowWindow(hwnd, SW_SHOW);
                    SetForegroundWindow(hwnd);
                });
            }
            else if (cmd == exitItem)
            {
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    CleanupTrayIcon();
                    Helpers.IconHelper.CleanupTempIcons();
                    App.WinKeyInterceptor.Stop();
                    App.HotKeyService.Dispose();
                    Environment.Exit(0);
                });
            }
        }
        catch
        {
            // Menu display failed silently
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (_wndProcHook == null) return DefWindowProcW(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_HOTKEY:
                if (App.HotKeyService.OnWindowMessage(msg, wParam, lParam))
                    return nint.Zero;
                break;

            case WM_TRAY_CALLBACK:
                var lParamLow = (uint)lParam.ToInt32();
                if (lParamLow == WM_LBUTTONUP)
                {
                    App.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (IsWindowVisible(hWnd))
                        {
                            MakeToolWindow();
                            ShowWindow(hWnd, SW_HIDE);
                        }
                        else
                        {
                            MakeAppWindow();
                            ShowWindow(hWnd, SW_SHOW);
                            SetForegroundWindow(hWnd);
                        }
                    });
                }
                else if (lParamLow == WM_RBUTTONUP)
                {
                    ShowContextMenu(hWnd);
                }
                return nint.Zero;

            case WM_DESTROY:
                CleanupTrayIcon();
                break;
        }

        return _oldWndProc != nint.Zero
            ? CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam)
            : DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    #region Win32 P/Invoke

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint cmd, ref NOTIFYICONDATAW nid);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(nint hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenuW(nint hMenu, uint uPosition, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern nint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    #endregion
}
