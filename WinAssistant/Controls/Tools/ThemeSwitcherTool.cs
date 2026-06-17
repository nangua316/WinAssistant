using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace WinAssistant.Controls.Tools;

public class ThemeSwitcherTool : IAssistantTool
{
    public string Id => "theme-switcher";
    public string Name => "主题切换";
    public string Description => "一键切换 Windows 浅色/深色主题";
    public string IconGlyph => IsLightTheme() ? "☀" : "☽";
    public string? IconColorHex => IsLightTheme() ? "#FFFFA726" : "#FF60A5FA";

    public bool IsOneClickAction => true;

    public string? Activate()
    {
        var settings = App.SettingsService.Load();
        var isLight = IsLightTheme(); // 读注册表判断系统当前主题
        var targetValue = isLight ? 0 : 1;
        var targetTheme = isLight ? ApplicationTheme.Dark : ApplicationTheme.Light;
        // 工具永远写注册表改系统主题
        _ = Task.Run(() => SetTheme(targetValue));
        // 仅在跟随系统模式时同步改 app 主题
        if (settings.ThemeMode == 0)
            App.RefreshTheme(targetTheme);
        return isLight ? "已切换为深色模式" : "已切换为浅色模式";
    }

    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() => new ThemeSwitcherControl();

    public UIElement? CreateSettingsContent() => null;

    public static void ToggleTheme()
    {
        var settings = App.SettingsService.Load();
        var isLight = IsLightTheme(); // 读注册表判断系统当前主题
        var targetTheme = isLight ? ApplicationTheme.Dark : ApplicationTheme.Light;
        // 工具永远写注册表改系统主题
        _ = Task.Run(() => SetTheme(isLight ? 0 : 1));
        // 仅在跟随系统模式时同步改 app 主题
        if (settings.ThemeMode == 0)
            App.RefreshTheme(targetTheme);
    }

    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch { return false; }
    }

    private static void SetTheme(int value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
            if (key == null) return;
            key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
        }
        catch { }

        // Notify Windows of the theme change (may be slow — runs on background thread)
        const int HWND_BROADCAST = 0xffff;
        const uint WM_SETTINGCHANGE = 0x001A;
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, nint.Zero, "ImmersiveColorSet",
            SMTO_ABORTIFHUNG, 5000, out _);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, string lParam,
        uint fuFlags, uint uTimeout, out nint lpdwResult);

    private const uint SMTO_ABORTIFHUNG = 0x0002;
}
