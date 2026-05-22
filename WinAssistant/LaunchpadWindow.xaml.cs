using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.Composition;
using WinAssistant.Pages;

namespace WinAssistant;

public sealed partial class LaunchpadWindow : Window
{
    private LaunchpadPage? _page;
    private int _gen;
    private bool _isShowing;
    private bool _isFullScreen;
    private readonly nint _hwnd;
    private static ITaskbarList2? _taskbar;

    private static readonly nint HWND_BOTTOM = (nint)1;
    private static readonly nint HWND_TOP = (nint)0;
    private static readonly nint HWND_TOPMOST = (nint)(-1);
    private static readonly nint HWND_NOTOPMOST = (nint)(-2);

    public LaunchpadWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var darkMode = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        // Cloak immediately so the window is never visible without cloak.
        int cloak = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

        // Set toolwindow style then show (while cloaked) to init the visual tree.
        MakeToolWindow();

        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
        DeleteFromTaskbar();

        // Enter FullScreen once and never leave — avoids presenter-transition jank.
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        _isFullScreen = true;
        // Re-assert TOOLWINDOW after FullScreen (which clears it).
        MakeToolWindow();
        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        // Intercept close (Alt+F4 / taskbar close when showing) → hide launchpad.
        AppWindow.Closing += (_, e) =>
        {
            if (_isShowing)
            {
                e.Cancel = true;
                CloseCore();
            }
            // else: already hidden, let the close propagate (app shutdown).
        };

        // Lazy-init ITaskbarList2 (once per process).
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
            catch (Exception ex)
            {
                Log($"ITaskbarList2 init failed: {ex.Message}");
            }
        }
    }

    public void Open()
    {
        try { OpenCore(); }
        catch { }
    }

    private void Log(string m)
    {
        try { File.AppendAllText(
            Path.Combine(Path.GetTempPath(), "WinAssistant_crash.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] {m}\n"); }
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

        var inner = ElementCompositionPreview.GetElementVisual(ContentScaleHost);
        if (inner == null) { Log("OpenCore ERROR: inner is null"); return; }
        var screenW = GetSystemMetrics(SM_CXSCREEN);
        var screenH = GetSystemMetrics(SM_CYSCREEN);
        inner.CenterPoint = new System.Numerics.Vector3(screenW / 2f, screenH / 2f, 0);
        inner.Opacity = 0;
        inner.Scale = new System.Numerics.Vector3(0.86f, 0.86f, 1);

        var compositor = inner.Compositor;

        // Show window first so XAML visual tree is connected.
        MakeToolWindow();
        ShowWindow(_hwnd, SW_SHOW);
        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        _page.ViewModel.SetXamlRootGetter(() => _page?.XamlRoot);
        _page.Activate();

        try
        {
            // FullScreen is always on — no presenter change.
            MakeToolWindow();
            SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            // Bring to front (topmost to cover taskbar) while still cloaked.
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE);
            var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var ourThread = GetCurrentThreadId();
            if (fgThread != ourThread)
                AttachThreadInput(ourThread, fgThread, true);
            SetForegroundWindow(_hwnd);
            if (fgThread != ourThread)
                AttachThreadInput(ourThread, fgThread, false);

            // Delay uncloak to let WinUI commit its first frame after SW_SHOW.
            int decloak = 0;
            var uncloakTimer = new Microsoft.UI.Xaml.DispatcherTimer();
            uncloakTimer.Interval = TimeSpan.FromMilliseconds(30);
            uncloakTimer.Tick += (s, e) =>
            {
                uncloakTimer.Stop();
                try
                {
                    if (_gen != currentGen) return;

                    DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref decloak, sizeof(int));
                    _isShowing = true;

                    var easeOut = compositor.CreateCubicBezierEasingFunction(
                        new System.Numerics.Vector2(0.0f, 0.0f),
                        new System.Numerics.Vector2(0.2f, 1.0f));

                    var fadeIn = compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(0, 0, easeOut);
                    fadeIn.InsertKeyFrame(1, 1, easeOut);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(180);

                    var scaleUp = compositor.CreateVector3KeyFrameAnimation();
                    scaleUp.InsertKeyFrame(0, new System.Numerics.Vector3(0.86f, 0.86f, 1), easeOut);
                    scaleUp.InsertKeyFrame(1, System.Numerics.Vector3.One, easeOut);
                    scaleUp.Duration = TimeSpan.FromMilliseconds(180);

                    inner.StartAnimation("Opacity", fadeIn);
                    inner.StartAnimation("Scale", scaleUp);
                }
                catch { }
            };
            uncloakTimer.Start();
        }
        catch { }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        try { CloseCore(); }
        catch { }
    }

    private void CloseCore()
    {
        var currentGen = _gen;

        var inner = ElementCompositionPreview.GetElementVisual(ContentScaleHost);
        if (inner?.Compositor == null) { ForceHide(); return; }
        var compositor = inner.Compositor;

        // Play fade-out and scale-down simultaneously.
        var easeIn = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.4f, 0.0f),
            new System.Numerics.Vector2(1.0f, 1.0f));

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(0, 1, easeIn);
        fadeOut.InsertKeyFrame(1, 0, easeIn);
        fadeOut.Duration = TimeSpan.FromMilliseconds(120);

        var scaleDown = compositor.CreateVector3KeyFrameAnimation();
        scaleDown.InsertKeyFrame(0, System.Numerics.Vector3.One, easeIn);
        scaleDown.InsertKeyFrame(1, new System.Numerics.Vector3(0.94f, 0.94f, 1), easeIn);
        scaleDown.Duration = TimeSpan.FromMilliseconds(120);

        inner.StartAnimation("Opacity", fadeOut);
        inner.StartAnimation("Scale", scaleDown);

        var timer = new Microsoft.UI.Xaml.DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(140);
        timer.Tick += (s, t) =>
        {
            try
            {
                timer.Stop();
                if (_gen != currentGen) return;
                ForceHide();
            }
            catch { }
        };
        timer.Start();
    }

    private void ForceHide()
    {
        try
        {
            _isShowing = false;

            var inner = ElementCompositionPreview.GetElementVisual(ContentScaleHost);
            if (inner != null)
            {
                inner.Opacity = 1;
                inner.Scale = System.Numerics.Vector3.One;
            }

            // Cloak (invisible), then hide — keeps FullScreen so the
            // next uncloak has no presenter transition jank.
            int cloak = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

            ShowWindow(_hwnd, SW_HIDE);

            // Re-assert toolwindow, remove topmost, and push to bottom while cloaked.
            MakeToolWindow();
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE);

            DeleteFromTaskbar();
        }
        catch { }
    }

    private void MakeToolWindow()
    {
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_APPWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
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
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_CLOAK = 13;

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

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    #endregion
}
