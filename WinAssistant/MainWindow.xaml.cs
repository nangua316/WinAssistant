using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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

    public void ShowSettings()
    {
        // Hide launchpad if visible
        LaunchpadOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        SetTitleBar(AppTitleBar);

        // Navigate to settings page if not already loaded
        if (RootFrame.Content == null || RootFrame.Content.GetType() != typeof(MainPage))
            RootFrame.Navigate(typeof(MainPage));

        // Show as a normal app window with taskbar entry
        MakeAppWindow();
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
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
            const uint settingsItem = 2;
            const uint restartItem = 3;
            const uint exitItem = 4;

            InsertMenuW(menu, 0, MF_STRING | MF_BYPOSITION, showItem, "启动台");
            InsertMenuW(menu, 1, MF_STRING | MF_BYPOSITION, settingsItem, "设置");
            InsertMenuW(menu, 2, MF_SEPARATOR | MF_BYPOSITION, 0, null);
            InsertMenuW(menu, 3, MF_STRING | MF_BYPOSITION, restartItem, "重启");
            InsertMenuW(menu, 4, MF_SEPARATOR | MF_BYPOSITION, 0, null);
            InsertMenuW(menu, 5, MF_STRING | MF_BYPOSITION, exitItem, "退出");

            // Add 20×20 solid-color icons to menu items
            const int iconSize = 20;
            var hBmpShow = CreateMenuIcon(iconSize, MenuIconType.Show);
            var hBmpSettings = CreateMenuIcon(iconSize, MenuIconType.Settings);
            var hBmpRestart = CreateMenuIcon(iconSize, MenuIconType.Restart);
            var hBmpExit = CreateMenuIcon(iconSize, MenuIconType.Exit);

            SetMenuItemBitmaps(menu, showItem, MF_BYCOMMAND, hBmpShow, hBmpShow);
            SetMenuItemBitmaps(menu, settingsItem, MF_BYCOMMAND, hBmpSettings, hBmpSettings);
            SetMenuItemBitmaps(menu, restartItem, MF_BYCOMMAND, hBmpRestart, hBmpRestart);
            SetMenuItemBitmaps(menu, exitItem, MF_BYCOMMAND, hBmpExit, hBmpExit);

            SetForegroundWindow(hwnd);
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD, pt.x, pt.y, 0, hwnd, nint.Zero);
            DestroyMenu(menu);

            if (hBmpShow != nint.Zero) DeleteObject(hBmpShow);
            if (hBmpSettings != nint.Zero) DeleteObject(hBmpSettings);
            if (hBmpExit != nint.Zero) DeleteObject(hBmpExit);

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
                    // Launch a delayed restart via cmd.exe — waits 1s then starts a new instance.
                    // We must exit first so the singleton mutex is released.
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

    private enum MenuIconType { Show, Settings, Restart, Exit }

    /// <summary>
    /// Create a 20×20 32-bit DIBSection with alpha, drawing a simple
    /// solid-color geometric shape.  The returned HBITMAP must be freed
    /// via DeleteObject when no longer needed.
    /// </summary>
    private static nint CreateMenuIcon(int size, MenuIconType type)
    {
        var hdcScreen = GetDC(nint.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hBmp = CreateCompatibleBitmap(hdcScreen, size, size);
        if (hBmp == nint.Zero) { DeleteDC(hdcMem); ReleaseDC(nint.Zero, hdcScreen); return nint.Zero; }
        var hOld = SelectObject(hdcMem, hBmp);

        // Fill entire bitmap with menu background colour so the icon
        // blends into the menu — no alpha-channel trickery needed.
        int menuColor = GetSysColor(COLOR_MENU);
        var bgBrush = CreateSolidBrush(menuColor);
        var hOldBrush = SelectObject(hdcMem, bgBrush);
        PatBlt(hdcMem, 0, 0, size, size, PATCOPY);
        SelectObject(hdcMem, hOldBrush);
        DeleteObject(bgBrush);

        // Foreground colour (COLORREF = 0x00BBGGRR)
        uint fg = type switch
        {
            MenuIconType.Show    => 0x000078D7, // blue   B=0xD7 G=0x78 R=0x00
            MenuIconType.Settings => 0x00787878, // gray   B=0x78 G=0x78 R=0x78
            MenuIconType.Restart => 0x0000B060, // green  B=0x60 G=0xB0 R=0x00
            MenuIconType.Exit    => 0x005050C8, // red    B=0x50 G=0x50 R=0xC8
            _ => 0x00808080,
        };

        var fgBrush = CreateSolidBrush(unchecked((int)fg));
        SelectObject(hdcMem, fgBrush);
        var hNullPen = GetStockObject(NULL_PEN);
        var hOldPen = SelectObject(hdcMem, hNullPen);

        int m = 2;
        switch (type)
        {
            case MenuIconType.Show:
            {
                // 2×2 grid of filled squares — app grid / launchpad
                int cell = 7;
                int gap = 2;
                int x0 = (size - (cell * 2 + gap)) / 2; // centered
                int y0 = x0;
                PatBlt(hdcMem, x0,      y0,      cell, cell, PATCOPY);
                PatBlt(hdcMem, x0 + cell + gap, y0,      cell, cell, PATCOPY);
                PatBlt(hdcMem, x0,      y0 + cell + gap, cell, cell, PATCOPY);
                PatBlt(hdcMem, x0 + cell + gap, y0 + cell + gap, cell, cell, PATCOPY);
                break;
            }
            case MenuIconType.Settings:
                Ellipse(hdcMem, m, m, size - m, size - m);
                break;
            case MenuIconType.Restart:
            {
                // A clockwise circular arrow (arc + arrowhead)
                var hPen = CreatePen(PS_SOLID, 2, fg);
                SelectObject(hdcMem, hPen);
                int cx = size / 2, cy = size / 2, r = 6;
                Arc(hdcMem, cx - r, cy - r, cx + r, cy + r, cx + r, cy, cx, cy - r);
                // Arrowhead
                LineTo(hdcMem, cx + r, cy);
                MoveToEx(hdcMem, cx + r, cy, nint.Zero);
                LineTo(hdcMem, cx + r - 3, cy - 3);
                MoveToEx(hdcMem, cx + r, cy, nint.Zero);
                LineTo(hdcMem, cx + r - 3, cy + 3);
                DeleteObject(hPen);
                break;
            }
            case MenuIconType.Exit:
            {
                var hPen = CreatePen(PS_SOLID, 3, fg);
                SelectObject(hdcMem, hPen);
                MoveToEx(hdcMem, m, m, nint.Zero);
                LineTo(hdcMem, size - m, size - m);
                MoveToEx(hdcMem, size - m, m, nint.Zero);
                LineTo(hdcMem, m, size - m);
                DeleteObject(hPen);
                break;
            }
        }

        SelectObject(hdcMem, hOldPen);
        SelectObject(hdcMem, hOldBrush);
        DeleteObject(fgBrush);
        SelectObject(hdcMem, hOld);
        DeleteDC(hdcMem);
        ReleaseDC(nint.Zero, hdcScreen);
        return hBmp;
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

    // --- Menu icon support (GDI drawing on compatible bitmap) ---
    private const int COLOR_MENU = 4;
    private const int COLOR_HIGHLIGHT = 6;
    private const int COLOR_MENUTEXT = 7;
    private const int COLOR_HIGHLIGHTTEXT = 8;
    private const uint PATCOPY = 0x00F00021;
    private const int NULL_PEN = 8;
    private const int PS_SOLID = 0;

    [DllImport("user32.dll")]
    private static extern bool SetMenuItemBitmaps(nint hMenu, uint uPosition, uint uFlags, nint hBitmapUnchecked, nint hBitmapChecked);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("user32.dll")]
    private static extern int GetSysColor(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(nint hdc, int nXLeft, int nYLeft, int nWidth, int nHeight, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int fnObject);

    [DllImport("gdi32.dll")]
    private static extern bool Rectangle(nint hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(nint hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool Arc(nint hdc, int left, int top, int right, int bottom,
        int xStart, int yStart, int xEnd, int yEnd);

    [DllImport("gdi32.dll")]
    private static extern nint CreatePen(int fnPenStyle, int nWidth, uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool MoveToEx(nint hdc, int x, int y, nint lpPoint);

    [DllImport("gdi32.dll")]
    private static extern bool LineTo(nint hdc, int x, int y);

    #endregion
}
