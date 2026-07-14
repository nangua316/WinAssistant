using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using static WinAssistant.Helpers.Win32WindowHelper;

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

                    ScreenshotOverlayWin32.Start(bmp, bounds.X, bounds.Y, (cropped, x, y) =>
                    {
                        App.DispatcherQueue.TryEnqueue(() =>
                        {
                            try { FloatingImageWin32.Show(cropped, x, y); }
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

    // Map each HWND to its instance so messages are routed correctly even if multiple
    // overlays exist concurrently (e.g. rapid hotkey presses). ConcurrentDictionary is
    // used because each overlay lives on its own STA thread.
    private static readonly ConcurrentDictionary<nint, ScreenshotOverlayWin32> _instances = new();

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

    /// <summary>
    /// 在专用 STA 线程上创建并运行覆盖层窗口。Win32 窗口必须有自己的消息循环，
    /// 否则依赖 DispatcherQueue 时输入消息可能无法送达 WndProc，导致窗口卡住。
    /// </summary>
    public static void Start(System.Drawing.Bitmap fullBitmap, int screenX, int screenY,
        Action<System.Drawing.Bitmap, int, int> onSelected)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var overlay = new ScreenshotOverlayWin32(fullBitmap, screenX, screenY, onSelected);
                overlay.RunMessageLoop();
            }
            catch (Exception ex)
            {
                Logger.Log("ScreenshotOverlayWin32", $"Thread failed: {ex}");
                fullBitmap.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "ScreenshotOverlay"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private ScreenshotOverlayWin32(System.Drawing.Bitmap fullBitmap, int screenX, int screenY,
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

            _instances.TryAdd(_hwnd, this);

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

    private void RunMessageLoop()
    {
        Win32WindowHelper.RunMessageLoop("ScreenshotOverlayWin32");
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
                PostQuitMessage(0);
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
            _instances.TryRemove(_hwnd, out _);
            DeleteObject(_hBitmap);
            _fullBitmap.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log("ScreenshotOverlayWin32", $"Cleanup failed: {ex.Message}");
        }
    }
}

/// <summary>
/// 纯 Win32 悬浮图片窗口：拖动移动，滚轮缩放，双击关闭，右上角 X 按钮，
/// 右下角复制按钮（点击关闭并复制图片到剪贴板），左上角高亮画笔按钮。
/// 用 Win32 替代 WinUI 3 Window，避免缩放时窗口resize带来的抖动。
/// </summary>
internal sealed class FloatingImageWin32
{
    private static readonly WndProcDelegate _wndProc = WndProcStatic;
    // Multiple floating images can exist at the same time, so map each HWND to its instance
    // instead of keeping a single active reference.
    private static readonly ConcurrentDictionary<nint, FloatingImageWin32> _instances = new();
    private static readonly nint _penCursor = CreatePenCursor();
    private static readonly nint _handCursor = LoadCursor(nint.Zero, IDC_HAND);

    /// <summary>
    /// 优先加载 Windows 系统自带的画笔光标，失败则回退到自定义白色画笔光标。
    /// </summary>
    private static nint CreatePenCursor()
    {
        try
        {
            var cursorPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Cursors", "aero_pen.cur");
            if (File.Exists(cursorPath))
            {
                var cursor = LoadCursorFromFile(cursorPath);
                if (cursor != nint.Zero)
                {
                    Logger.Log("FloatingImageWin32", $"Loaded system pen cursor: {cursorPath}");
                    return cursor;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"Load system pen cursor failed: {ex.Message}");
        }

        return CreateFallbackPenCursor();
    }

    /// <summary>
    /// 自定义白色单色画笔光标，在深色截图背景上足够明显。
    /// </summary>
    private static nint CreateFallbackPenCursor()
    {
        const int size = 32;
        var andMask = new byte[size * size / 8];
        var xorMask = new byte[size * size / 8];
        Array.Fill(andMask, (byte)0xFF);

        void SetPixel(int x, int y)
        {
            if (x < 0 || x >= size || y < 0 || y >= size) return;
            // CreateIcon expects a bottom-up bitmap; row 0 in the array is the bottom row.
            // In monochrome bitmaps, the most-significant bit of each byte is the leftmost pixel.
            int row = (size - 1) - y;
            int byteIndex = row * (size / 8) + x / 8;
            int bitIndex = 7 - (x % 8);
            andMask[byteIndex] &= (byte)~(1 << bitIndex);
            xorMask[byteIndex] |= (byte)(1 << bitIndex); // white pen shape
        }

        // Draw a thick diagonal pen from (2,0) to (26,26).
        for (int i = 0; i < 26; i++)
        {
            int x = 2 + i;
            int y = i;
            // 3px wide body
            SetPixel(x, y);
            SetPixel(x + 1, y);
            SetPixel(x, y + 1);
            SetPixel(x + 1, y + 1);
        }

        var cursor = CreateIcon(nint.Zero, size, size, 1, 1, andMask, xorMask);
        if (cursor == nint.Zero)
        {
            Logger.Log("FloatingImageWin32", $"CreateFallbackPenCursor failed: {Marshal.GetLastWin32Error()}");
        }
        else
        {
            Logger.Log("FloatingImageWin32", $"Fallback pen cursor created: 0x{cursor:X}");
        }
        return cursor;
    }

    private System.Drawing.Bitmap _bitmap = null!;
    private System.Drawing.Bitmap _highlightBitmap = null!;
    private System.Drawing.Bitmap _compositedBitmap = null!;
    private nint _hBitmap;
    private int _baseW;
    private int _baseH;

    private nint _hwnd;
    private double _zoom = 1.0;
    private System.Drawing.Bitmap? _backBuffer;

    private bool _dragging;
    private POINT _dragStartCursor;
    private RECT _dragStartRect;
    private DateTimeOffset _firstClickTime;
    private bool _firstClickPending;
    private bool _closeButtonHover;
    private bool _closeButtonPressed;
    private bool _copyButtonHover;
    private bool _copyButtonPressed;
    private bool _copyRequested;
    private bool _highlightMode;
    private bool _highlightButtonHover;
    private bool _isHighlightDrawing;
    private bool _mouseInside;
    private bool _trackingMouseLeave;
    private POINT _lastHighlightPoint;

    private const string CLASS_NAME = "WinAssistantFloatingImage";
    private const int HIGHLIGHT_BUTTON_SIZE = 36;
    private const int HIGHLIGHT_BUTTON_MARGIN = 10;
    private static readonly System.Drawing.Color HIGHLIGHT_COLOR = System.Drawing.Color.FromArgb(180, 255, 255, 0);
    private int _highlightPenWidth;

    /// <summary>
    /// 在专用 STA 线程上创建并运行悬浮图片窗口。Win32 窗口必须有自己的消息循环，
    /// 否则依赖 DispatcherQueue 时输入消息可能无法送达 WndProc，导致窗口卡住。
    /// </summary>
    public static void Show(System.Drawing.Bitmap bitmap, int screenX, int screenY)
    {
        // Calculate the target monitor's work area on the UI thread before spawning
        // the window thread, so we size the image against the display that owns it.
        int maxW = bitmap.Width;
        int maxH = bitmap.Height;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            var display = DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd),
                DisplayAreaFallback.Primary);
            var area = display.WorkArea;
            maxW = (int)(area.Width * 0.9);
            maxH = (int)(area.Height * 0.9);
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"WorkArea calc failed: {ex.Message}");
        }

        var thread = new Thread(() =>
        {
            try
            {
                var floating = new FloatingImageWin32();
                floating.Initialize(bitmap, screenX, screenY, maxW, maxH);
                floating.RunMessageLoop();
            }
            catch (Exception ex)
            {
                Logger.Log("FloatingImageWin32", $"Thread failed: {ex}");
                bitmap.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "FloatingImage"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private FloatingImageWin32() { }

    private void Initialize(System.Drawing.Bitmap bitmap, int screenX, int screenY, int maxW, int maxH)
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

        // Make the highlighter wide enough to cover a line of text in the original screenshot.
        _highlightPenWidth = Math.Max(35, _baseH / 45);

        _highlightBitmap = new System.Drawing.Bitmap(_baseW, _baseH, PixelFormat.Format32bppPArgb);
        using (var hg = System.Drawing.Graphics.FromImage(_highlightBitmap))
        {
            hg.Clear(System.Drawing.Color.Transparent);
        }

        // 合成图初始等于原图，后续高亮只更新局部区域。
        _compositedBitmap = new System.Drawing.Bitmap(_baseW, _baseH, PixelFormat.Format24bppRgb);
        using (var cg = System.Drawing.Graphics.FromImage(_compositedBitmap))
        {
            cg.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            cg.DrawImageUnscaled(_bitmap, 0, 0);
        }

        try
        {
            _hBitmap = _bitmap.GetHbitmap();

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

            var (w, h) = CalcInitialSize(maxW, maxH);
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

            _instances.TryAdd(_hwnd, this);

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
            _compositedBitmap?.Dispose();
            _highlightBitmap?.Dispose();
            _bitmap?.Dispose();
            throw;
        }
    }

    private void RunMessageLoop()
    {
        Win32WindowHelper.RunMessageLoop("FloatingImageWin32");
    }

    private (int w, int h) CalcInitialSize(int maxW, int maxH)
    {
        try
        {
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

                if (HitTestHighlightButton(x, y, cw, ch))
                {
                    _highlightMode = !_highlightMode;
                    _isHighlightDrawing = false;
                    _firstClickPending = false;
                    if (_highlightMode)
                    {
                        // Capture the mouse so clicks outside the image can be detected.
                        SetCapture(hwnd);
                    }
                    else if (GetCapture() == hwnd)
                    {
                        ReleaseCapture();
                    }
                    InvalidateRect(hwnd, nint.Zero, false);
                    return 0;
                }

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

                if (_highlightMode)
                {
                    // Clicking outside the image exits highlight mode instead of drawing.
                    if (x < 0 || x >= cw || y < 0 || y >= ch)
                    {
                        _highlightMode = false;
                        _isHighlightDrawing = false;
                        _firstClickPending = false;
                        if (GetCapture() == hwnd)
                        {
                            ReleaseCapture();
                        }
                        InvalidateRect(hwnd, nint.Zero, false);
                        return 0;
                    }

                    _isHighlightDrawing = true;
                    _lastHighlightPoint = new POINT { X = x, Y = y };
                    SetCapture(hwnd);
                    return 0;
                }

                _dragging = false;
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

                bool highlightHovering = HitTestHighlightButton(mx, my, cw, ch);
                if (highlightHovering != _highlightButtonHover)
                {
                    _highlightButtonHover = highlightHovering;
                    InvalidateRect(hwnd, nint.Zero, false);
                }

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

                if (!_mouseInside)
                {
                    _mouseInside = true;
                    InvalidateRect(hwnd, nint.Zero, false);
                }

                // Track when the cursor leaves the window so we can hide the buttons.
                if (!_trackingMouseLeave)
                {
                    _trackingMouseLeave = true;
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = hwnd,
                        dwHoverTime = 0
                    };
                    TrackMouseEvent(ref tme);
                }

                if (_highlightMode && _isHighlightDrawing)
                {
                    var current = new POINT { X = mx, Y = my };
                    DrawHighlightLine(_lastHighlightPoint, current);
                    _lastHighlightPoint = current;
                    return 0;
                }

                // Dragging is only allowed outside highlight mode.
                if (GetCapture() == hwnd && !_highlightMode)
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
                int ux = GET_X_LPARAM(lParam);
                int uy = GET_Y_LPARAM(lParam);
                GetClientRect(hwnd, out var cr);
                int cw = cr.right - cr.left;
                int ch = cr.bottom - cr.top;

                if (_highlightMode && _isHighlightDrawing)
                {
                    _isHighlightDrawing = false;
                    // Keep capture while highlight mode remains active.
                    return 0;
                }

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
                    if (GetCapture() == hwnd)
                    {
                        ReleaseCapture();
                    }
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

                // A plain click outside highlight/drag mode releases capture.
                if (GetCapture() == hwnd && !_highlightMode)
                {
                    ReleaseCapture();
                }
                return 0;
            }

            case WM_MOUSELEAVE:
            {
                _trackingMouseLeave = false;
                _mouseInside = false;
                InvalidateRect(hwnd, nint.Zero, false);
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

                    if (HitTestHighlightButton(pt.X, pt.Y, cw, ch) ||
                        HitTestCloseButton(pt.X, pt.Y, cw, ch) ||
                        HitTestCopyButton(pt.X, pt.Y, cw, ch))
                    {
                        SetCursor(_handCursor);
                        return (nint)1;
                    }

                    if (_highlightMode)
                    {
                        SetCursor(_penCursor != nint.Zero ? _penCursor : LoadCursor(nint.Zero, IDC_CROSS));
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
                PostQuitMessage(0);
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
            // Reuse the buffer when the window size hasn't changed.
            if (_backBuffer == null || _backBuffer.Width != w || _backBuffer.Height != h)
            {
                _backBuffer?.Dispose();
                _backBuffer = new System.Drawing.Bitmap(w, h, PixelFormat.Format24bppRgb);
            }
            using var g = System.Drawing.Graphics.FromImage(_backBuffer);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_compositedBitmap, 0, 0, w, h);

            // Draw the semi-transparent buttons on top of everything.
            // Hide them when the mouse is outside the window, unless highlight mode
            // is active so the user can still turn it off.
            if (_mouseInside || _highlightMode)
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                DrawHighlightButton(g, w, h);
                DrawCloseButton(g, w, h);
                DrawCopyButton(g, w, h);
            }

            using var screenG = System.Drawing.Graphics.FromHdc(hdc);
            screenG.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            screenG.DrawImageUnscaled(_backBuffer, 0, 0);
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

    private static System.Drawing.Rectangle GetHighlightButtonRect(int windowW, int windowH)
    {
        // Place to the left of the copy button in the bottom-right corner.
        return new System.Drawing.Rectangle(
            windowW - HIGHLIGHT_BUTTON_SIZE * 2 - HIGHLIGHT_BUTTON_MARGIN * 2,
            windowH - HIGHLIGHT_BUTTON_SIZE - HIGHLIGHT_BUTTON_MARGIN,
            HIGHLIGHT_BUTTON_SIZE, HIGHLIGHT_BUTTON_SIZE);
    }

    private static bool HitTestHighlightButton(int x, int y, int windowW, int windowH)
    {
        return GetHighlightButtonRect(windowW, windowH).Contains(x, y);
    }

    private void DrawHighlightButton(System.Drawing.Graphics g, int windowW, int windowH)
    {
        try
        {
            var rect = GetHighlightButtonRect(windowW, windowH);
            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Match the copy/close button visual style: same alpha levels.
            int alpha = _highlightMode ? 140 : (_highlightButtonHover ? 105 : 70);
            using var bgBrush = new System.Drawing.SolidBrush(
                System.Drawing.Color.FromArgb(alpha, 0, 0, 0));
            g.FillEllipse(bgBrush, rect);

            // Draw a simple pen/highlighter icon: a diagonal line with a small tip.
            const int padding = 10;
            using var shadowPen = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(160, 0, 0, 0), 2);
            g.DrawLine(shadowPen, rect.Left + padding + 1, rect.Bottom - padding - 2,
                rect.Right - padding + 2, rect.Top + padding - 4);
            g.DrawLine(shadowPen, rect.Right - padding - 1, rect.Top + padding,
                rect.Right - padding + 4, rect.Top + padding - 7);

            using var pen = new System.Drawing.Pen(
                _highlightMode ? HIGHLIGHT_COLOR : System.Drawing.Color.White, 2);
            g.DrawLine(pen, rect.Left + padding, rect.Bottom - padding - 3,
                rect.Right - padding + 1, rect.Top + padding - 5);
            g.DrawLine(pen, rect.Right - padding - 2, rect.Top + padding - 1,
                rect.Right - padding + 3, rect.Top + padding - 8);

            g.SmoothingMode = oldSmoothing;
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"DrawHighlightButton failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 用 HSV Color blend 把 _highlightBitmap 的指定区域合成到 _compositedBitmap。
    /// 黑色文字保持黑色，白色/浅色背景被染上黄色，实现真实荧光笔效果。
    /// </summary>
    private void CompositeHighlight(int x, int y, int width, int height)
    {
        try
        {
            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            int right = Math.Min(_baseW, left + width);
            int bottom = Math.Min(_baseH, top + height);
            if (left >= right || top >= bottom) return;

            int w = right - left;
            int h = bottom - top;

            var baseData = _bitmap.LockBits(
                new System.Drawing.Rectangle(left, top, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var blendData = _highlightBitmap.LockBits(
                new System.Drawing.Rectangle(left, top, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var resultData = _compositedBitmap.LockBits(
                new System.Drawing.Rectangle(left, top, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                unsafe
                {
                    byte* basePtr = (byte*)baseData.Scan0;
                    byte* blendPtr = (byte*)blendData.Scan0;
                    byte* resultPtr = (byte*)resultData.Scan0;

                    int baseStride = baseData.Stride;
                    int blendStride = blendData.Stride;
                    int resultStride = resultData.Stride;

                    var highlightHsv = RgbToHsv(HIGHLIGHT_COLOR.R, HIGHLIGHT_COLOR.G, HIGHLIGHT_COLOR.B);

                    for (int py = 0; py < h; py++)
                    {
                        byte* bpRow = basePtr + py * baseStride;
                        byte* lpRow = blendPtr + py * blendStride;
                        byte* rpRow = resultPtr + py * resultStride;

                        for (int px = 0; px < w; px++)
                        {
                            byte* bp = bpRow + px * 3;
                            byte* lp = lpRow + px * 4;
                            byte* rp = rpRow + px * 3;

                            byte blendA = lp[3];
                            if (blendA == 0)
                            {
                                rp[0] = bp[0];
                                rp[1] = bp[1];
                                rp[2] = bp[2];
                                continue;
                            }

                            double alpha = blendA / 255.0;

                            // Color blend in HSV: preserve base brightness, apply highlight hue/saturation.
                            var baseHsv = RgbToHsv(bp[2], bp[1], bp[0]);
                            var (r, g, b) = HsvToRgb(highlightHsv.H, highlightHsv.S, baseHsv.V);

                            rp[0] = (byte)(bp[0] + (b - bp[0]) * alpha);
                            rp[1] = (byte)(bp[1] + (g - bp[1]) * alpha);
                            rp[2] = (byte)(bp[2] + (r - bp[2]) * alpha);
                        }
                    }
                }
            }
            finally
            {
                _bitmap.UnlockBits(baseData);
                _highlightBitmap.UnlockBits(blendData);
                _compositedBitmap.UnlockBits(resultData);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"CompositeHighlight failed: {ex.Message}");
        }
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double v = max;
        double d = max - min;
        double s = max == 0 ? 0 : d / max;

        if (max == min)
            return (0, s, v);

        double h;
        if (max == rd)
            h = (gd - bd) / d + (gd < bd ? 6 : 0);
        else if (max == gd)
            h = (bd - rd) / d + 2;
        else
            h = (rd - gd) / d + 4;
        h /= 6.0;

        return (h, s, v);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        double r, g, b;
        int i = (int)(h * 6);
        double f = h * 6 - i;
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);

        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private void DrawHighlightLine(POINT fromWindow, POINT toWindow)
    {
        try
        {
            GetClientRect(_hwnd, out var clientRect);
            int cw = clientRect.right - clientRect.left;
            int ch = clientRect.bottom - clientRect.top;
            if (cw == 0 || ch == 0) return;

            double scaleX = (double)_baseW / cw;
            double scaleY = (double)_baseH / ch;

            int x1 = (int)(fromWindow.X * scaleX);
            int y1 = (int)(fromWindow.Y * scaleY);
            int x2 = (int)(toWindow.X * scaleX);
            int y2 = (int)(toWindow.Y * scaleY);

            x1 = Math.Clamp(x1, 0, _baseW - 1);
            y1 = Math.Clamp(y1, 0, _baseH - 1);
            x2 = Math.Clamp(x2, 0, _baseW - 1);
            y2 = Math.Clamp(y2, 0, _baseH - 1);

            // Region that needs to be recomposited: the segment bounding box expanded by
            // the pen width to cover round caps and anti-aliasing.
            int dirtyX = Math.Min(x1, x2) - _highlightPenWidth;
            int dirtyY = Math.Min(y1, y2) - _highlightPenWidth;
            int dirtyW = Math.Abs(x2 - x1) + _highlightPenWidth * 2;
            int dirtyH = Math.Abs(y2 - y1) + _highlightPenWidth * 2;

            using (var g = System.Drawing.Graphics.FromImage(_highlightBitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                using var pen = new System.Drawing.Pen(HIGHLIGHT_COLOR, _highlightPenWidth);
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;

                // Interpolate between the two points so fast mouse movements don't leave
                // dotted gaps; draw a sequence of short overlapping segments instead.
                double dx = x2 - x1;
                double dy = y2 - y1;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double step = _highlightPenWidth / 4.0;
                int segments = Math.Max(1, (int)(dist / step));

                for (int i = 0; i < segments; i++)
                {
                    double t1 = i / (double)segments;
                    double t2 = (i + 1) / (double)segments;
                    int sx1 = (int)(x1 + dx * t1);
                    int sy1 = (int)(y1 + dy * t1);
                    int sx2 = (int)(x1 + dx * t2);
                    int sy2 = (int)(y1 + dy * t2);
                    g.DrawLine(pen, sx1, sy1, sx2, sy2);
                }
            }

            CompositeHighlight(dirtyX, dirtyY, dirtyW, dirtyH);

            // Invalidate only the window region that corresponds to the dirty bitmap
            // rectangle, so large screenshots don't redraw the whole image on every segment.
            if (cw > 0 && ch > 0)
            {
                double invScaleX = (double)cw / _baseW;
                double invScaleY = (double)ch / _baseH;
                int winDirtyX = (int)(dirtyX * invScaleX);
                int winDirtyY = (int)(dirtyY * invScaleY);
                int winDirtyW = Math.Max(1, (int)(dirtyW * invScaleX));
                int winDirtyH = Math.Max(1, (int)(dirtyH * invScaleY));

                var dirtyRect = new RECT
                {
                    left = winDirtyX,
                    top = winDirtyY,
                    right = winDirtyX + winDirtyW,
                    bottom = winDirtyY + winDirtyH
                };
                nint pDirtyRect = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
                try
                {
                    Marshal.StructureToPtr(dirtyRect, pDirtyRect, false);
                    InvalidateRect(_hwnd, pDirtyRect, false);
                }
                finally
                {
                    Marshal.FreeHGlobal(pDirtyRect);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("FloatingImageWin32", $"DrawHighlightLine failed: {ex.Message}");
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
            _instances.TryRemove(_hwnd, out _);
            DeleteObject(_hBitmap);

            // Only copy to clipboard when the user explicitly clicked the copy button.
            // Double-click / X-button / Esc close should leave the clipboard untouched.
            if (_copyRequested)
            {
                // _compositedBitmap already contains the Color-blended highlight.
                System.Drawing.Bitmap? clipboardBitmap = null;
                try
                {
                    clipboardBitmap = _compositedBitmap.Clone(
                        new System.Drawing.Rectangle(0, 0, _compositedBitmap.Width, _compositedBitmap.Height),
                        _compositedBitmap.PixelFormat);
                }
                finally
                {
                    _compositedBitmap.Dispose();
                    _bitmap.Dispose();
                    _highlightBitmap.Dispose();
                }

                App.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (clipboardBitmap != null)
                        {
                            await CopyBitmapToClipboardAsync(clipboardBitmap);
                            HotKeyToast.Show("截图已复制到剪贴板");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("FloatingImageWin32", $"Clipboard copy failed: {ex.Message}");
                    }
                    finally
                    {
                        clipboardBitmap?.Dispose();
                    }
                });
            }
            else
            {
                _compositedBitmap.Dispose();
                _bitmap.Dispose();
                _highlightBitmap.Dispose();
            }

            _backBuffer?.Dispose();
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
}
