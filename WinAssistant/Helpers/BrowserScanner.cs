using Microsoft.Win32;

namespace WinAssistant.Helpers;

/// <summary>
/// Scans the local machine for installed web browsers.
/// </summary>
public static class BrowserScanner
{
    public record BrowserInfo(string Name, string Path);

    /// <summary>
    /// Returns a list of installed browsers plus a sentinel entry for the system default.
    /// The system default entry has an empty Path.
    /// </summary>
    public static List<BrowserInfo> ScanInstalledBrowsers()
    {
        var browsers = new Dictionary<string, BrowserInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Registry-based discovery (the canonical Windows way).
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var root = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
            if (root == null) continue;

            foreach (var browserKeyName in root.GetSubKeyNames())
            {
                try
                {
                    using var browserKey = root.OpenSubKey(browserKeyName);
                    using var commandKey = browserKey?.OpenSubKey(@"shell\open\command");
                    var command = commandKey?.GetValue(null)?.ToString();
                    if (string.IsNullOrWhiteSpace(command)) continue;

                    var path = ExtractExePath(command);
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                    var name = browserKey?.GetValue(null)?.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        name = Path.GetFileNameWithoutExtension(path);

                    browsers[path] = new BrowserInfo(NormalizeName(name), path);
                }
                catch { }
            }
        }

        // 2. Well-known paths as fallback for browsers that may not register above.
        var wellKnown = new[]
        {
            ("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
            ("Microsoft Edge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),
            ("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            ("Google Chrome", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"),
            ("Google Chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")),
            ("Mozilla Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
            ("Mozilla Firefox", @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe"),
            ("Brave", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"),
            ("Brave", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe")),
            ("Opera", @"C:\Users\AppData\Local\Programs\Opera\opera.exe"),
            ("Opera GX", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Opera GX\opera.exe")),
            ("Vivaldi", @"C:\Program Files\Vivaldi\Application\vivaldi.exe"),
            ("Vivaldi", @"C:\Program Files (x86)\Vivaldi\Application\vivaldi.exe"),
            ("360安全浏览器", @"C:\Program Files (x86)\360\360se6\Application\360se.exe"),
            ("360极速浏览器", @"C:\Program Files (x86)\360\360Chrome\Chrome\Application\360chrome.exe"),
            ("QQ浏览器", @"C:\Program Files (x86)\Tencent\QQBrowser\QQBrowser.exe"),
            ("搜狗浏览器", @"C:\Program Files (x86)\SogouExplorer\SogouExplorer.exe"),
            ("UC浏览器", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"UCBrowser\Application\UCBrowser.exe")),
        };

        foreach (var (name, path) in wellKnown)
        {
            if (File.Exists(path))
                browsers[path] = new BrowserInfo(name, path);
        }

        // 3. Sort alphabetically by name, keeping the system-default sentinel separate.
        var result = browsers.Values
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList<BrowserInfo>();

        return result;
    }

    /// <summary>
    /// Extracts the executable path from a command string, stripping quotes and arguments.
    /// </summary>
    private static string? ExtractExePath(string command)
    {
        var trimmed = command.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        string path;
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            path = endQuote > 0 ? trimmed[1..endQuote] : trimmed.Trim('"');
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            path = space > 0 ? trimmed[..space] : trimmed;
        }

        // Some commands are .dll with "rundll32" style; skip those.
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return null;

        // Expand environment variables just in case.
        path = Environment.ExpandEnvironmentVariables(path);
        return path;
    }

    private static string NormalizeName(string name)
    {
        // Remove trailing "..." or redundant "(x86)" noise from registry display names.
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        return name;
    }
}
