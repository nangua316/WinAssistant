using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.Composition;
using WinAssistant.Pages;

namespace WinAssistant;

public sealed partial class LaunchpadWindow : Window
{
    private LaunchpadPage? _page;
    private int _gen;
    private bool _isShowing;
    private readonly nint _hwnd;
    private static ITaskbarList2? _taskbar;

    public LaunchpadWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Dark mode
        var darkMode = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        // Acrylic backdrop (blur + tint)
        SystemBackdrop = new DesktopAcrylicBackdrop();

        // Popup — no taskbar entry, no caption buttons
        MakeToolWindow();

        // Hide title bar so the page content fills the window
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        // Size to ~85% of virtual screen (DPI-aware)
        var (winW, winH) = CalcWindowSize();
        AppWindow.MoveAndResize(new RectInt32((winW - 860) / 2, (winH - 750) / 2, 860, 750)); // initial: smaller until DPI is known
        // Actual resize happens in OpenCore() when DPI is available

        // Rounded corners (Windows 11 DWM)
        var cornerPref = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        // Keep hidden on construction — shown on first Open()
        ShowWindow(_hwnd, SW_HIDE);

        // Intercept Alt+F4 / system close when showing
        AppWindow.Closing += (_, e) =>
        {
            if (_isShowing)
            {
                e.Cancel = true;
                CloseCore();
            }
        };

        // Auto-close when user clicks outside (deactivate)
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && _isShowing)
                CloseCore();
        };

        // Lazy-init ITaskbarList2 (once per process)
        if (_taskbar == null)
        {
            try
            {
                var clsid = new Guid("56FDF342-FD6D-11d0-958A-006097C9A090");
                var type = Type.GetTypeFromCLSID(clsid);
                if (type != null)
                {
                    var tl = (ITaskbarList2)Activator.CreateInstance(type)!;
                    tl.HrInit();
                    _taskbar = tl;
                }
            }
            catch { }
        }
    }

    public void Open()
    {
        try { OpenCore(); }
        catch { }
    }

    private void DeleteFromTaskbar()
    {
        try { _taskbar?.DeleteTab(_hwnd); }
        catch { }
    }

    private void OpenCore()
    {
        if (_isShowing)
        {
            if (_page != null)
                OnCloseRequested(null, null);
            return;
        }

        _gen++;
        var currentGen = _gen;

        if (_page == null)
        {
            _page = new LaunchpadPage();
            _page.CloseRequested += OnCloseRequested;
            ContentScaleHost.Children.Add(_page);
        }

        _page.ViewModel.SetXamlRootGetter(() => _page?.XamlRoot);
        _page.Activate();

        // Show window
        MakeToolWindow();
        ShowWindow(_hwnd, SW_SHOW);
        // Size to ~95% of screen (physical pixels), centered
        var dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        var (winW, winH) = CalcWindowSize();
        var physW = GetSystemMetrics(SM_CXSCREEN);
        var physH = GetSystemMetrics(SM_CYSCREEN);
        AppWindow.MoveAndResize(new RectInt32((physW - winW) / 2, (physH - winH) / 2, winW, winH));

        // Force window to top (use HWND_TOPMOST temporarily, then restore)
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        var fgHwnd = GetForegroundWindow();
        var fgThreadId = GetWindowThreadProcessId(fgHwnd, out _);
        var myThreadId = GetWindowThreadProcessId(_hwnd, out _);
        if (fgThreadId != myThreadId)
            AttachThreadInput(myThreadId, fgThreadId, true);
        SetForegroundWindow(_hwnd);
        BringWindowToTop(_hwnd);
        if (fgThreadId != myThreadId)
            AttachThreadInput(myThreadId, fgThreadId, false);
        SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

        _isShowing = true;
        DeleteFromTaskbar();

        // Log actual window size for debugging
        if (GetWindowRect(_hwnd, out var wrect))
            Log($"Window: {wrect.right - wrect.left}x{wrect.bottom - wrect.top} at ({wrect.left},{wrect.top}), page ActualWidth={_page?.ActualWidth}");

        // Fade in
        var inner = ElementCompositionPreview.GetElementVisual(ContentScaleHost);
        if (inner == null) return;
        inner.Opacity = 0;

        var compositor = inner.Compositor;
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0, 0);
        fadeIn.InsertKeyFrame(1, 1);
        fadeIn.Duration = TimeSpan.FromMilliseconds(150);
        inner.StartAnimation("Opacity", fadeIn);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        try { CloseCore(); }
        catch { }
    }

    private void CloseCore()
    {
        // Hide immediately — no fade-out delay so the launched app can take focus.
        ForceHide();
    }

    private void ForceHide()
    {
        _isShowing = false;

        var inner = ElementCompositionPreview.GetElementVisual(ContentScaleHost);
        if (inner != null)
        {
            inner.Opacity = 1;
        }

        ShowWindow(_hwnd, SW_HIDE);
        DeleteFromTaskbar();
    }

    private void MakeToolWindow()
    {
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
    }

    /// <summary>Virtual screen size in DIPs (DPI-independent pixels).</summary>
    private (int w, int h) GetVirtualScreenSize()
    {
        var dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96; // fallback
        var physW = GetSystemMetrics(SM_CXSCREEN);
        var physH = GetSystemMetrics(SM_CYSCREEN);
        // Convert physical → virtual: virtual = physical * 96 / dpi
        return (physW * 96 / dpi, physH * 96 / dpi);
    }

    /// <summary>Window size in PHYSICAL pixels, fixed 1600x1200.</summary>
    private (int w, int h) CalcWindowSize()
    {
        return (1600, 1200);
    }

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant_dbg.txt"),
            $"[{DateTime.Now:HH:mm:ss.fff}] Launchpad: {msg}{Environment.NewLine}"); }
        catch { }
    }

    #region P/Invoke and COM interop

    [ComImport, Guid("EEDF1CFE-387B-41A1-AF42-0A0B30D1F1C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(nint hwnd);

    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private static readonly nint HWND_TOPMOST = (nint)(-1);
    private static readonly nint HWND_NOTOPMOST = (nint)(-2);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    #endregion
}
