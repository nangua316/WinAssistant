using System.Runtime.InteropServices;

namespace WinAssistant.Helpers;

/// <summary>
/// Shared Win32 interop used by the screenshot overlay and floating image windows.
/// Keeping P/Invoke declarations, constants, and structs in one place avoids duplication
/// between the two window classes while leaving their message-handling logic separate.
/// </summary>
internal static class Win32WindowHelper
{
    // ── Window styles ──
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_TOPMOST = 0x00000008;

    // ── ShowWindow / WindowPos ──
    public const int SW_SHOW = 5;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly nint HWND_TOPMOST = (nint)(-1);

    // ── Messages ──
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_KEYDOWN = 0x0100;

    // ── Input / Cursors ──
    public const nint VK_ESCAPE = 0x1B;
    public const nint IDC_CROSS = 32515;
    public const nint IDC_HAND = 32649;
    public const nint IDC_ARROW = 32512;
    public const int HTCLIENT = 1;
    public const uint TME_LEAVE = 0x00000002;

    // ── GDI ──
    public const uint SRCCOPY = 0x00CC0020;
    public const uint PS_SOLID = 0;
    public const int HOLLOW_BRUSH = 5;

    // ── Window class / DWM ──
    public const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    public const uint CS_DROPSHADOW = 0x00020000;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    public delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowEx(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint BeginPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    public static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern nint GetCapture();

    [DllImport("user32.dll")]
    public static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll")]
    public static extern nint SetCursor(nint hCursor);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint LoadCursorFromFile(string lpFileName);

    [DllImport("user32.dll")]
    public static extern nint CreateIcon(nint hInstance, int nWidth, int nHeight,
        byte cPlanes, byte cBitsPixel, byte[] lpbANDbits, byte[] lpbXORbits);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);

    [DllImport("kernel32.dll")]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(nint hdcDest, int nXDest, int nYDest,
        int nWidth, int nHeight, nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    public static extern nint CreatePen(uint fnPenStyle, int nWidth, uint crColor);

    [DllImport("gdi32.dll")]
    public static extern nint GetStockObject(int fnObject);

    [DllImport("gdi32.dll")]
    public static extern bool Rectangle(nint hdc, int nLeftRect, int nTopRect,
        int nRightRect, int nBottomRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    public static int GET_X_LPARAM(nint lParam) => (int)(short)(lParam & 0xFFFF);
    public static int GET_Y_LPARAM(nint lParam) => (int)(short)((lParam >> 16) & 0xFFFF);
    public static int GET_WHEEL_DELTA_WPARAM(nint wParam) => (int)(short)((wParam >> 16) & 0xFFFF);
    public static uint RGB(byte r, byte g, byte b) => (uint)((b << 16) | (g << 8) | r);

    public static void RunMessageLoop(string logCategory)
    {
        MSG msg;
        int result;
        while ((result = GetMessage(out msg, nint.Zero, 0, 0)) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (result < 0)
        {
            Logger.Log(logCategory, $"GetMessage failed: {Marshal.GetLastWin32Error()}");
        }
    }
}
