using System.Runtime.InteropServices;

namespace WinAssistant.Helpers;

/// <summary>
/// Shows a small notification overlay at the bottom-left of the screen
/// when a hotkey is triggered (e.g., "打开微信" / "最小化微信").
/// </summary>
internal static class HotKeyToast
{
    private static nint _hwnd;
    private static nint _hfont;
    private static nint _hinst;
    private static nint _hicon;
    private static string _toastVerb = "";
    private static string _toastName = "";
    private static WndProcDelegate? _wndProcDelegate;

    private const int TOAST_WIDTH = 520;
    private const int TOAST_MIN_HEIGHT = 100;
    private const int TOAST_MAX_HEIGHT = 360;
    private const int ACCENT_WIDTH = 4;
    private const int ICON_SIZE = 44;
    private const int TEXT_GAP = 12;         // gap between icon and text
    private const int TEXT_PAD_LR = 22;     // left & right padding past accent
    private const int TEXT_PAD_TB = 20;     // top & bottom padding
    private const int DURATION_MS = 3000;
    private const string CLASS_NAME = "WinAssistantToast";

    /// <summary>
    /// Show a three-part toast: verb + icon + app name.
    /// When appName is empty, shows verb as plain text (no icon).
    /// </summary>
    /// <summary>
    /// 释放 GDI 资源（字体句柄），应用退出前调用。
    /// </summary>
    public static void Cleanup()
    {
        if (_hfont != nint.Zero)
        {
            DeleteObject(_hfont);
            _hfont = nint.Zero;
        }
        if (_hicon != nint.Zero)
        {
            DestroyIcon(_hicon);
            _hicon = nint.Zero;
        }
    }

    public static void Show(string verb, string appName = "", string? iconPath = null)
    {
        try
        {
            if (_hwnd == nint.Zero)
            {
                _hinst = GetModuleHandleW(null);
                if (_hinst == nint.Zero) return;
                _hwnd = CreateWindow();
                if (_hwnd == nint.Zero) return;
            }

            _toastVerb = verb;
            _toastName = appName;

            // Load app icon from exe path — try high-res first, fall back to default size
            if (_hicon != nint.Zero) { DestroyIcon(_hicon); _hicon = nint.Zero; }
            if (!string.IsNullOrEmpty(iconPath) && (File.Exists(iconPath) || Directory.Exists(iconPath)))
            {
                if (SHDefExtractIconW(iconPath, 0, 0, out nint hIcon, out _, ICON_SIZE) == 0)
                    _hicon = hIcon;
                else
                {
                    var shfi = new SHFILEINFOW();
                    SHGetFileInfoW(iconPath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON);
                    _hicon = shfi.hIcon;
                }
            }

            bool hasParts = !string.IsNullOrEmpty(_toastName);
            bool hasIcon = _hicon != nint.Zero;

            // Calculate height
            var hdc = GetDC(_hwnd);
            var height = TOAST_MIN_HEIGHT;
            if (hdc != nint.Zero)
            {
                if (_hfont != nint.Zero) SelectObject(hdc, _hfont);
                if (hasParts || hasIcon)
                {
                    height = Math.Max(TOAST_MIN_HEIGHT, ICON_SIZE + TEXT_PAD_TB * 2);
                }
                else
                {
                    int textWidth = TOAST_WIDTH - ACCENT_WIDTH - TEXT_PAD_LR * 2;
                    var rc = new RECT { left = 0, top = 0, right = textWidth, bottom = 0 };
                    DrawTextW(hdc, verb, verb.Length, ref rc, DT_WORDBREAK | DT_CALCRECT);
                    height = Math.Max(TOAST_MIN_HEIGHT,
                        Math.Min(rc.bottom + TEXT_PAD_TB * 2, TOAST_MAX_HEIGHT));
                }
                ReleaseDC(_hwnd, hdc);
            }

            InvalidateRect(_hwnd, nint.Zero, true);

            // Position above the taskbar
            var workRect = new RECT();
            SystemParametersInfoW(0x0030, 0, ref workRect, 0);
            var y = workRect.bottom - height - 8;
            SetWindowPos(_hwnd, HWND_TOPMOST, 16, y,
                TOAST_WIDTH, height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);

            KillTimer(_hwnd, 1);
            SetTimer(_hwnd, 1, DURATION_MS, nint.Zero);
        }
        catch
        {
            // silently ignore — toast is non-critical
        }
    }

    private static nint CreateWindow()
    {
        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_DROPSHADOW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = _hinst,
            hCursor = LoadCursorW(nint.Zero, IDC_ARROW),
            hbrBackground = nint.Zero, // we paint our own background
            lpszClassName = CLASS_NAME,
        };
        RegisterClassExW(ref wc);

        var hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_LAYERED,
            CLASS_NAME, "",
            WS_POPUP,
            0, 0, TOAST_WIDTH, TOAST_MIN_HEIGHT,
            nint.Zero, nint.Zero, _hinst, nint.Zero);

        if (hwnd == nint.Zero) return nint.Zero;

        // Semi-transparent (235/255 ~ 92% opacity)
        SetLayeredWindowAttributes(hwnd, 0, 235, LWA_ALPHA);

        // Rounded corners via DWM (Windows 11+)
        if (Environment.OSVersion.Version.Major >= 10)
        {
            int cornerPref = DWMWCP_ROUNDSMALL;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        }

        // Font for text
        _hfont = CreateFontW(48, 0, 0, 0, FW_NORMAL, 0, 0, 0,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, "Microsoft YaHei UI");

        return hwnd;
    }

    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            const int WM_TIMER = 0x0113;
            const int WM_PAINT = 0x000F;
            const int WM_ERASEBKGND = 0x0014;

            switch (msg)
            {
                case WM_TIMER:
                    ShowWindow(hWnd, SW_HIDE);
                    KillTimer(hWnd, 1);
                    return nint.Zero;

                case WM_ERASEBKGND:
                    return 1;

                case WM_PAINT:
                    {
                        PAINTSTRUCT ps;
                        BeginPaint(hWnd, out ps);

                        RECT rc;
                        GetClientRect(hWnd, out rc);

                        // Dark background
                        var brush = CreateSolidBrush(0x002B2B2B);
                        FillRect(ps.hdc, ref rc, brush);
                        DeleteObject(brush);

                        // Left accent strip (4px blue)
                        var accentRect = new RECT { left = 0, top = 0, right = ACCENT_WIDTH, bottom = rc.bottom };
                        var accentBrush = CreateSolidBrush(ACCENT_COLOR);
                        FillRect(ps.hdc, ref accentRect, accentBrush);
                        DeleteObject(accentBrush);

                        // Custom font
                        if (_hfont != nint.Zero)
                            SelectObject(ps.hdc, _hfont);
                        SetBkMode(ps.hdc, 1); // TRANSPARENT
                        SetTextColor(ps.hdc, 0x00FFFFFF);

                        bool hasParts = !string.IsNullOrEmpty(_toastName);
                        bool hasIcon = _hicon != nint.Zero;
                        int x = ACCENT_WIDTH + TEXT_PAD_LR;

                        // Draw icon first if available (for both parts and plain text modes)
                        int iconY = (rc.bottom - ICON_SIZE) / 2;
                        if (hasIcon)
                        {
                            DrawIconEx(ps.hdc, x, iconY, _hicon, ICON_SIZE, ICON_SIZE, 0, nint.Zero, DI_NORMAL);
                            x += ICON_SIZE + TEXT_GAP;
                        }

                        if (hasParts)
                        {
                            // Layout: [icon] 打开 微信
                            string fullText = _toastVerb + " " + _toastName;
                            var dRc = new RECT { left = x, top = 0, right = rc.right - TEXT_PAD_LR, bottom = rc.bottom };
                            DrawTextW(ps.hdc, fullText, fullText.Length, ref dRc,
                                DT_SINGLELINE | DT_VCENTER | DT_LEFT);
                        }
                        else if (_toastVerb.Length > 0)
                        {
                            // Plain text mode (with optional icon)
                            var textRc = new RECT { left = x, top = 0, right = rc.right - TEXT_PAD_LR, bottom = 0 };
                            DrawTextW(ps.hdc, _toastVerb, _toastVerb.Length, ref textRc,
                                DT_WORDBREAK | DT_CALCRECT);
                            var textH = textRc.bottom;
                            rc.top = (rc.bottom - textH) / 2;
                            rc.left = x;
                            rc.right = rc.right - TEXT_PAD_LR;
                            DrawTextW(ps.hdc, _toastVerb, _toastVerb.Length, ref rc,
                                DT_WORDBREAK | DT_LEFT);
                        }

                        EndPaint(hWnd, ref ps);
                        return nint.Zero;
                    }
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        catch
        {
            return nint.Zero;
        }
    }

    // P/Invoke
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private const int CS_DROPSHADOW = 0x00020000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int LWA_ALPHA = 0x00000002;
    private const int SW_HIDE = 0;
    private const int ACCENT_COLOR = 0x00D9904A;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUNDSMALL = 4;
    private const int FW_NORMAL = 400;
    private const byte DEFAULT_CHARSET = 1;
    private const uint OUT_DEFAULT_PRECIS = 0;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint CLEARTYPE_QUALITY = 5;
    private const uint DEFAULT_PITCH = 0;
    private const uint FF_DONTCARE = 0;
    private const uint DT_LEFT = 0x0000;
    private const uint DT_VCENTER = 0x0004;
    private const uint DT_SINGLELINE = 0x0020;
    private const uint DT_WORDBREAK = 0x0010;
    private const uint DT_CALCRECT = 0x0400;
    private const uint SHGFI_ICON = 0x00000100;
    private const uint DI_NORMAL = 0x0003;
    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint IDC_ARROW = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public nint hdc; public bool fErase; public RECT rcPaint; public bool fRestore; public bool fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(string pszIconFile, int nIcons, uint uFlags, out nint phiconLarge, out nint phiconSmall, uint nIconSize);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint SetTimer(nint hWnd, nint nIDEvent, int uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern nint KillTimer(nint hWnd, nint uIDEvent);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(nint hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll")]
    private static extern bool FillRect(nint hDC, ref RECT lprc, nint hbr);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(nint hdc, int crColor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint hdc, string lpchText, int cchText, ref RECT lprc, uint uFormat);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, byte iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);
}
