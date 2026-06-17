using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
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
    public bool IsShowing => _isShowing;
    private bool _isPinned;
    private readonly nint _hwnd;
    private static ITaskbarList2? _taskbar;
    private long _lastOpenTicks;
    private Microsoft.UI.Xaml.DispatcherTimer? _focusTimer;

    public LaunchpadWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Dark mode — follow system theme
        var isDark = App.CurrentTheme == ApplicationTheme.Dark;
        var darkMode = isDark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        // Update DWM title bar dark mode when system theme changes
        App.SystemThemeChanged += OnSystemThemeChanged;

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

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        var isDark = App.CurrentTheme == ApplicationTheme.Dark;
        var darkMode = isDark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
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

        // Guard against immediate deactivation after open
        _lastOpenTicks = DateTime.UtcNow.Ticks;

        _gen++;
        var currentGen = _gen;

        if (_page == null)
        {
            _page = new LaunchpadPage();
            _page.OwnerHwnd = _hwnd;
            _page.CloseRequested += OnCloseRequested;
            _page.PinChanged += (_, pinned) => _isPinned = pinned;
            ContentScaleHost.Children.Add(_page);
        }

        _page.ViewModel.SetXamlRootGetter(() => _page?.XamlRoot);
        _page.Activate();

        // Show the window offscreen so DWM's white flash is invisible to the
        // user. After ~60ms (WinUI's first composition frames are done), move
        // it onscreen and activate.
        // 构造函数已调用 MakeToolWindow()，此处重复调用会导致卡顿
        var (winW, winH) = CalcWindowSize();
        AppWindow.MoveAndResize(new RectInt32(-9999, -9999, winW, winH));
        ShowWindow(_hwnd, SW_SHOW);

        _isShowing = true;
        DeleteFromTaskbar();

        var moveTimer = new Microsoft.UI.Xaml.DispatcherTimer();
        moveTimer.Interval = TimeSpan.FromMilliseconds(60);
        moveTimer.Tick += (s, e) =>
        {
            moveTimer.Stop();
            if (!_isShowing) return; // Was closed during the delay
            if (currentGen != _gen) return; // Superseded

            var physW = GetSystemMetrics(SM_CXSCREEN);
            var physH = GetSystemMetrics(SM_CYSCREEN);
            var cx = (physW - winW) / 2;
            var cy = (physH - winH) / 2;
            SetWindowPos(_hwnd, nint.Zero, cx, cy, 0, 0, SWP_NOSIZE | SWP_NOZORDER);

            // Force window to top and foreground
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
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

            StartFocusTimer();

            if (GetWindowRect(_hwnd, out var wrect)) { }
        };
        moveTimer.Start();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // Page.Close() already saved search text — no need to save again
        try { CloseCore(skipSave: true); }
        catch { }
    }

    private void CloseCore(bool skipSave = false)
    {
        if (!skipSave) SaveSearchText();
        ForceHide();
    }

    private void SaveSearchText()
    {
        try
        {
            if (_page == null) return;
            var settings = App.SettingsService.Load();
            settings.LastSearchText = _page.ViewModel.SearchText;
            App.SettingsService.Save(settings);
        }
        catch { }
    }

    private void ForceHide()
    {
        _isShowing = false;
        StopFocusTimer();

        ShowWindow(_hwnd, SW_HIDE);
        DeleteFromTaskbar();
    }

    private void StartFocusTimer()
    {
        StopFocusTimer();
        _focusTimer = new Microsoft.UI.Xaml.DispatcherTimer();
        _focusTimer.Interval = TimeSpan.FromMilliseconds(150);
        _focusTimer.Tick += OnFocusTick;
        _focusTimer.Start();
    }

    private void StopFocusTimer()
    {
        if (_focusTimer != null)
        {
            _focusTimer.Stop();
            _focusTimer.Tick -= OnFocusTick;
            _focusTimer = null;
        }
    }

    private void OnFocusTick(object? sender, object e)
    {
        if (!_isShowing || _isPinned) return;

        var elapsed = (DateTime.UtcNow.Ticks - _lastOpenTicks) / TimeSpan.TicksPerMillisecond;
        if (elapsed < 200) return;

        var foreground = GetForegroundWindow();
        if (foreground != _hwnd)
            CloseCore();
    }

    private void MakeToolWindow()
    {
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle = (exStyle & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
        // Force taskbar to re-evaluate extended styles
        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>Window size in PHYSICAL pixels, fixed 1600x1200.</summary>
    private (int w, int h) CalcWindowSize()
    {
        return (1600, 1200);
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
