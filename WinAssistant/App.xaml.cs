using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.System;
using Windows.UI;
using WinAssistant.Controls.AiChat;
using WinAssistant.Controls.Tools;
using WinAssistant.Helpers;
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
    public static MouseHookService MouseHookService { get; } = new();
    public static QwenService QwenService { get; } = new();
    public static SkillLibraryService SkillLibraryService { get; } = new();
    public static SkillExecutionService SkillExecutionService { get; } = new(QwenService);
    public static KeyboardHookService KeyboardHookService { get; } = new();
    public static ImeService ImeService { get; } = new();
    public static TsfImeMonitorService TsfImeMonitorService { get; } = new();

    /// <summary>Fired when the system theme changes (light ↔ dark).</summary>
    public static event EventHandler? SystemThemeChanged;

    /// <summary>当前实际启用的主题（可能不同于注册表值，例如通过 F5/ThemeSwitcherTool 临时切换）。</summary>
    public static ApplicationTheme CurrentTheme => _lastTheme;

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
        _mutex = new Mutex(true, SingleInstanceMutex, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }
        // Enable native Win32 menu dark/light mode (affects tray context menu)
        SetPreferredAppMode(1); // 1 = AllowDark, respects system theme

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            HotKeyToast.Cleanup();
            TsfImeMonitorService.Stop();
            ImeService.Stop();
            KeyboardHookService.Dispose();
            ToolHostWindow.CloseAll();
            _mutex?.Dispose();
        };

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

        _lastTheme = GetSystemTheme();
        RequestedTheme = _lastTheme;

        InitializeComponent();
        _mainViewModel = new MainPageViewModel(SettingsService, HotKeyService);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        RestoreTaskbar();

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
        // 在窗口隐藏前设置主题，确保根元素 RequestedTheme 正确
        ApplyThemeToRoot();
        ShowWindow(App.WindowHandle, SW_HIDE);

        MouseHookService.MiddleButtonClicked += OnLaunchpadTriggered;
        MouseHookService.XButton1Clicked += OnLaunchpadTriggered;
        MouseHookService.XButton2Clicked += OnLaunchpadTriggered;

        _mainViewModel!.LoadSettings();
        SkillLibraryService.Load();
        var appSettings = SettingsService.Load();
        QwenService.Configure(appSettings.AiApiKey, appSettings.AiEndpoint, appSettings.AiChatModel);
        DispatcherQueue.TryEnqueue(() => App.LaunchpadWindow.Open());

        WinAssistant.Services.AppScanner.PreloadCache();

        // 输入法管理服务
        KeyboardHookService.Start();
        ImeService.Start();

        // 键盘输入触发 IME 状态检测（Shift 中英文切换、全半角等）
        KeyboardHookService.ShiftToggled += () => ImeService.OnShiftToggled();
        KeyboardHookService.ImeStateMayHaveChanged += () =>
        {
            Logger.Log("IME", "StateMayHaveChanged fired");
            ImeService.CheckImeStateChanged();
        };

        // 输入法切换监控（Win+Space / Ctrl+Shift / Alt+Shift）
        TsfImeMonitorService.ImeProfileChanged += name =>
            DispatcherQueue.TryEnqueue(() =>
                HotKeyToast.Show("输入法", name));
        KeyboardHookService.WinSpaceDetected += () =>
        {
            ImeService.ClearImeChangeEvent();
            ImeService.SuppressChangeDetection();
            TsfImeMonitorService.OnWinSpaceDetected();
        };
        TsfImeMonitorService.Start();

        StartThemeListener();
    }

    public const int GLOBAL_HOTKEY_ID = 9001;
    public const int ALTSPACE_HOTKEY_ID = 9002;

    public static void RegisterTriggerHotKey(int id, uint modifiers, uint virtualKey)
    {
        RegisterHotKey(WindowHandle, id, modifiers, virtualKey);
    }

    public static void UnregisterTriggerHotKey(int id)
    {
        UnregisterHotKey(WindowHandle, id);
    }

    private static void OnLaunchpadTriggered(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => App.LaunchpadWindow.Open());
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var hwnd = FindWindowW(null, "WinAssistant - 设置");
            if (hwnd == nint.Zero)
            {
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
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;


    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica Base
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Mica BaseAlt（强制刷新用）

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

    private static ApplicationTheme _lastTheme;
    private static int _lastThemeMode; // 缓存 ThemeMode，避免每 tick 读 JSON
    private static Windows.UI.ViewManagement.UISettings? _uiSettings;

    public static ApplicationTheme GetSystemTheme()
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

    /// <summary>
    /// 切换应用主题并刷新 UI。
    /// 供 ThemeSwitcherTool 直接调用（传目标主题，不绕注册表）；
    /// 无参数重载从注册表读取（供轮询/事件使用）。
    ///
    /// 注意：不设置 App.Current.RequestedTheme —— WinUI 3 官方限制，
    /// Application.RequestedTheme 只能在启动时设置一次，运行时会抛 COMException。
    /// 改为通过 SystemThemeChanged 事件让各窗口更新元素级 RequestedTheme，
    /// {ThemeResource} 即可在根 RequestedTheme 下正确重解析。
    /// </summary>
    public static void RefreshTheme(ApplicationTheme? target = null)
    {
        var theme = target ?? GetSystemTheme();
        // 自动跟随（无 target）时才用 guard 避免冗余更新；
        // 手动指定 target 时总是执行，确保用户选择即时生效
        if (target == null && theme == _lastTheme) return;
        _lastTheme = theme;

        // 从设置同步 ThemeMode 缓存（手动切换时会更新设置文件）
        if (target != null)
        {
            try
            {
                var s = SettingsService.Load();
                _lastThemeMode = s.ThemeMode;
            }
            catch { }
        }

        // 不修改 App.Current.RequestedTheme（窗口激活后设不了），
        // 由各窗口的 SystemThemeChanged 处理器更新元素级 RequestedTheme。
        SystemThemeChanged?.Invoke(null, EventArgs.Empty);
        // 更新标题栏颜色
        UpdateTitleBarTheme();

        // 集中强制刷新所有窗口的 DWM Mica（MicaBackdrop 在主题切换时不自动刷新，
        // 导致灰色背景。此处统一处理，避免每个窗口的 handler 各自实现遗漏）
        RefreshDwmMicaForAllWindows();
    }

    /// <summary>
    /// 强制刷新所有已知窗口的 DWM Mica 效果。
    /// 主题切换时 MicaBackdrop 不会自动通知 DWM，必须手动刷新。
    /// </summary>
    private static void RefreshDwmMicaForAllWindows()
    {
        try
        {
            var backdropType = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(WindowHandle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            if (_launchpadWindow != null)
            {
                var lpHandle = WinRT.Interop.WindowNative.GetWindowHandle(_launchpadWindow);
                DwmSetWindowAttribute(lpHandle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
        }
        catch { }
    }

    /// <summary>
    /// 供外部主题切换时更新标题栏颜色。
    /// </summary>
    public static void UpdateTitleBarTheme()
    {
        if (App.Current is App app)
        {
            var isDark = CurrentTheme == ApplicationTheme.Dark;
            app.ApplyThemeToRoot(isDark ? ElementTheme.Dark : ElementTheme.Light);
        }
    }

    private void ApplyThemeToRoot()
    {
        ApplyThemeToRoot(GetSystemElementTheme());
    }

    private void ApplyThemeToRoot(ElementTheme theme)
    {
        // 启动时窗口可见，显式设置根元素主题（运行时不重复设置以避免 COMException）
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

    /// <summary>
    /// 通过 DWM API 直接为窗口启用 Mica 背景效果（不依赖 WinUI MicaBackdrop）。
    /// 适用于 Win11 22621+，与元素级 RequestedTheme 切换兼容。
    /// </summary>
    public static void SetMica(nint hwnd)
    {
        try
        {
            var backdropType = DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            // 同步当前暗色/浅色模式
            var isDark = CurrentTheme == ApplicationTheme.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// 主题切换时更新 DWM 暗色模式属性（影响 Mica 颜色和标题栏着色）。
    /// </summary>
    public static void UpdateDwmDarkMode(nint hwnd)
    {
        try
        {
            var isDark = CurrentTheme == ApplicationTheme.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));
        }
        catch { }
    }

    private void StartThemeListener()
    {
        // 初始化 ThemeMode 缓存
        try { _lastThemeMode = SettingsService.Load().ThemeMode; } catch { }

        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (_, _) =>
            {
                // ColorValuesChanged fires on a background thread — must marshal to UI thread
                DispatcherQueue?.TryEnqueue(() =>
                {
                    // 检查缓存 ThemeMode，仅在跟随系统时自动跟随
                    if (_lastThemeMode != 0) return;
                    RefreshTheme();
                });
            };
        }
        catch
        {
            // UISettings 不可用时捕获，不影响应用运行
        }

        // Fallback: poll registry every 2s for system theme change.
        // 仅在 ThemeMode=0（跟随系统）时自动跟随。
        var pollTimer = new Microsoft.UI.Xaml.DispatcherTimer();
        pollTimer.Interval = TimeSpan.FromSeconds(2);
        pollTimer.Tick += (_, _) =>
        {
            // 检查缓存 ThemeMode，避免每 tick 读磁盘 JSON
            if (_lastThemeMode != 0)
            {
                // 手动锁定模式，不跟随系统
                return;
            }

            var currentTheme = GetSystemTheme();
            if (currentTheme != _lastTheme)
            {
                RefreshTheme(currentTheme);
            }
        };
        pollTimer.Start();
    }

    /// <summary>Debug log for theme change diagnostics.</summary>
    private static void LogTheme(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "WinAssistant_theme.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    #endregion
}
