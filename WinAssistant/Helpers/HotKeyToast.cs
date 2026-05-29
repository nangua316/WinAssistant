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
    private static WndProcDelegate? _wndProcDelegate;

    private const int TOAST_WIDTH = 520;
    private const int TOAST_MIN_HEIGHT = 60;
    private const int TOAST_MAX_HEIGHT = 300;
    private const int DURATION_MS = 2000;
    private const string CLASS_NAME = "WinAssistantToast";

    public static void Show(string message)
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

            SetWindowTextW(_hwnd, message);

            // Calculate required height based on text + font metrics
            var hdc = GetDC(_hwnd);
            var height = TOAST_MIN_HEIGHT;
            if (hdc != nint.Zero)
            {
                if (_hfont != nint.Zero) SelectObject(hdc, _hfont);
                var rc = new RECT { left = 0, top = 0, right = TOAST_WIDTH - 20, bottom = 0 };
                DrawTextW(hdc, message, message.Length, ref rc,
                    DT_WORDBREAK | DT_CALCRECT);
                height = Math.Max(TOAST_MIN_HEIGHT, Math.Min(rc.bottom + 32, TOAST_MAX_HEIGHT));
                ReleaseDC(_hwnd, hdc);
            }

            InvalidateRect(_hwnd, nint.Zero, true);

            // Position above the taskbar using the work area
            var workRect = new RECT();
            SystemParametersInfoW(0x0030, 0, ref workRect, 0); // SPI_GETWORKAREA
            var y = workRect.bottom - height - 8;
            var x = 16;
            SetWindowPos(_hwnd, HWND_TOPMOST,
                x, y,
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
        _hfont = CreateFontW(40, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0,
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

                        // Left accent strip (6px blue)
                        var accentRect = new RECT { left = 0, top = 0, right = 6, bottom = rc.bottom };
                        var accentBrush = CreateSolidBrush(ACCENT_COLOR);
                        FillRect(ps.hdc, ref accentRect, accentBrush);
                        DeleteObject(accentBrush);

                        // White text with custom font
                        if (_hfont != nint.Zero)
                            SelectObject(ps.hdc, _hfont);
                        SetBkMode(ps.hdc, 1); // TRANSPARENT
                        SetTextColor(ps.hdc, 0x00FFFFFF);

                        var sb = new System.Text.StringBuilder(256);
                        GetWindowTextW(hWnd, sb, sb.Capacity);
                        var text = sb.ToString();
                        if (text.Length > 0)
                        {
                            // Measure text height with word wrap
                            var textRc = new RECT { left = 16, top = 0, right = rc.right - 16, bottom = 0 };
                            DrawTextW(ps.hdc, text, text.Length, ref textRc,
                                DT_WORDBREAK | DT_CALCRECT);
                            var textH = textRc.bottom;
                            // Vertically center: offset start Y by half the leftover space
                            rc.top = (rc.bottom - textH) / 2;
                            rc.left = 16;
                            rc.right -= 16;
                            DrawTextW(ps.hdc, text, text.Length, ref rc,
                                DT_WORDBREAK | DT_CENTER);
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
    private const uint SS_CENTERIMAGE = 0x00000200;
    private const int FW_SEMIBOLD = 600;
    private const byte DEFAULT_CHARSET = 1;
    private const uint OUT_DEFAULT_PRECIS = 0;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint CLEARTYPE_QUALITY = 5;
    private const uint DEFAULT_PITCH = 0;
    private const uint FF_DONTCARE = 0;
    private const uint DT_SINGLELINE = 0x0020;
    private const uint DT_VCENTER = 0x0004;
    private const uint DT_LEFT = 0x0000;
    private const uint DT_CENTER = 0x0001;
    private const uint DT_END_ELLIPSIS = 0x8000;
    private const uint DT_WORDBREAK = 0x0010;
    private const uint DT_CALCRECT = 0x0400;
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
