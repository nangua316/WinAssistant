using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace WinAssistant.Helpers;

/// <summary>
/// 悬浮截图入口：全屏框选区域，截图结果以无边框窗口悬浮显示。
/// 双击图片关闭，滚轮缩放，不保存文件。
/// </summary>
internal static class ScreenshotOverlay
{
    public static void Start()
    {
        Logger.Log("ScreenshotOverlay", "Start requested");
        App.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                Logger.Log("ScreenshotOverlay", "Starting Win32 screenshot overlay");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                var bounds = display.OuterBounds;
                Logger.Log("ScreenshotOverlay", $"Display bounds={bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");

                var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                try
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0,
                            new System.Drawing.Size(bounds.Width, bounds.Height));
                    }
                    Logger.Log("ScreenshotOverlay", $"Captured bitmap {bmp.Width}x{bmp.Height}");

                    _ = new ScreenshotOverlayWin32(bmp, bounds.X, bounds.Y, (cropped, x, y) =>
                    {
                        App.DispatcherQueue.TryEnqueue(() =>
                        {
                            try { new FloatingImageWin32(cropped, x, y); }
                            catch (Exception ex)
                            {
                                Logger.Log("ScreenshotOverlay", $"Open floating window failed: {ex.Message}");
                                cropped.Dispose();
                            }
                        });
                    });
                }
                catch (Exception ex)
                {
                    bmp.Dispose();
                    Logger.Log("ScreenshotOverlay", $"Start failed: {ex}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("ScreenshotOverlay", $"Start failed: {ex}");
            }
        });
    }
}

/// <summary>
/// 纯 Win32 全屏截图覆盖层。WinUI 3 辅助窗口在这个项目里始终无法显示，
/// 而 Win32 窗口是成熟截图工具（如 Snipping Tool、ShareX）使用的可靠方案。
/// </summary>
internal sealed class ScreenshotOverlayWin32
{
    // Keep the WndProc delegate alive. The native window class holds a function pointer
    // to it; if the delegate is GC'd the app will crash as soon as a message is dispatched.
    private static readonly WndProcDelegate _wndProc = WndProcStatic;

    // Only one overlay can be active at a time, so a single static reference is enough
    // and avoids the pointer-truncation pitfalls of GWL_USERDATA on 64-bit.
    private static ScreenshotOverlayWin32? _activeInstance;

    private readonly System.Drawing.Bitmap _fullBitmap;
    private readonly nint _hBitmap;
    private readonly int _screenX;
    private readonly int _screenY;
    private readonly Action<System.Drawing.Bitmap, int, int> _onSelected;

    private nint _hwnd;
    private bool _selecting;
    private POINT _start;
    private POINT _end;

    private const string CLASS_NAME = "WinAssistantScreenshotOverlay";

    public ScreenshotOverlayWin32(System.Drawing.Bitmap fullBitmap, int screenX, int screenY,
        Action<System.Drawing.Bitmap, int, int> onSelected)
    {
        _fullBitmap = fullBitmap;
        _screenX = screenX;
        _screenY = screenY;
        _onSelected = onSelected;

        Logger.Log("ScreenshotOverlayWin32", "Getting HBITMAP");
        _hBitmap = fullBitmap.GetHbitmap();
        Logger.Log("ScreenshotOverlayWin32", $"HBITMAP={_hBitmap}");

        try
        {
            Logger.Log("ScreenshotOverlayWin32", "Registering window class");
            var wndClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandle(null),
                lpszClassName = CLASS_NAME,
                hCursor = LoadCursor(nint.Zero, IDC_CROSS),
                hbrBackground = nint.Zero
            };

            ushort atom = RegisterClassEx(ref wndClass);
            int err = Marshal.GetLastWin32Error();
            Logger.Log("ScreenshotOverlayWin32", $"RegisterClassEx atom={atom} lastErr={err}");
            if (atom == 0 && err != ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new InvalidOperationException($"RegisterClassEx failed: {err}");
            }

            Logger.Log("ScreenshotOverlayWin32", "Creating window");
            _hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
                CLASS_NAME,
                "悬浮截图",
                WS_POPUP,
                screenX, screenY, fullBitmap.Width, fullBitmap.Height,
                nint.Zero, nint.Zero, GetModuleHandle(null), nint.Zero);

            err = Marshal.GetLastWin32Error();
            Logger.Log("ScreenshotOverlayWin32", $"CreateWindowEx hwnd={_hwnd} lastErr={err}");
            if (_hwnd == nint.Zero)
            {
                throw new InvalidOperationException($"CreateWindowEx failed: {err}");
            }

            _activeInstance = this;

            Logger.Log("ScreenshotOverlayWin32", "Showing window");
            ShowWindow(_hwnd, SW_SHOW);
            UpdateWindow(_hwnd);
            SetForegroundWindow(_hwnd);
            Logger.Log("ScreenshotOverlayWin32", "Window shown");
        }
        catch
        {
            DeleteObject(_hBitmap);
            _fullBitmap.Dispose();
            throw;
        }
    }

    private static nint WndProcStatic(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = _activeInstance;
        if (self == null || self._hwnd != hwnd)
            return DefWindowProc(hwnd, msg, wParam, lParam);

        return self.WndProc(hwnd, msg, wParam, lParam);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                OnPaint();
                return 0;

            case WM_ERASEBKGND:
                return 1;

            case WM_LBUTTONDOWN:
                _selecting = true;
                _start = new POINT(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                _end = _start;
                SetCapture(hwnd);
                InvalidateRect(hwnd, nint.Zero, false);
                return 0;

            case WM_MOUSEMOVE:
                if (_selecting)
                {
                    var oldEnd = _end;
                    _end = new POINT(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                    InvalidateSelectionRect(oldEnd, _end);
                }
                return 0;

            case WM_LBUTTONUP:
                if (_selecting)
                {
                    _selecting = false;
                    ReleaseCapture();
                    _end = new POINT(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                    FinishSelection();
                }
                return 0;

            case WM_RBUTTONDOWN:
                DestroyWindow(hwnd);
                return 0;

            case WM_KEYDOWN:
                if (wParam == VK_ESCAPE)
                    DestroyWindow(hwnd);
                return 0;

            case WM_DESTROY:
                Cleanup();
                return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnPaint()
    {
        var ps = new PAINTSTRUCT();
        nint hdc = BeginPaint(_hwnd, ref ps);
        if (hdc == nint.Zero) return;

        GetClientRect(_hwnd, out var clientRect);
        int cw = clientRect.right - clientRect.left;
        int ch = clientRect.bottom - clientRect.top;

        // Double-buffer the entire frame: draw background + rectangle + label into a memory
        // bitmap first, then blit the completed frame to the screen in one go.  This avoids
        // tearing/flicker that happens when the display refreshes between drawing steps.
        nint memDc = nint.Zero;
        nint memBitmap = nint.Zero;
        nint oldBitmap = nint.Zero;
        try
        {
            memDc = CreateCompatibleDC(hdc);
            memBitmap = CreateCompatibleBitmap(hdc, cw, ch);
            oldBitmap = SelectObject(memDc, memBitmap);

            var bmpDc = CreateCompatibleDC(hdc);
            var oldBmp = SelectObject(bmpDc, _hBitmap);
            BitBlt(memDc, 0, 0, cw, ch, bmpDc, 0, 0, SRCCOPY);
            SelectObject(bmpDc, oldBmp);
            DeleteDC(bmpDc);

            if (_selecting && (_end.X != _start.X || _end.Y != _start.Y))
            {
                int x = Math.Min(_start.X, _end.X);
                int y = Math.Min(_start.Y, _end.Y);
                int w = Math.Abs(_end.X - _start.X);
                int h = Math.Abs(_end.Y - _start.Y);

                if (w > 2 && h > 2)
                {
                    // Draw a black outline behind the bright red border so the selection is visible
                    // on any background (similar to WeChat / Snipping Tool).
                    var outlinePen = CreatePen(PS_SOLID, 5, RGB(0, 0, 0));
                    var oldOutlinePen = SelectObject(memDc, outlinePen);
                    var brush = GetStockObject(HOLLOW_BRUSH);
                    var oldBrush = SelectObject(memDc, brush);
                    Rectangle(memDc, x, y, x + w, y + h);

                    var pen = CreatePen(PS_SOLID, 4, RGB(255, 40, 40));
                    SelectObject(memDc, pen);
                    Rectangle(memDc, x, y, x + w, y + h);

                    SelectObject(memDc, oldOutlinePen);
                    SelectObject(memDc, oldBrush);
                    DeleteObject(outlinePen);
                    DeleteObject(pen);

                    DrawSizeLabel(memDc, x, y, w, h);
                }
            }

            BitBlt(hdc, 0, 0, cw, ch, memDc, 0, 0, SRCCOPY);
        }
        finally
        {
            if (oldBitmap != nint.Zero) SelectObject(memDc, oldBitmap);
            if (memBitmap != nint.Zero) DeleteObject(memBitmap);
            if (memDc != nint.Zero) DeleteDC(memDc);
            EndPaint(_hwnd, ref ps);
        }
    }

    private static void DrawSizeLabel(nint hdc, int x, int y, int w, int h)
    {
        try
        {
            var text = $"{w} x {h}";
            using var font = new System.Drawing.Font("Microsoft YaHei UI", 11);

            // Render text + shadow into a small transparent bitmap once. On subsequent frames
            // only the bitmap position changes, so the crisp single-bit pixels are not
            // re-rasterized and the label no longer shimmers as the selection moves.
            using var labelBmp = RenderLabelBitmap(text, font);
            int padding = 4;
            int labelW = labelBmp.Width + padding * 2;
            int labelH = labelBmp.Height + padding * 2;

            // Keep the label inside the selection rect so it is always covered by the
            // invalidation region and never leaves trails outside the selection.
            int labelX = x + 6;
            int labelY = y + 6;
            if (labelX + labelW > x + w) labelX = x + w - labelW;
            if (labelY + labelH > y + h) labelY = y + h - labelH;
            if (labelX < x) labelX = x;
            if (labelY < y) labelY = y;

            using var g = System.Drawing.Graphics.FromHdc(hdc);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(labelBmp, labelX + padding, labelY + padding);
        }
        catch (Exception ex)
        {
            Logger.Log("ScreenshotOverlayWin32", $"DrawSizeLabel failed: {ex.Message}");
        }
    }

    private static System.Drawing.Bitmap RenderLabelBitmap(string text, System.Drawing.Font font)
    {
        var size = MeasureText(text, font);
        int bmpW = (int)Math.Ceiling(size.Width) + 2;
        int bmpH = (int)Math.Ceiling(size.Height) + 2;

        var bmp = new System.Drawing.Bitmap(bmpW, bmpH, PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            // WeChat-style crisp label: no anti-aliased edges.  Single-bit pixels are either
            // fully opaque or fully transparent, so moving the label over the changing
            // screenshot background never produces shimmering semi-transparent edge blends.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(System.Drawing.Color.Transparent);

            // 1 px black shadow behind white text for readability without a solid label background.
            using var shadowBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            g.DrawString(text, font, shadowBrush, 1, 1);

            using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.DrawString(text, font, textBrush, 0, 0);
        }
        return bmp;
    }

    private static System.Drawing.SizeF MeasureText(string text, System.Drawing.Font font)
    {
        using var temp = new System.Drawing.Bitmap(1, 1);
        using var g = System.Drawing.Graphics.FromImage(temp);
        // Match the rendering mode used for the actual label bitmap.
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        return g.MeasureString(text, font);
    }

    private void FinishSelection()
    {
        int x = Math.Min(_start.X, _end.X);
        int y = Math.Min(_start.Y, _end.Y);
        int w = Math.Abs(_end.X - _start.X);
        int h = Math.Abs(_end.Y - _start.Y);

        if (w < 10 || h < 10)
        {
            DestroyWindow(_hwnd);
            return;
        }

        x = Math.Clamp(x, 0, _fullBitmap.Width - 1);
        y = Math.Clamp(y, 0, _fullBitmap.Height - 1);
        w = Math.Clamp(w, 1, _fullBitmap.Width - x);
        h = Math.Clamp(h, 1, _fullBitmap.Height - y);

        try
        {
            var cropped = _fullBitmap.Clone(
                new System.Drawing.Rectangle(x, y, w, h),
                _fullBitmap.PixelFormat);
            _onSelected(cropped, _screenX + x, _screenY + y);
        }
        catch (Exception ex)
        {
            Logger.Log("ScreenshotOverlayWin32", $"Clone failed: {ex.Message}");
        }

        DestroyWindow(_hwnd);
    }

    private void InvalidateSelectionRect(POINT oldEnd, POINT newEnd)
    {
        int x1 = Math.Min(_start.X, oldEnd.X) - 6;
        int y1 = Math.Min(_start.Y, oldEnd.Y) - 6;
        int x2 = Math.Max(_start.X, oldEnd.X) + 6;
        int y2 = Math.Max(_start.Y, oldEnd.Y) + 6;

        int x3 = Math.Min(_start.X, newEnd.X) - 6;
        int y3 = Math.Min(_start.Y, newEnd.Y) - 6;
        int x4 = Math.Max(_start.X, newEnd.X) + 6;
        int y4 = Math.Max(_start.Y, newEnd.Y) + 6;

        int left = Math.Min(x1, x3);
        int top = Math.Min(y1, y3);
        int right = Math.Max(x2, x4);
        int bottom = Math.Max(y2, y4);

        var rect = new RECT { left = left, top = top, right = right, bottom = bottom };
        nint pRect = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(rect, pRect, false);
            InvalidateRect(_hwnd, pRect, false);
        }
        finally
        {
            Marshal.FreeHGlobal(pRect);
        }
    }

    private void Cleanup()
    {
        try
        {
            _activeInstance = null;
            DeleteObject(_hBitmap);
            _fullBitmap.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log("ScreenshotOverlayWin32", $"Cleanup failed: {ex.Message}");
        }
    }

    // ── Win32 ──

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const int SW_SHOW = 5;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_KEYDOWN = 0x0100;
    private const nint VK_ESCAPE = 0x1B;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint PS_SOLID = 0;
    private const int HOLLOW_BRUSH = 5;
    private const nint IDC_CROSS = 32515;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
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
    private struct PAINTSTRUCT
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
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int nXDest, int nYDest,
        int nWidth, int nHeight, nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern nint CreatePen(uint fnPenStyle, int nWidth, uint crColor);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int fnObject);

    [DllImport("gdi32.dll")]
    private static extern bool Rectangle(nint hdc, int nLeftRect, int nTopRect,
        int nRightRect, int nBottomRect);

    private static int GET_X_LPARAM(nint lParam) => (int)(short)(lParam & 0xFFFF);
    private static int GET_Y_LPARAM(nint lParam) => (int)(short)((lParam >> 16) & 0xFFFF);
    private static uint RGB(byte r, byte g, byte b) => (uint)((b << 16) | (g << 8) | r);
}

/// <summary>
/// 纯 Win32 悬浮图片窗口：拖动移动，滚轮缩放，双击关闭，右上角 X 按钮，
/// 右下角复制按钮（点击关闭并复制图片到剪贴板）。
/// 用 Win32 替代 WinUI 3 Window，避免缩放时窗口resize带来的抖动。
/// </summary>
internal sealed class FloatingImageWin32
{
    private static readonly WndProcDelegate _wndProc = WndProcStatic;
    // Multiple floating images can exist at the same time, so map each HWND to its instance
    // instead of keeping a single active reference.
    private static readonly Dictionary<nint, FloatingImageWin32> _instances = new();

    private readonly System.Drawing.Bitmap _bitmap;
    private readonly nint _hBitmap;
    private readonly int _baseW;
    private readonly int _baseH;

    private nint _hwnd;
    private double _zoom = 1.0;

    private bool _dragging;
    private bool _hasCapture;
    private POINT _dragStartCursor;
    private RECT _dragStartRect;
    private DateTimeOffset _firstClickTime;
    private bool _firstClickPending;
    private bool _closeButtonHover;
    private bool _closeButtonPressed;
    private bool _copyButtonHover;
    private bool _copyButtonPressed;
    private bool _copyRequested;

    private const string CLASS_NAME = "WinAssistantFloatingImage";

    public FloatingImageWin32(System.Drawing.Bitmap bitmap, int screenX, int screenY)
    {
        // GDI+ respects alpha; screen captures may carry alpha=0, which would draw
        // transparently. Convert to 24bppRgb to ensure the image always renders opaque.
        _bitmap = new System.Drawing.Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(_bitmap))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(bitmap, 0, 0);
        }
        bitmap.Dispose();

        _baseW = _bitmap.Width;
        _baseH = _bitmap.Height;

        _hBitmap = _bitmap.GetHbitmap();

        try
        {
            var wndClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DROPSHADOW,
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandle(null),
                lpszClassName = CLASS_NAME,
                hCursor = LoadCursor(nint.Zero, IDC_ARROW),
                hbrBackground = nint.Zero
            };

            ushort atom = RegisterClassEx(ref wndClass);
            if (atom == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

            var (w, h) = CalcInitialSize();
            _zoom = w / (double)_baseW;

            _hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
                CLASS_NAME,
                "悬浮图片",
                WS_POPUP,
                screenX, screenY, w, h,
                nint.Zero, nint.Zero, GetModuleHandle(null), nint.Zero);

            if (_hwnd == nint.Zero)
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

            _instances[_hwnd] = this;

            // Round corners so the window gets the modern DWM shadow back.
            var cornerPref = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            ShowWindow(_hwnd, SW_SHOW);
            UpdateWindow(_hwnd);
            SetForegroundWindow(_hwnd);
        }
        catch
        {
            DeleteObject(_hBitmap);
            _bitmap.Dispose();
            throw;
        }
    }

    private (int w, int h) CalcInitialSize()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            var display = DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd),
                DisplayAreaFallback.Primary);
            var area = display.WorkArea;

            int maxW = (int)(area.Width * 0.9);
            int maxH = (int)(area.Height * 0.9);

            double scale = 1.0;
            if (_baseW > maxW || _baseH > maxH)
                scale = Math.Min((double)maxW / _baseW, (double)maxH / _baseH);

            int w = Math.Max(92, (int)(_baseW * scale));
            int h = Math.Max(92, (int)(_baseH * scale));
            return (w, h);
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"CalcInitialSize failed: {ex.Message}");
            return (_baseW, _baseH);
        }
    }

    private static nint WndProcStatic(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (!_instances.TryGetValue(hwnd, out var self))
            return DefWindowProc(hwnd, msg, wParam, lParam);
        return self.WndProc(hwnd, msg, wParam, lParam);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                OnPaint();
                return 0;

            case WM_ERASEBKGND:
                return 1;

            case WM_LBUTTONDOWN:
            {
                int x = GET_X_LPARAM(lParam);
                int y = GET_Y_LPARAM(lParam);
                GetClientRect(hwnd, out var cr);
                int cw = cr.right - cr.left;
                int ch = cr.bottom - cr.top;

                if (HitTestCloseButton(x, y, cw, ch))
                {
                    _closeButtonPressed = true;
                    _closeButtonHover = true;
                    InvalidateRect(hwnd, nint.Zero, false);
                    return 0;
                }

                if (HitTestCopyButton(x, y, cw, ch))
                {
                    _copyButtonPressed = true;
                    _copyButtonHover = true;
                    InvalidateRect(hwnd, nint.Zero, false);
                    return 0;
                }

                _dragging = false;
                _hasCapture = true;
                GetCursorPos(out _dragStartCursor);
                GetWindowRect(hwnd, out _dragStartRect);
                SetCapture(hwnd);
                return 0;
            }

            case WM_MOUSEMOVE:
            {
                int mx = GET_X_LPARAM(lParam);
                int my = GET_Y_LPARAM(lParam);
                GetClientRect(hwnd, out var cr);
                int cw = cr.right - cr.left;
                int ch = cr.bottom - cr.top;

                bool closeHovering = HitTestCloseButton(mx, my, cw, ch);
                if (closeHovering != _closeButtonHover)
                {
                    _closeButtonHover = closeHovering;
                    InvalidateRect(hwnd, nint.Zero, false);
                }
                if (_closeButtonPressed && !closeHovering)
                {
                    _closeButtonPressed = false;
                    InvalidateRect(hwnd, nint.Zero, false);
                }

                bool copyHovering = HitTestCopyButton(mx, my, cw, ch);
                if (copyHovering != _copyButtonHover)
                {
                    _copyButtonHover = copyHovering;
                    InvalidateRect(hwnd, nint.Zero, false);
                }
                if (_copyButtonPressed && !copyHovering)
                {
                    _copyButtonPressed = false;
                    InvalidateRect(hwnd, nint.Zero, false);
                }

                if (_hasCapture)
                {
                    GetCursorPos(out var cursor);
                    int dx = cursor.X - _dragStartCursor.X;
                    int dy = cursor.Y - _dragStartCursor.Y;
                    if (!_dragging && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                    {
                        _dragging = true;
                        _firstClickPending = false;
                    }
                    if (_dragging)
                    {
                        SetWindowPos(hwnd, HWND_TOPMOST,
                            _dragStartRect.left + dx, _dragStartRect.top + dy, 0, 0,
                            SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                    }
                }
                return 0;
            }

            case WM_LBUTTONUP:
            {
                _hasCapture = false;
                ReleaseCapture();

                int ux = GET_X_LPARAM(lParam);
                int uy = GET_Y_LPARAM(lParam);
                GetClientRect(hwnd, out var cr);
                int cw = cr.right - cr.left;
                int ch = cr.bottom - cr.top;

                if (_closeButtonPressed)
                {
                    _closeButtonPressed = false;
                    if (HitTestCloseButton(ux, uy, cw, ch))
                    {
                        DestroyWindow(hwnd);
                        return 0;
                    }
                    InvalidateRect(hwnd, nint.Zero, false);
                }

                if (_copyButtonPressed)
                {
                    _copyButtonPressed = false;
                    if (HitTestCopyButton(ux, uy, cw, ch))
                    {
                        _copyRequested = true;
                        DestroyWindow(hwnd);
                        return 0;
                    }
                    InvalidateRect(hwnd, nint.Zero, false);
                }

                if (_dragging)
                {
                    _dragging = false;
                    return 0;
                }

                var now = DateTimeOffset.UtcNow;
                if (_firstClickPending && (now - _firstClickTime).TotalMilliseconds < 500)
                {
                    DestroyWindow(hwnd);
                    return 0;
                }
                _firstClickPending = true;
                _firstClickTime = now;
                return 0;
            }

            case WM_SETCURSOR:
            {
                uint hitTest = (uint)(lParam & 0xFFFF);
                if (hitTest == HTCLIENT)
                {
                    GetCursorPos(out var pt);
                    ScreenToClient(hwnd, ref pt);
                    GetClientRect(hwnd, out var cr);
                    int cw = cr.right - cr.left;
                    int ch = cr.bottom - cr.top;
                    if (HitTestCloseButton(pt.X, pt.Y, cw, ch) ||
                        HitTestCopyButton(pt.X, pt.Y, cw, ch))
                    {
                        SetCursor(LoadCursor(nint.Zero, IDC_HAND));
                        return (nint)1;
                    }
                }
                break;
            }

            case WM_MOUSEWHEEL:
                OnWheel(GET_WHEEL_DELTA_WPARAM(wParam));
                return 0;

            case WM_KEYDOWN:
                if (wParam == VK_ESCAPE)
                    DestroyWindow(hwnd);
                return 0;

            case WM_DESTROY:
                Cleanup();
                return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnPaint()
    {
        var ps = new PAINTSTRUCT();
        nint hdc = BeginPaint(_hwnd, ref ps);
        if (hdc == nint.Zero) return;

        GetClientRect(_hwnd, out var clientRect);
        int w = clientRect.right - clientRect.left;
        int h = clientRect.bottom - clientRect.top;

        try
        {
            // Double-buffer: draw to a memory bitmap first, then blit to the window.
            using var backBuffer = new System.Drawing.Bitmap(w, h, PixelFormat.Format24bppRgb);
            using var g = System.Drawing.Graphics.FromImage(backBuffer);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_bitmap, 0, 0, w, h);

            // Draw the semi-transparent close button on top of the image.
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            DrawCloseButton(g, w, h);
            DrawCopyButton(g, w, h);

            using var screenG = System.Drawing.Graphics.FromHdc(hdc);
            screenG.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            screenG.DrawImageUnscaled(backBuffer, 0, 0);
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"OnPaint failed: {ex.Message}");
        }
        finally
        {
            EndPaint(_hwnd, ref ps);
        }
    }

    private void OnWheel(int delta)
    {
        var factor = delta > 0 ? 1.1 : 0.9;
        var newZoom = Math.Clamp(_zoom * factor, 0.2, 5.0);

        if (!GetWindowRect(_hwnd, out var rect)) return;

        int oldW = rect.right - rect.left;
        int oldH = rect.bottom - rect.top;
        int newW = Math.Max(92, (int)(_baseW * newZoom));
        int newH = Math.Max(92, (int)(_baseH * newZoom));

        GetCursorPos(out var cursor);
        double rx = (cursor.X - rect.left) / (double)oldW;
        double ry = (cursor.Y - rect.top) / (double)oldH;
        int newX = rect.left + (int)((oldW - newW) * rx);
        int newY = rect.top + (int)((oldH - newH) * ry);

        _zoom = newZoom;
        SetWindowPos(_hwnd, HWND_TOPMOST, newX, newY, newW, newH,
            SWP_NOACTIVATE | SWP_NOZORDER);
        InvalidateRect(_hwnd, nint.Zero, false);
    }

    private static System.Drawing.Rectangle GetCloseButtonRect(int windowW, int windowH)
    {
        const int size = 36;
        const int margin = 10;
        return new System.Drawing.Rectangle(windowW - size - margin, margin, size, size);
    }

    private static bool HitTestCloseButton(int x, int y, int windowW, int windowH)
    {
        return GetCloseButtonRect(windowW, windowH).Contains(x, y);
    }

    private void DrawCloseButton(System.Drawing.Graphics g, int windowW, int windowH)
    {
        try
        {
            var rect = GetCloseButtonRect(windowW, windowH);
            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int alpha = _closeButtonPressed ? 140 : (_closeButtonHover ? 105 : 70);
            using var bgBrush = new System.Drawing.SolidBrush(
                System.Drawing.Color.FromArgb(alpha, 0, 0, 0));
            g.FillEllipse(bgBrush, rect);

            const int padding = 10;
            using var shadowPen = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(160, 0, 0, 0), 2);
            g.DrawLine(shadowPen, rect.Left + padding + 1, rect.Top + padding + 1,
                rect.Right - padding + 1, rect.Bottom - padding + 1);
            g.DrawLine(shadowPen, rect.Right - padding + 1, rect.Top + padding + 1,
                rect.Left + padding + 1, rect.Bottom - padding + 1);

            using var xPen = new System.Drawing.Pen(System.Drawing.Color.White, 2);
            g.DrawLine(xPen, rect.Left + padding, rect.Top + padding,
                rect.Right - padding, rect.Bottom - padding);
            g.DrawLine(xPen, rect.Right - padding, rect.Top + padding,
                rect.Left + padding, rect.Bottom - padding);

            g.SmoothingMode = oldSmoothing;
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"DrawCloseButton failed: {ex.Message}");
        }
    }

    private static System.Drawing.Rectangle GetCopyButtonRect(int windowW, int windowH)
    {
        const int size = 36;
        const int margin = 10;
        return new System.Drawing.Rectangle(windowW - size - margin, windowH - size - margin, size, size);
    }

    private static bool HitTestCopyButton(int x, int y, int windowW, int windowH)
    {
        return GetCopyButtonRect(windowW, windowH).Contains(x, y);
    }

    private void DrawCopyButton(System.Drawing.Graphics g, int windowW, int windowH)
    {
        try
        {
            var rect = GetCopyButtonRect(windowW, windowH);
            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int alpha = _copyButtonPressed ? 140 : (_copyButtonHover ? 105 : 70);
            using var bgBrush = new System.Drawing.SolidBrush(
                System.Drawing.Color.FromArgb(alpha, 0, 0, 0));
            g.FillEllipse(bgBrush, rect);

            const int padding = 8;
            int inner = rect.Width - padding * 2;
            int offset = inner / 4;

            using var shadowPen = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(160, 0, 0, 0), 2);
            DrawCopyIcon(g, shadowPen, rect.Left + padding + 1, rect.Top + padding + 1, inner, offset);

            using var iconPen = new System.Drawing.Pen(System.Drawing.Color.White, 2);
            DrawCopyIcon(g, iconPen, rect.Left + padding, rect.Top + padding, inner, offset);

            g.SmoothingMode = oldSmoothing;
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"DrawCopyButton failed: {ex.Message}");
        }
    }

    private static void DrawCopyIcon(System.Drawing.Graphics g, System.Drawing.Pen pen,
        int x, int y, int size, int offset)
    {
        const int radius = 3;
        using var backPath = new System.Drawing.Drawing2D.GraphicsPath();
        AddRoundedRectangle(backPath, x + offset, y, size - offset, size - offset, radius);
        g.DrawPath(pen, backPath);

        using var frontPath = new System.Drawing.Drawing2D.GraphicsPath();
        AddRoundedRectangle(frontPath, x, y + offset, size - offset, size - offset, radius);
        g.DrawPath(pen, frontPath);
    }

    private static void AddRoundedRectangle(System.Drawing.Drawing2D.GraphicsPath path,
        int x, int y, int width, int height, int radius)
    {
        int d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
    }

    private void Cleanup()
    {
        try
        {
            _instances.Remove(_hwnd);
            DeleteObject(_hBitmap);

            // Only copy to clipboard when the user explicitly clicked the copy button.
            // Double-click / X-button / Esc close should leave the clipboard untouched.
            if (_copyRequested)
            {
                // Clone before disposing so the async clipboard operation has a valid bitmap.
                var clipboardBitmap = _bitmap.Clone(
                    new System.Drawing.Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
                    _bitmap.PixelFormat);
                _bitmap.Dispose();

                App.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await CopyBitmapToClipboardAsync(clipboardBitmap);
                        HotKeyToast.Show("截图已复制到剪贴板");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("FloatingImageWin32", $"Clipboard copy failed: {ex.Message}");
                    }
                    finally
                    {
                        clipboardBitmap.Dispose();
                    }
                });
            }
            else
            {
                _bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"Cleanup failed: {ex.Message}");
        }
    }

    private static async Task CopyBitmapToClipboardAsync(System.Drawing.Bitmap bitmap)
    {
        byte[] png;
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }

        // Keep the stream alive until Clipboard.Flush() has committed the bitmap data.
        // Flush forces the clipboard to read the reference immediately, so disposing
        // the stream afterward is safe.
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        finally
        {
            stream.Dispose();
        }
    }

    // ── Win32 ──

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const int SW_SHOW = 5;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SETCURSOR = 0x0020;
    private const nint VK_ESCAPE = 0x1B;
    private const nint IDC_HAND = 32649;
    private const nint IDC_ARROW = 32512;
    private const int HTCLIENT = 1;
    private const uint SRCCOPY = 0x00CC0020;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const uint CS_DROPSHADOW = 0x00020000;
    private static readonly nint HWND_TOPMOST = (nint)(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
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
    private struct PAINTSTRUCT
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
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(nint hdcDest, int nXOriginDest, int nYOriginDest,
        int nWidthDest, int nHeightDest, nint hdcSrc, int nXOriginSrc, int nYOriginSrc,
        int nWidthSrc, int nHeightSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int iStretchMode);

    private static int GET_X_LPARAM(nint lParam) => (int)(short)(lParam & 0xFFFF);
    private static int GET_Y_LPARAM(nint lParam) => (int)(short)((lParam >> 16) & 0xFFFF);
    private static int GET_WHEEL_DELTA_WPARAM(nint wParam) => (int)(short)((wParam >> 16) & 0xFFFF);
}
