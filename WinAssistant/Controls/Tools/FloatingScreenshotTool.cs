using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WinAssistant.Models;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;
using Windows.Foundation;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;
using WinUIRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace WinAssistant.Controls.Tools;

/// <summary>
/// 悬浮截图工具：点击后在屏幕上框选区域，截图结果以无边框窗口悬浮显示。
/// 点击图片关闭，滚轮缩放，不保存文件。
/// </summary>
public class FloatingScreenshotTool : IAssistantTool
{
    public string Id => "floating-screenshot";
    public string Name => "悬浮截图";
    public string Description => "框选截图，图片悬浮显示在桌面，滚轮缩放，点击关闭";
    public string IconGlyph => "📷"; // Camera emoji
    public string? IconColorHex => "#FF34D399";
    public bool IsOneClickAction => true;
    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() =>
        new TextBlock { Text = "悬浮截图", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public UIElement? CreateSettingsContent() => null;

    public string? Activate()
    {
        ScreenshotSelector.Start();
        return "开始截图";
    }
}

/// <summary>
/// 全屏截图选区窗口：把当前屏幕抓下来作为背景，用户拖拽框选，完成后裁剪并悬浮显示。
/// </summary>
internal static class ScreenshotSelector
{
    public static void Start()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(App.Window)),
                DisplayAreaFallback.Primary);
            var bounds = display.OuterBounds;

            var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0,
                    new System.Drawing.Size(bounds.Width, bounds.Height));
            }

            var selector = new ScreenshotSelectorWindow(bmp, bounds.X, bounds.Y);
            selector.Activate();
        }
        catch { }
    }
}

/// <summary>
/// 全屏覆盖选区窗口。
/// </summary>
internal sealed class ScreenshotSelectorWindow : Window
{
    private readonly nint _hwnd;
    private readonly System.Drawing.Bitmap _fullBitmap;
    private readonly WinUIImage _image;
    private readonly WinUIRectangle _selectionRect;
    private readonly Grid _root;
    private bool _selecting;
    private Windows.Foundation.Point _start;
    private double _scale = 1.0;

    public ScreenshotSelectorWindow(System.Drawing.Bitmap fullBitmap, int screenX, int screenY)
    {
        _fullBitmap = fullBitmap;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle = (exStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

        var style = GetWindowLong(_hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        SetWindowLong(_hwnd, GWL_STYLE, style);

        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        _image = new WinUIImage { Stretch = Stretch.UniformToFill };

        _selectionRect = new WinUIRectangle
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed
        };

        var canvas = new Canvas();
        canvas.Children.Add(_selectionRect);

        _root = new Grid();
        _root.Children.Add(_image);
        _root.Children.Add(canvas);
        Content = _root;

        Closed += (_, _) => _fullBitmap?.Dispose();

        _root.IsTabStop = true;
        _root.PointerPressed += OnPressed;
        _root.PointerMoved += OnMoved;
        _root.PointerReleased += OnReleased;
        _root.PointerCaptureLost += (_, _) => Close();
        _root.RightTapped += (_, _) => Close();

        _root.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Escape)
                Close();
        };

        var bounds = AppWindow.ClientSize;
        AppWindow.MoveAndResize(new RectInt32(screenX, screenY, fullBitmap.Width, fullBitmap.Height));
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        _ = LoadImageAsync();
    }

    private async Task LoadImageAsync()
    {
        try
        {
            var img = await BitmapHelper.ToBitmapImageAsync(_fullBitmap);
            _image.Source = img;
        }
        catch { }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        _selecting = true;
        _start = e.GetCurrentPoint(_root).Position;
        Canvas.SetLeft(_selectionRect, _start.X);
        Canvas.SetTop(_selectionRect, _start.Y);
        _selectionRect.Width = 0;
        _selectionRect.Height = 0;
        _selectionRect.Visibility = Visibility.Visible;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting) return;
        var pt = e.GetCurrentPoint(_root).Position;
        var x = Math.Min(_start.X, pt.X);
        var y = Math.Min(_start.Y, pt.Y);
        var w = Math.Abs(pt.X - _start.X);
        var h = Math.Abs(pt.Y - _start.Y);
        Canvas.SetLeft(_selectionRect, x);
        Canvas.SetTop(_selectionRect, y);
        _selectionRect.Width = w;
        _selectionRect.Height = h;
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        var pt = e.GetCurrentPoint(_root).Position;
        var x = Math.Min(_start.X, pt.X);
        var y = Math.Min(_start.Y, pt.Y);
        var w = Math.Abs(pt.X - _start.X);
        var h = Math.Abs(pt.Y - _start.Y);

        if (w < 10 || h < 10)
        {
            Close();
            return;
        }

        var px = (int)(x * _scale);
        var py = (int)(y * _scale);
        var pw = (int)(w * _scale);
        var ph = (int)(h * _scale);

        px = Math.Clamp(px, 0, _fullBitmap.Width - 1);
        py = Math.Clamp(py, 0, _fullBitmap.Height - 1);
        pw = Math.Clamp(pw, 1, _fullBitmap.Width - px);
        ph = Math.Clamp(ph, 1, _fullBitmap.Height - py);

        System.Drawing.Bitmap? cropped = null;
        try
        {
            cropped = _fullBitmap.Clone(
                new System.Drawing.Rectangle(px, py, pw, ph),
                _fullBitmap.PixelFormat);
        }
        catch { }

        Close();

        if (cropped != null)
        {
            App.DispatcherQueue.TryEnqueue(() =>
            {
                try { new FloatingImageWindow(cropped).Activate(); }
                catch { }
            });
        }
    }

    // ── Win32 ──

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly nint HWND_TOPMOST = (nint)(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}

/// <summary>
/// 悬浮显示截图的无边框窗口：点击图片关闭，滚轮缩放。
/// </summary>
internal sealed class FloatingImageWindow : Window
{
    private readonly nint _hwnd;
    private readonly WinUIImage _image;
    private readonly int _baseW;
    private readonly int _baseH;
    private double _zoom = 1.0;

    public FloatingImageWindow(System.Drawing.Bitmap bitmap)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle = (exStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

        var style = GetWindowLong(_hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        SetWindowLong(_hwnd, GWL_STYLE, style);

        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        var cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        _baseW = bitmap.Width;
        _baseH = bitmap.Height;

        _image = new WinUIImage
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(_image);
        Content = root;

        root.Tapped += (_, _) => Close();
        root.PointerWheelChanged += OnWheelChanged;

        var (w, h, x, y) = CalcInitialPlacement();
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        _ = LoadImageAsync(bitmap);
    }

    private async Task LoadImageAsync(System.Drawing.Bitmap bitmap)
    {
        try
        {
            _image.Source = await BitmapHelper.ToBitmapImageAsync(bitmap);
        }
        catch { }
        finally
        {
            bitmap.Dispose();
        }
    }

    private (int w, int h, int x, int y) CalcInitialPlacement()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd),
                DisplayAreaFallback.Primary);
            var area = display.WorkArea;

            int maxW = (int)(area.Width * 0.9);
            int maxH = (int)(area.Height * 0.9);

            double scale = 1.0;
            if (_baseW > maxW || _baseH > maxH)
                scale = Math.Min((double)maxW / _baseW, (double)maxH / _baseH);

            _zoom = scale;
            int w = Math.Max(64, (int)(_baseW * scale));
            int h = Math.Max(64, (int)(_baseH * scale));
            int x = area.X + (area.Width - w) / 2;
            int y = area.Y + (area.Height - h) / 2;
            return (w, h, x, y);
        }
        catch
        {
            return (_baseW, _baseH, 0, 0);
        }
    }

    private void OnWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 1.1 : 0.9;
        _zoom = Math.Clamp(_zoom * factor, 0.2, 5.0);
        e.Handled = true;

        if (!GetWindowRect(_hwnd, out var rect)) return;

        int newW = Math.Max(64, (int)(_baseW * _zoom));
        int newH = Math.Max(64, (int)(_baseH * _zoom));
        int newX = rect.left + ((rect.right - rect.left) - newW) / 2;
        int newY = rect.top + ((rect.bottom - rect.top) - newH) / 2;

        SetWindowPos(_hwnd, HWND_TOPMOST, newX, newY, newW, newH,
            SWP_NOACTIVATE | SWP_NOZORDER);
    }

    // ── Win32 ──

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private static readonly nint HWND_TOPMOST = (nint)(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);
}

/// <summary>
/// System.Drawing.Bitmap → WinUI BitmapImage，通过内存 PNG 流转换。
/// </summary>
internal static class BitmapHelper
{
    public static async Task<BitmapImage> ToBitmapImageAsync(System.Drawing.Bitmap bitmap)
    {
        byte[] png;
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var img = new BitmapImage();
        await img.SetSourceAsync(stream);
        return img;
    }
}
