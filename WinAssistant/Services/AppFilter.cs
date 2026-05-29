using System.Diagnostics;
using Microsoft.Win32;

namespace WinAssistant.Services;

/// <summary>
/// Centralized filter rules for deciding which scan results are "normal" apps vs "hidden" (system) entries.
/// Modify this file to add/remove filter rules without touching the scanner logic.
/// </summary>
public static class AppFilter
{
    // Known non-app entries to exclude from results
    public static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "“添加文件夹建议”对话框",
    };

    // PackagedApp name patterns that indicate a system dialog/component (not a real app)
    public static readonly string[] SystemAppSuffixes =
    {
        "对话框", "操作员消息", "打印队列", "网络连接流", "强制网络门户流",
        "电子邮件和账户", "工作或学校账户", "安全删除设备",
        "管理移动设备", "应用解析程序", "桌面应用 Web 查看器",
        "凭据对话框", "移动设备", "反馈中心", "讲述人",
        "目视控制", "相机控制",
        // Additional system components
        "体验主机", "运行时", "LifetimeManager", "Singleton",
        "COM Server", "MCP Server",
    };

    // PackagedApp exact names that are system components (not user apps)
    public static readonly string[] SystemComponentNames =
    {
        "CapturePicker",
        "DesktopPackageMetadata",
        "WidgetsPlatformRuntime",
        "Game Speech Window",
        "PCyybContextMenuApp1",
        "PCyybContextMenuApp2",
        "PCyybContextMenuApp3",
        "Ink.Handwriting.Main.Store.zh-Hans.1.0",
        "应用安装程序",
        "Windows 程序包管理器客户端",
        "Microsoft 必应",
        "Microsoft Office",
    };

    // Keywords in Registry App Paths names that indicate a system component (not user-launchable)
    public static readonly string[] RegistryAppPathsComponentKeywords =
    {
        " Helper",
        " Handler",
        " Component",
        " Host",
        " Server",
        " Utility",
        " Diagnostics",
        " Extension",
    };

    // Exact-name overrides for Registry App Paths entries that keywords don't catch
    public static readonly string[] RegistryAppPathsComponentExact =
    {
        "Microsoft (R) Contacts Import Tool",
    };

    // Names from Registry Uninstall that are system services or drivers (not user-launchable apps)
    public static readonly string[] UninstallServiceNames =
    {
        "HonorAPOService",
    };

    /// <summary>
    /// Check if a PackagedApp/StartApp entry is a system dialog or component.
    /// </summary>
    public static bool IsSystemDialogEntry(string name, string source)
    {
        if (source != "PackagedApp" && source != "StartApp") return false;

        // Names wrapped in full-width quotes → system dialog entry
        if (name.StartsWith("“") || name.StartsWith("‘"))
            return true;

        // Check exact match system component names
        foreach (var comp in SystemComponentNames)
        {
            if (string.Equals(name, comp, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Names matching known system component patterns
        foreach (var suffix in SystemAppSuffixes)
        {
            if (name.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // "适用于 X 的 Y" pattern — Edge addons for system features
        if (name.StartsWith("适用于", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Check if a file is an uninstaller (unins*.exe, uninstall.exe, installpre.exe, etc.)
    /// </summary>
    public static bool IsUninstaller(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var name = fileName.ToLowerInvariant();
        if (name.Contains("unins") || name == "uninstall" ||
            name == "installpre" ||
            path.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase))
            return true;

        // Chinese uninstaller: 卸载 means uninstall
        if (fileName.Contains("卸载"))
            return true;

        return false;
    }

    /// <summary>
    /// Check if a file is a Microsoft Windows system binary (signed by Microsoft).
    /// Third-party apps installed into System32/SysWOW64 are NOT considered system binaries.
    /// </summary>
    public static bool IsWindowsSystemBinary(string path)
    {
        // Only applies to files inside the Windows directory
        if (!path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            var company = fvi.CompanyName;
            if (!string.IsNullOrEmpty(company) &&
                company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                return true;
            return false; // Non-Microsoft file in Windows dir → third-party app
        }
        catch
        {
            // If we can't read version info, be safe and treat it as system binary
            return true;
        }
    }

    /// <summary>
    /// Check registry Uninstall key for system update/component markers.
    /// </summary>
    public static bool IsSystemComponent(RegistryKey key)
    {
        // Skip Windows updates, hotfixes, system components
        if (key.GetValue("ParentKeyName") != null) return true;
        if (key.GetValue("ReleaseType") is string rt &&
            (rt.Equals("Update", StringComparison.OrdinalIgnoreCase) ||
             rt.Equals("Hotfix", StringComparison.OrdinalIgnoreCase) ||
             rt.Equals("SecurityUpdate", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Skip entries marked as system components (the most common case)
        if (key.GetValue("SystemComponent") is int systemComp && systemComp == 1)
            return true;

        return false;
    }

    /// <summary>
    /// Check if a Registry App Paths name matches known component patterns.
    /// </summary>
    public static bool IsRegistryAppPathsComponent(string name)
    {
        foreach (var keyword in RegistryAppPathsComponentKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var exact in RegistryAppPathsComponentExact)
        {
            if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if a Registry Uninstall name is a background service (not user-launchable).
    /// </summary>
    public static bool IsUninstallService(string name)
    {
        // General rule: names ending with "Service" are background services, not user apps
        if (name.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
            return true;

        // Specific known service entries
        foreach (var svc in UninstallServiceNames)
        {
            if (string.Equals(name, svc, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if the exe filename indicates a background daemon/service process (not user-launchable).
    /// </summary>
    public static bool IsDaemonProcess(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Contains("Daemon", StringComparison.OrdinalIgnoreCase))
            return true;
        // Background services (*Service.exe) can't be usefully launched as apps
        if (fileName.EndsWith("Service", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            return true;
        // Background server processes (*_server.exe, *Server.exe) — e.g. IME engines
        if (fileName.EndsWith("_server", StringComparison.OrdinalIgnoreCase) ||
            (fileName.EndsWith("Server", StringComparison.OrdinalIgnoreCase) &&
             !fileName.Equals("Server", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }
}
