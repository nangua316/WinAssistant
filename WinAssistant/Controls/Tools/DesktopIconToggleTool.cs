using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace WinAssistant.Controls.Tools;

public class DesktopIconToggleTool : IAssistantTool
{
    public string Id => "desktop-icon-toggle";
    public string Name => "隐藏桌面图标";
    public string Description => "显示或隐藏桌面图标";
    public string IconGlyph => "🖥";
    public string? IconColorHex => "#FF60A5FA";

    public bool IsOneClickAction => true;

    public string? Activate()
    {
        var (isHidden, _) = GetDesktopIconState();
        SetDesktopIconVisibility(isHidden);

        var (nowHidden, _) = GetDesktopIconState();
        return nowHidden ? "已隐藏桌面图标" : "已显示桌面图标";
    }

    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() =>
        new TextBlock
        {
            Text = "隐藏桌面图标",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    public UIElement? CreateSettingsContent() => null;

    private static (bool isHidden, int rawValue) GetDesktopIconState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var value = (int)(key?.GetValue("HideIcons", 0) ?? 0);
            return (value == 1, value);
        }
        catch { return (false, 0); }
    }

    private static void SetDesktopIconVisibility(bool show)
    {
        // 1. Registry — persist the setting
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: true);
            key?.SetValue("HideIcons", show ? 0 : 1, RegistryValueKind.DWord);
        }
        catch { }

        // 2. Direct ShowWindow on the desktop icon ListView — instant effect.
        //    Walk all top-level windows to find SHELLDLL_DefView → SysListView32.
        ToggleDesktopListView(show);

        // 3. Background: notify shell via SHChangeNotify (for good measure).
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        _ = Task.Run(() =>
        {
            var pPath = Marshal.StringToHGlobalAuto(desktopPath);
            try
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_FLUSHNOWAIT | SHCNF_PATHW, pPath, nint.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pPath);
            }
        });
    }

    private static bool ToggleDesktopListView(bool show)
    {
        var hwnd = FindDesktopListView();
        if (hwnd == nint.Zero)
        {
            return false;
        }
        ShowWindow(hwnd, show ? SW_SHOW : SW_HIDE);
        return true;
    }

    /// <summary>
    /// Locate the SysListView32("FolderView") that renders desktop icons.
    /// Uses EnumWindows to probe both Progman and WorkerW parents
    /// (Windows 10+ moves the DefView from Progman into a WorkerW).
    /// </summary>
    private static nint FindDesktopListView()
    {
        nint result = nint.Zero;

        EnumWindows((hwnd, _) =>
        {
            var defView = FindWindowExW(hwnd, nint.Zero, "SHELLDLL_DefView", null);
            if (defView != nint.Zero)
            {
                // The desktop SysListView32 has window text "FolderView"
                var lv = FindWindowExW(defView, nint.Zero, "SysListView32", "FolderView");
                if (lv != nint.Zero)
                {
                    result = lv;
                    return false; // stop enumeration
                }
            }
            return true; // continue
        }, nint.Zero);

        return result;
    }

    // ── Win32 ──

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint hwndParent, nint hwndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint SHCNE_UPDATEDIR = 0x00000800;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSHNOWAIT = 0x2000;
}
