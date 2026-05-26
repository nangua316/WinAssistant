using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.UI;
using WinAssistant.Services;
using WinAssistant.ViewModels;

namespace WinAssistant;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string SingleInstanceMutex = "WinAssistant_SingleInstance";

    public static Window Window { get; private set; } = null!;
    private static LaunchpadWindow? _launchpadWindow;
    public static LaunchpadWindow LaunchpadWindow =>
        _launchpadWindow ??= new LaunchpadWindow();
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static HotKeyService HotKeyService { get; } = new();
    public static SettingsService SettingsService { get; } = new();
    public static DoubleKeyDetector DoubleKeyDetector { get; } = new();
    public static WinKeyInterceptor WinKeyInterceptor { get; } = new();
    private static MainPageViewModel? _mainViewModel;

    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public static T GetService<T>() where T : class
    {
        if (typeof(T) == typeof(MainPageViewModel) && _mainViewModel is T vm)
            return vm;
        throw new InvalidOperationException($"Service {typeof(T).Name} not found");
    }

    public App()
    {
        // Enforce single instance via named mutex
        _mutex = new Mutex(true, SingleInstanceMutex, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }
        AppDomain.CurrentDomain.ProcessExit += (s, e) => _mutex?.Dispose();

        // Global exception handlers for crash diagnostics
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            try
            {
                RestoreTaskbar();
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "WinAssistant_crash.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] APPDOMAIN {(e.IsTerminating ? "FATAL" : "NON-FATAL")}: {ex?.Message}\n{ex?.StackTrace}\n");
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "WinAssistant_crash.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] UNOBSERVED TASK: {e.Exception.Message}\n{e.Exception.StackTrace}\n");
            }
            catch { }
            e.SetObserved();
        };

        // Follow system theme at startup.
        _lastTheme = GetSystemTheme();
        RequestedTheme = _lastTheme;

        InitializeComponent();
        _mainViewModel = new MainPageViewModel(SettingsService, HotKeyService);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Safety: if the taskbar was hidden by a previous crash, restore it now.
        RestoreTaskbar();

        // Catch WinUI-level exceptions (dispatcher timers, event handlers, etc.)
        Current.UnhandledException += (s, e) =>
        {
            try
            {
                RestoreTaskbar();
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "WinAssistant_crash.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] WINUI UNHANDLED: {e.Message}\n{e.Exception?.StackTrace}\n");
            }
            catch { }
            e.Handled = true;
        };

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        HotKeyService.Initialize(WindowHandle);
        Window.Activate();
        // Hide MainWindow immediately — LaunchpadWindow is the primary UI
        ShowWindow(App.WindowHandle, SW_HIDE);
        // Launchpad as the home page — show via LaunchpadWindow (same as hotkey trigger)
        DispatcherQueue.TryEnqueue(() => App.LaunchpadWindow.Open());
        // LaunchpadWindow is created lazily on first Open() call
        // — no flash at startup.
        WinAssistant.Services.AppScanner.PreloadCache();

        // Apply system theme to the root visual and poll for changes.
        ApplyThemeToRoot();
        StartThemeListener();
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var hwnd = FindWindowW(null, "WinAssistant - 全局快捷键工具");
            if (hwnd == nint.Zero)
            {
                // Fallback: search by process
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("WinAssistant"))
                {
                    var h = proc.MainWindowHandle;
                    if (h != nint.Zero) { hwnd = h; break; }
                }
            }
            if (hwnd != nint.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static void RestoreTaskbar()
    {
        try
        {
            var h = FindWindowW("Shell_TrayWnd", null);
            if (h != nint.Zero) ShowWindow(h, SW_SHOW);
        }
        catch { }
    }

    #region System theme support

    private ApplicationTheme _lastTheme;
    private Microsoft.UI.Xaml.DispatcherTimer? _themeTimer;

    private static ApplicationTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
        catch { }
        return ApplicationTheme.Dark;
    }

    private static ElementTheme GetSystemElementTheme() =>
        GetSystemTheme() == ApplicationTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;

    private void ApplyThemeToRoot()
    {
        ApplyThemeToRoot(GetSystemElementTheme());
    }

    private void ApplyThemeToRoot(ElementTheme theme)
    {
        if (Window?.Content is FrameworkElement root)
            root.RequestedTheme = theme;

        var isDark = theme == ElementTheme.Dark;
        try
        {
            var tb = Window.AppWindow.TitleBar;
            if (isDark)
            {
                tb.ButtonForegroundColor = Colors.White;
                tb.ButtonHoverForegroundColor = Colors.White;
                tb.ButtonHoverBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
                tb.ButtonPressedForegroundColor = Colors.White;
                tb.ButtonPressedBackgroundColor = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
                tb.ButtonInactiveForegroundColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
            }
            else
            {
                tb.ButtonForegroundColor = Colors.Black;
                tb.ButtonHoverForegroundColor = Colors.Black;
                tb.ButtonHoverBackgroundColor = Color.FromArgb(0x19, 0x00, 0x00, 0x00);
                tb.ButtonPressedForegroundColor = Colors.Black;
                tb.ButtonPressedBackgroundColor = Color.FromArgb(0x33, 0x00, 0x00, 0x00);
                tb.ButtonInactiveForegroundColor = Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            }
        }
        catch { }
    }

    private void StartThemeListener()
    {
        _themeTimer = new Microsoft.UI.Xaml.DispatcherTimer();
        _themeTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _themeTimer.Tick += (_, _) =>
        {
            var current = GetSystemTheme();
            if (current != _lastTheme)
            {
                _lastTheme = current;
                ApplyThemeToRoot(current == ApplicationTheme.Light ? ElementTheme.Light : ElementTheme.Dark);
            }
        };
        _themeTimer.Start();
    }

    #endregion
}
