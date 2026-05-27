using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace WinAssistant.Controls.Tools;

public class WeChatDualTool : IAssistantTool
{
    public string Id => "wechat-dual";
    public string Name => "微信双开";
    public string Description => "打开一个新的微信登录窗口";
    public string IconGlyph => "💬";
    public string? IconColorHex => "#FF07C160";

    private static readonly Lazy<string?> _weChatPath = new(FindWeChatPath);

    public bool IsOneClickAction => true;

    public string? IconExtractPath => _weChatPath.Value;

    public string? Activate()
    {
        var path = _weChatPath.Value;
        if (string.IsNullOrEmpty(path))
            return "未找到微信，请检查微信是否安装";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            return "启动微信失败";
        }

        return "正在打开新的微信窗口";
    }

    private static string? FindWeChatPath()
    {
        var exeNames = new[] { "WeChat.exe", "Weixin.exe" };

        // 1. Registry: HKLM App Paths (try both names)
        foreach (var name in exeNames)
        {
            var path = CheckRegistryPath(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
            if (path != null) return path;
        }

        // 2. Registry: HKCU App Paths
        foreach (var name in exeNames)
        {
            var path = CheckRegistryPath(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}",
                useHkcu: true);
            if (path != null) return path;
        }

        // 3. Registry: Tencent WeChat/Weixin InstallPath (HKCU)
        foreach (var subKey in new[] { @"Software\Tencent\WeChat", @"Software\Tencent\Weixin" })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey);
                if (key?.GetValue("InstallPath") is string dir && !string.IsNullOrEmpty(dir))
                {
                    foreach (var name in exeNames)
                    {
                        var exePath = Path.Combine(dir, name);
                        if (File.Exists(exePath)) return exePath;
                    }
                }
            }
            catch { }
        }

        // 4. Registry: HKLM/WOW6432Node Uninstall (try all known subkey names)
        var uninstallBases = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        var uninstallNames = new[] { "微信", "WeChat", "Weixin" };
        foreach (var baseKey in uninstallBases)
        {
            foreach (var name in uninstallNames)
            {
                var path = CheckRegistryUninstall($@"{baseKey}\{name}", exeNames);
                if (path != null) return path;
            }
        }

        // 5. Common install paths (multiple drives, both exe names)
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName.TrimEnd('\\'))
            .ToArray();

        foreach (var drive in drives)
        {
            foreach (var name in exeNames)
            {
                var candidates = new[]
                {
                    $@"{drive}\Program Files (x86)\Tencent\WeChat\{name}",
                    $@"{drive}\Program Files\Tencent\WeChat\{name}",
                    $@"{drive}\Tencent\WeChat\{name}",
                    $@"{drive}\APP\Weixin\{name}",
                    $@"{drive}\APP\WeChat\{name}",
                };
                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        // 6. Search Tencent directories broadly (both exe names)
        foreach (var drive in drives)
        {
            var searchDirs = new[]
            {
                $@"{drive}\Program Files (x86)\Tencent",
                $@"{drive}\Program Files\Tencent",
                $@"{drive}\Tencent",
                $@"{drive}\APP",
            };
            foreach (var searchDir in searchDirs)
            {
                if (!Directory.Exists(searchDir)) continue;
                foreach (var name in exeNames)
                {
                    try
                    {
                        var found = Directory.GetFiles(searchDir, name,
                            SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null) return found;
                    }
                    catch { }
                }
            }
        }

        return null;
    }

    private static string? CheckRegistryPath(string keyPath, bool useHkcu = false)
    {
        try
        {
            var root = useHkcu ? Registry.CurrentUser : Registry.LocalMachine;
            using var key = root.OpenSubKey(keyPath);
            if (key?.GetValue(null) is string exePath && File.Exists(exePath))
                return exePath;
        }
        catch { }
        return null;
    }

    private static string? CheckRegistryUninstall(string keyPath, string[] exeNames)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key == null) return null;

            var installDir = key.GetValue("InstallLocation") as string;
            if (!string.IsNullOrEmpty(installDir))
            {
                foreach (var name in exeNames)
                {
                    var exePath = Path.Combine(installDir, name);
                    if (File.Exists(exePath)) return exePath;
                }
            }

            var displayIcon = key.GetValue("DisplayIcon") as string;
            if (!string.IsNullOrEmpty(displayIcon))
            {
                // DisplayIcon may be quoted, e.g. "D:\APP\Weixin\Weixin.exe"
                var cleanPath = displayIcon.Trim('"', ' ');
                if (File.Exists(cleanPath)) return cleanPath;
            }
        }
        catch { }
        return null;
    }

    public (double width, double height) DefaultWindowSize => (320, 200);

    public UIElement CreateContent() =>
        new TextBlock
        {
            Text = "微信双开",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    public UIElement? CreateSettingsContent() => null;
}
