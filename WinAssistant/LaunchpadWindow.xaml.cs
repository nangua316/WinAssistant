using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.Composition;
using WinAssistant.Helpers;
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

        // MicaBackdrop only works on Windows 11 (build 22000+).
        // On Windows 10 we fall back to a solid theme background to avoid a blank/transparent window.
        WindowBackdropHelper.ApplyMicaOrSolidBackground(this, RootGrid);
        // DWM 暗色模式（SystemBackdrop 之后设置，确保 Mica 用正确主题渲染）
        App.UpdateDwmDarkMode(_hwnd);

        // 初始主题与 App.CurrentTheme 保持一致，避免窗口创建时继承系统默认主题
        RootGrid.RequestedTheme = App.CurrentTheme == ApplicationTheme.Light
            ? ElementTheme.Light : ElementTheme.Dark;

        // Update DWM title bar dark mode when system theme changes
        App.SystemThemeChanged += OnSystemThemeChanged;

        // Popup — no taskbar entry, no caption buttons
        MakeToolWindow();

        // Hide title bar so the page content fills the window
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        // Size to fit the primary monitor work area (respects taskbar and low-res screens)
        var (winW, winH, winX, winY) = CalcWindowSizeAndPosition();
        AppWindow.MoveAndResize(new RectInt32(winX, winY, winW, winH));
        // Actual resize/position happens in OpenCore() after the window is shown

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
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    public void Open()
    {
        try { OpenCore(); }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "WinAssistant_launchpad.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] LAUNCHPAD OPEN FAILED: {ex.Message}\n{ex.StackTrace}\n");
            }
            catch { }
        }
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

        // Cloak the window so DWM never renders a white flash,
        // then show off-screen, render content, and only uncloak
        // after moving to the final position.
        var (winW, winH, winX, winY) = CalcWindowSizeAndPosition();
        var cloaked = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloaked, sizeof(int));
        AppWindow.MoveAndResize(new RectInt32(-9999, -9999, winW, winH));
        ShowWindow(_hwnd, SW_SHOW);

        _isShowing = true;
        DeleteFromTaskbar();

        _page.ViewModel.SetXamlRootGetter(() => _page?.XamlRoot);
        _page.Activate();

        // Init Win32 drag-drop on first show (after window is visible,
        // WinUI subclassing is settled).
        EnsureFileDragDrop();

        var moveTimer = new Microsoft.UI.Xaml.DispatcherTimer();
        moveTimer.Interval = TimeSpan.FromMilliseconds(60);
        moveTimer.Tick += (s, e) =>
        {
            moveTimer.Stop();
            if (!_isShowing) return; // Was closed during the delay
            if (currentGen != _gen) return; // Superseded

            AppWindow.MoveAndResize(new RectInt32(winX, winY, winW, winH));

            // Uncloak now that we're at the final position — DWM will
            // compose the first frame with Mica already rendered.
            cloaked = 0;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloaked, sizeof(int));

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

    private bool _dragDropInitialized;
    private WndProcDelegate? _wndProcDelegate;
    private nint _oldWndProc;

    /// <summary>Set up Win32 WM_DROPFILES drag-drop for this elevated window.
    /// WinUI's AllowDrop is fundamentally broken under elevation (UIPI blocks
    /// OLE cross-integrity marshalling), so we bypass it entirely.</summary>
    private void EnsureFileDragDrop()
    {
        if (_dragDropInitialized) return;
        _dragDropInitialized = true;

        try
        {
            // Process-wide UIPI bypass — allows WM_DROPFILES through the
            // integrity-level barrier so Explorer (medium) → us (high) works.
            ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ADD);
            ChangeWindowMessageFilter(WM_COPYGLOBALDATA, MSGFLT_ADD);

            // Per-window bypass (belt-and-suspenders).
            var filter = new CHANGEFILTERSTRUCT();
            filter.cbSize = (uint)Marshal.SizeOf<CHANGEFILTERSTRUCT>();
            ChangeWindowMessageFilterEx(_hwnd, WM_DROPFILES, MSGFLT_ALLOW, ref filter);

            // Register the WIN32 drop target (NOT OLE — we bypass WinUI's
            // AllowDrop which is broken under elevation).
            DragAcceptFiles(_hwnd, true);

            // Subclass HWND to intercept WM_DROPFILES.
            _wndProcDelegate = FileDropWndProc;
            _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        }
        catch (Exception ex)
        {
            Logger.Log("DragDrop", $"init failed: {ex.Message}");
        }
    }

    private nint FileDropWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DROPFILES)
        {
            HandleDropFiles(wParam);
            return 0;
        }
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private static void HandleDropFiles(nint hDrop)
    {
        try
        {
            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var sb = new System.Text.StringBuilder(260);
                DragQueryFile(hDrop, i, sb, sb.Capacity);
                var path = sb.ToString();

                App.DispatcherQueue.TryEnqueue(() =>
                {
                    var win = App.LaunchpadWindow;
                    if (win?._page == null) return;
                    if (Directory.Exists(path))
                        win._page.ViewModel.AddFolderItem(path, Path.GetFileName(path));
                    else if (File.Exists(path))
                        win._page.ViewModel.AddFileItem(path, Path.GetFileNameWithoutExtension(path));
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log("DragDrop", $"HandleDropFiles: {ex.Message}");
        }
        finally
        {
            DragFinish(hDrop);
        }
    }

    /// <summary>
    /// Calculates a launchpad size that fits the primary monitor work area.
    /// On low-resolution screens (e.g. 1366x768) it shrinks to ~90% of the work area;
    /// on large screens it is capped at 1600x1200 and centered.
    /// </summary>
    private (int w, int h, int x, int y) CalcWindowSizeAndPosition()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd),
                DisplayAreaFallback.Primary);
            var area = display.WorkArea;

            // On lower-resolution screens leave more desktop visible; on large monitors fill more.
            double scale = (area.Width >= 1600 && area.Height >= 900) ? 0.9 : 0.8;
            int w = (int)(area.Width * scale);
            int h = (int)(area.Height * scale);
            w = Math.Clamp(w, 800, 1600);
            h = Math.Clamp(h, 600, 1200);

            if (w > area.Width) w = area.Width;
            if (h > area.Height) h = area.Height;

            int x = area.X + (area.Width - w) / 2;
            int y = area.Y + (area.Height - h) / 2;
            return (w, h, x, y);
        }
        catch
        {
            return (1600, 1200, 0, 0);
        }
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

    // ── UIPI bypass for OLE drag-drop (app runs elevated, Explorer is medium) ──
    // WM_COPYGLOBALDATA is the internal OLE message blocked by UIPI.
    // Allowing it through makes WinUI's AllowDrop/DragOver/Drop work.

    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;
    private const uint MSGFLT_ADD = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint msg, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(nint hWnd, uint msg,
        uint action, ref CHANGEFILTERSTRUCT pChangeFilter);

    [StructLayout(LayoutKind.Sequential)]
    private struct CHANGEFILTERSTRUCT
    {
        public uint cbSize;
        public uint ExtStatus;
    }

    // -- Win32 WM_DROPFILES --
    private const int GWLP_WNDPROC = -4;
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void DragAcceptFiles(nint hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint hDrop, uint iFile,
        [Out] System.Text.StringBuilder? lpszFile, int cch);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void DragFinish(nint hDrop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd,
        uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWA_CLOAK = 14;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    #endregion
}
