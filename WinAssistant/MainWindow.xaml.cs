using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.Graphics;
using WinAssistant.Controls.Tools;

namespace WinAssistant;

public sealed partial class MainWindow : Window
{
    private nint _oldWndProc;
    private WndProcDelegate? _wndProcHook;
    private nint _trayIconHandle;
    private bool _trayIconAdded;
    private nint _hwnd;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    private const int WM_TRAY_CALLBACK = 0x0400 + 1001;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DESTROY = 0x0002;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const int GWLP_WNDPROC = -4;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint MF_STRING = 0;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_BYCOMMAND = 0;
    private const uint MF_BYPOSITION = 0x00000400;
    private const uint TPM_RETURNCMD = 0x0100;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1800, 1500));

        // Subclass the window for hotkey + tray messages
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _wndProcHook = WndProc;

        // MicaBackdrop — 与 Win11 设置页面效果一致
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        // DWM 暗色模式（SystemBackdrop 之后设置，确保 Mica 用正确主题渲染）
        App.UpdateDwmDarkMode(_hwnd);

        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcHook);
        SetLastError(0);
        _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newProc);
        if (_oldWndProc == nint.Zero && Marshal.GetLastWin32Error() != 0)
        {
            // Subclassing genuinely failed — WndProc will fall back to DefWindowProcW
            _wndProcHook = null;
        }

        InitializeTrayIcon(_hwnd);

        // Intercept close → hide to tray (如果托盘图标可用)
        AppWindow.Closing += (s, e) =>
        {
            if (_trayIconAdded)
            {
                e.Cancel = true;
                ShowWindow(_hwnd, SW_HIDE);
            }
            // 如果没有托盘图标（初始化失败），允许窗口真正关闭退出
        };

        // 主题切换时同步 MainWindow 根元素 RequestedTheme 和 DWM 暗色模式
        App.SystemThemeChanged += (_, _) =>
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
                    ? ElementTheme.Light : ElementTheme.Dark;
            }
            // 更新 DWM 暗色模式以更新 Mica 颜色
            App.UpdateDwmDarkMode(_hwnd);
            App.UpdateTitleBarTheme();
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

    public void ShowSettings()
    {
        LaunchpadOverlay.Visibility = Visibility.Collapsed;
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        SetTitleBar(AppTitleBar);
        MakeAppWindow();
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);

        // 显示窗口后再调用 WinUI 激活，确保 XAML 框架正确处理视觉树连接
        try { Activate(); } catch { }

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
                ? ElementTheme.Light : ElementTheme.Dark;
        }

        RootFrame.Navigate(typeof(MainPage));
    }

    private void MakeToolWindow()
    {
        try
        {
            var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            exStyle = (exStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
            SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
        catch { }
    }

    private void MakeAppWindow()
    {
        try
        {
            var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            exStyle = (exStyle & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
            SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
        catch { }
    }

    private void ShowContextMenu(nint hwnd)
    {
        try
        {
            var menu = CreatePopupMenu();
            const uint showItem = 1;
            const uint settingsItem = 2;
            const uint restartItem = 3;
            const uint exitItem = 4;

            InsertMenuW(menu, 0, MF_STRING | MF_BYPOSITION, showItem, "启动台");
            InsertMenuW(menu, 1, MF_STRING | MF_BYPOSITION, settingsItem, "设置");
            InsertMenuW(menu, 2, MF_SEPARATOR | MF_BYPOSITION, 0, null);
            InsertMenuW(menu, 3, MF_STRING | MF_BYPOSITION, restartItem, "重启应用");
            InsertMenuW(menu, 4, MF_SEPARATOR | MF_BYPOSITION, 0, null);
            InsertMenuW(menu, 5, MF_STRING | MF_BYPOSITION, exitItem, "退出");

            SetForegroundWindow(hwnd);
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD, pt.x, pt.y, 0, hwnd, nint.Zero);
            DestroyMenu(menu);

            if (cmd == showItem)
            {
                App.DispatcherQueue.TryEnqueue(() => App.LaunchpadWindow.Open());
            }
            else if (cmd == settingsItem)
            {
                App.DispatcherQueue.TryEnqueue(ShowSettings);
            }
            else if (cmd == restartItem)
            {
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exePath}\"",
                                UseShellExecute = true,
                                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                                CreateNoWindow = true
                            });
                        }
                        catch { }
                    }
                    ToolHostWindow.CloseAll();
                    CleanupTrayIcon();
                    Helpers.IconHelper.CleanupTempIcons();
                    App.MouseHookService.Stop();
                    App.HotKeyService.Dispose();
                    Environment.Exit(0);
                });
            }
            else if (cmd == exitItem)
            {
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    ToolHostWindow.CloseAll();
                    CleanupTrayIcon();
                    Helpers.IconHelper.CleanupTempIcons();
                    App.MouseHookService.Stop();
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
                var hotKeyId = wParam.ToInt32();
                if (hotKeyId == App.GLOBAL_HOTKEY_ID || hotKeyId == App.ALTSPACE_HOTKEY_ID)
                {
                    App.DispatcherQueue.TryEnqueue(() => App.LaunchpadWindow.Open());
                    return nint.Zero;
                }
                if (App.HotKeyService.OnWindowMessage(msg, wParam, lParam))
                    return nint.Zero;
                break;

            case WM_TRAY_CALLBACK:
                var lParamLow = (uint)lParam.ToInt32();
                if (lParamLow == WM_LBUTTONUP)
                {
                    App.DispatcherQueue.TryEnqueue(() => App.LaunchpadWindow.Open());
                }
                else if (lParamLow == WM_RBUTTONUP)
                {
                    ShowContextMenu(hWnd);
                }
                return nint.Zero;

            case WM_DESTROY:
                CleanupTrayIcon();
                break;

            case WM_SETTINGCHANGE:
                // Detect system theme change (light ↔ dark) via native Win32 broadcast
                if (lParam != nint.Zero)
                {
                    var setting = Marshal.PtrToStringUni(lParam);
                    if (string.Equals(setting, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
                    {
                        App.DispatcherQueue.TryEnqueue(() =>
                        {
                            Helpers.Logger.Log("THEME", "WM_SETTINGCHANGE ImmersiveColorSet detected");
                            var s = App.SettingsService.Load();
                            if (s.ThemeMode == 0)
                                App.RefreshTheme();
                        });
                    }
                }
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

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
    private static extern bool InsertMenuW(nint hMenu, uint uPosition, uint uFlags, uint uIDNewItem, string? lpNewItem);

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

