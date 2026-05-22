using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using WinAssistant.Models;

namespace WinAssistant.Services;

public static class AppScanner
{
    private static readonly string? LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinAssistant", "scanner_debug.log");

    private static List<InstalledAppInfo>? _cachedApps;
    private static readonly System.Threading.SemaphoreSlim _scanLock = new(1, 1);

    /// <summary>Pre-fill cache on a background thread so subsequent calls are instant.</summary>
    public static void PreloadCache()
    {
        if (_cachedApps != null) return;
        Task.Run(async () =>
        {
            if (!await _scanLock.WaitAsync(0)) return; // another scan already in progress
            try
            {
                if (_cachedApps != null) return; // double-check after acquiring lock
                _cachedApps = ScanInstalledAppsCore();
            }
            finally { _scanLock.Release(); }
        });
    }

    public static List<InstalledAppInfo> ScanInstalledApps()
    {
        if (_cachedApps != null)
            return _cachedApps;

        _scanLock.Wait();
        try
        {
            if (_cachedApps != null) return _cachedApps; // double-check
            _cachedApps = ScanInstalledAppsCore();
        }
        finally { _scanLock.Release(); }
        return _cachedApps;
    }

    /// <summary>Force a fresh scan on next access.</summary>
    public static void InvalidateCache() => _cachedApps = null;

    /// <summary>Invalidate and re-scan immediately. Returns the fresh app list.</summary>
    public static async Task<List<InstalledAppInfo>> RefreshCacheAsync()
    {
        await _scanLock.WaitAsync();
        try
        {
            _cachedApps = ScanInstalledAppsCore();
            return _cachedApps;
        }
        finally { _scanLock.Release(); }
    }

    private static List<InstalledAppInfo> ScanInstalledAppsCore()
    {
        var apps = new Dictionary<string, InstalledAppInfo>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> source

        // 1. Start Menu & Desktop shortcuts
        var shortcutPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        foreach (var path in shortcutPaths)
        {
            if (Directory.Exists(path))
                ScanShortcuts(path, apps, sources);
        }

        // 2. Collect package info once (shared between ScanPackagedApps and ScanStartApps)
        var packageLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ScanPackagedApps(apps, sources, packageLocations);

        // 3. Get-StartApps (additional UWP apps visible in Start Menu)
        ScanStartApps(apps, sources, packageLocations);

        // 4. Common system utilities (hard-coded with localized names)
        ScanSystemPrograms(apps, sources);

        // 5. Registry Uninstall keys
        ScanRegistryUninstall(apps, sources);

        // 6. Registry App Paths (for well-known exes)
        ScanRegistryAppPaths(apps, sources);

        // 7. Post-process: merge launcher entries with their main app in the same directory
        MergeLauncherEntries(apps);

        // 8. Populate usage counts from Windows FeatureUsage tracking
        var usageCounts = GetAppUsageCounts();
        int matched = 0, total = 0;
        foreach (var app in apps.Values)
        {
            total++;
            if (usageCounts.TryGetValue(app.AppPath, out var count))
            {
                app.UsageCount = count;
                matched++;
            }
        }

        // Log sources for all entries to debug file
        LogSources(sources);

        // Basic validity: must have an existing .exe path, not a Windows system binary
        var valid = apps.Values
            .Where(a => !string.IsNullOrEmpty(a.AppPath) && File.Exists(a.AppPath) && a.AppPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(a => !AppFilter.IsWindowsSystemBinary(a.AppPath))
            .ToList();

        // Debug log
        try
        {
            var dir = System.IO.Path.GetDirectoryName(LogPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var logLines = new List<string> { $"=== AppScanner Debug {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===" };
            foreach (var a in apps.Values.OrderBy(a => a.Name))
            {
                var src = sources.GetValueOrDefault(a.Name, "?");
                var isFiltered = AppFilter.ExcludedNames.Contains(a.Name)
                    || AppFilter.IsUninstaller(a.AppPath)
                    || AppFilter.IsDaemonProcess(a.AppPath)
                    || AppFilter.IsSystemDialogEntry(a.Name, sources.GetValueOrDefault(a.Name, ""));
                var passes = valid.Contains(a) && !isFiltered ? " [PASSES]" : " [FILTERED]";
                logLines.Add($"[{src}] {a.Name} -> {a.AppPath}{passes}");
            }
            File.WriteAllLines(LogPath, logLines);

            // USAGE summary: append at the end so it's always visible in the log
            File.AppendAllText(LogPath,
                $"\n[USAGE] matched {matched}/{total} apps, usageCounts has {usageCounts.Count} entries\n");

            // Log top matched apps for debugging
            var topMatches = apps.Values
                .Where(a => a.UsageCount > 0)
                .OrderByDescending(a => a.UsageCount)
                .Take(10)
                .Select(a => $"  {a.UsageCount,6}  {a.Name}");
            foreach (var line in topMatches)
                File.AppendAllText(LogPath, "[USAGE] " + line + "\n");
        }
        catch { }

        // Valid entries, filtered, dedup by path, sorted by name
        return [.. valid
            .Where(a => !AppFilter.ExcludedNames.Contains(a.Name))
            .Where(a => !AppFilter.IsUninstaller(a.AppPath))
            .Where(a => !AppFilter.IsDaemonProcess(a.AppPath))
            .Where(a => !AppFilter.IsSystemDialogEntry(a.Name, sources.GetValueOrDefault(a.Name, "")))
            .DistinctBy(a => a.AppPath.ToLowerInvariant())
            .OrderBy(a => a.Name)];
    }

    private static void LogSources(Dictionary<string, string> sources)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(LogPath);
            if (dir != null) Directory.CreateDirectory(dir);

            var lines = new List<string>
            {
                $"=== Scanner Debug Log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"Total entries: {sources.Count}"
            };
            foreach (var kvp in sources.OrderBy(k => k.Key))
            {
                lines.Add($"[{kvp.Value}] {kvp.Key}");
            }
            File.AppendAllLines(LogPath, lines);   // append so USAGE data isn't lost
        }
        catch { }
    }

    private static void ScanShortcuts(string directory, Dictionary<string, InstalledAppInfo> apps, Dictionary<string, string> sources)
    {
        try
        {
            foreach (var lnkFile in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    var (target, args, iconPath) = ResolveShortcut(lnkFile);
                    if (string.IsNullOrEmpty(target)) continue;

                    var ext = Path.GetExtension(target).ToLowerInvariant();
                    if (ext != ".exe") continue;
                    if (AppFilter.IsUninstaller(target)) continue;

                    var name = Path.GetFileNameWithoutExtension(lnkFile);
                    if (AddOrUpdateApp(apps, name, target, iconPath, arguments: args, shortcutPath: lnkFile))
                        sources[name] = "StartMenu";
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanPackagedApps(Dictionary<string, InstalledAppInfo> apps, Dictionary<string, string> sources, Dictionary<string, string> packageLocations)
    {
        // Step 1: Get package info via PowerShell
        var pkgInfos = new Dictionary<string, (string installLocation, string familyName)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"$OutputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; &{Get-AppxPackage|Select-Object InstallLocation,PackageFamilyName,IsFramework,IsResourcePackage|ConvertTo-Json -Compress}\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc != null)
            {
                var json = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15000);
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            try
                            {
                                if (item.TryGetProperty("IsFramework", out var fw) && fw.GetBoolean()) continue;
                                if (item.TryGetProperty("IsResourcePackage", out var rp) && rp.GetBoolean()) continue;

                                var installLocation = item.GetProperty("InstallLocation").GetString();
                                var familyName = item.GetProperty("PackageFamilyName").GetString();
                                if (!string.IsNullOrEmpty(installLocation) && !string.IsNullOrEmpty(familyName))
                                {
                                    pkgInfos[familyName] = (installLocation, familyName);
                                    packageLocations[familyName] = installLocation;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        if (pkgInfos.Count == 0) return;

        // Step 2: Enumerate Packages from PowerShell output, read manifests
        foreach (var kvp in pkgInfos)
        {
            try
            {
                var (installLocation, familyName) = kvp.Value;
                var manifestPath = System.IO.Path.Combine(installLocation, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) continue;

                var manifest = XDocument.Load(manifestPath);
                foreach (var appElement in manifest.Descendants())
                {
                    if (appElement.Name.LocalName != "Application") continue;
                    var exe = appElement.Attribute("Executable")?.Value;
                    var appId = appElement.Attribute("Id")?.Value;
                    if (string.IsNullOrEmpty(exe)) continue;

                    var exePath = System.IO.Path.Combine(installLocation, exe);
                    var aumid = $"{familyName}!{appId}";
                    var displayName = ResolvePackageDisplayName(manifest, familyName, exePath, aumid);
                    if (string.IsNullOrEmpty(displayName))
                        displayName = System.IO.Path.GetFileNameWithoutExtension(exe);

                    if (AddOrUpdateApp(apps, displayName, exePath, exePath, aumid))
                        sources[displayName] = "PackagedApp";
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Get additional UWP apps from Get-StartApps, using a single batch query.
    /// </summary>
    private static void ScanStartApps(Dictionary<string, InstalledAppInfo> apps, Dictionary<string, string> sources, Dictionary<string, string> packageLocations)
    {
        // Get-StartApps (works for both UWP and Win32 entries)
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"$OutputEncoding=[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; &{Get-StartApps|Select-Object Name,AppID|ConvertTo-Json -Compress}\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc == null) return;

            var json = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            if (string.IsNullOrEmpty(json)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : [];

            foreach (var item in items)
            {
                try
                {
                    var rawName = item.GetProperty("Name").GetString() ?? "";
                    var appId = item.GetProperty("AppID").GetString() ?? "";
                    if (string.IsNullOrEmpty(rawName) || string.IsNullOrEmpty(appId)) continue;

                    // UWP apps have "!" in AppID (AUMID format: PackageFamily!AppId)
                    if (appId.Contains('!'))
                    {
                        if (apps.Values.Any(a => a.Aumid?.Equals(appId, StringComparison.OrdinalIgnoreCase) == true))
                            continue;

                        // Resolve display name
                        var displayName = rawName;
                        if (displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var shellType = Type.GetTypeFromProgID("Shell.Application");
                                if (shellType != null)
                                {
                                    dynamic shell = Activator.CreateInstance(shellType);
                                    dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
                                    dynamic shellItem = appsFolder.ParseName(appId);
                                    if (shellItem != null)
                                    {
                                        var n = (string)shellItem.Name;
                                        if (!string.IsNullOrEmpty(n)) displayName = n;
                                    }
                                }
                            }
                            catch { }
                        }
                        if (string.IsNullOrEmpty(displayName) || displayName.StartsWith("ms-resource:")) continue;

                        // Look up install location from batch
                        var exclamationIdx = appId.LastIndexOf('!');
                        var packageFamily = exclamationIdx > 0 ? appId[..exclamationIdx] : "";
                        if (string.IsNullOrEmpty(packageFamily) || !packageLocations.TryGetValue(packageFamily, out var installLocation))
                            continue;

                        // Read manifest for exe path
                        var manifestPath = System.IO.Path.Combine(installLocation, "AppxManifest.xml");
                        if (!File.Exists(manifestPath)) continue;

                        var manifest = XDocument.Load(manifestPath);
                        foreach (var appElement in manifest.Descendants())
                        {
                            if (appElement.Name.LocalName != "Application") continue;
                            var exe = appElement.Attribute("Executable")?.Value;
                            if (string.IsNullOrEmpty(exe)) continue;

                            var exePath = System.IO.Path.Combine(installLocation, exe);
                            if (File.Exists(exePath) && AddOrUpdateApp(apps, displayName, exePath, exePath, appId))
                                sources[displayName] = "StartApp";
                            break;
                        }
                    }
                    // Win32 apps: AppID is a file path (e.g. "D:\APP\bilibili\xxx.exe")
                    else if (appId.Length > 3 && appId[1] == ':' && appId[2] == '\\' && File.Exists(appId))
                    {
                        // Skip if already added by another source
                        var nameFromPath = Path.GetFileNameWithoutExtension(appId);
                        if (string.IsNullOrEmpty(nameFromPath) || AppFilter.IsUninstaller(appId))
                            continue;
                        if (apps.ContainsKey(nameFromPath) || apps.Values.Any(a => a.AppPath.Equals(appId, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Use filename from path (more reliable than rawName, which can garble CJK)
                        if (AddOrUpdateApp(apps, nameFromPath, appId, appId))
                            sources[nameFromPath] = "StartApp";
                    }
                    // Custom AppUserModelID (e.g., "BiliBiliPC") — not UWP, not a file path
                    else
                    {
                        if (apps.ContainsKey(rawName)) continue;
                        if (apps.Values.Any(a => a.Aumid?.Equals(appId, StringComparison.OrdinalIgnoreCase) == true)) continue;

                        try
                        {
                            var shellType = Type.GetTypeFromProgID("Shell.Application");
                            if (shellType == null) continue;

                            dynamic shell = Activator.CreateInstance(shellType);
                            dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
                            dynamic shellItem = appsFolder.ParseName(appId);
                            if (shellItem == null) continue;

                            var displayName = (string)shellItem.Name;
                            if (string.IsNullOrEmpty(displayName)) displayName = rawName;

                            string exePath = "";
                            try
                            {
                                var rawPath = (string)shellItem.Path;
                                if (!string.IsNullOrEmpty(rawPath)
                                    && !rawPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                                    && File.Exists(rawPath))
                                    exePath = rawPath;
                            }
                            catch { }

                            // Fallback: try App Paths registry with AppID as exe name
                            if (string.IsNullOrEmpty(exePath))
                            {
                                try
                                {
                                    using var key = Registry.LocalMachine.OpenSubKey(
                                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{appId}.exe");
                                    if (key != null)
                                    {
                                        var val = key.GetValue("") as string;
                                        if (!string.IsNullOrEmpty(val) && File.Exists(val))
                                            exePath = val;
                                    }
                                }
                                catch { }
                            }

                            // Fallback: AppID in "{KnownFolderGUID}\relative\path\exe.exe" format
                            if (string.IsNullOrEmpty(exePath) && appId.Length > 40 && appId[0] == '{')
                            {
                                var braceEnd = appId.IndexOf('}', 1);
                                if (braceEnd > 0 && appId.Length > braceEnd + 1 && appId[braceEnd + 1] == '\\')
                                {
                                    var guid = appId[..(braceEnd + 1)];
                                    var relativePath = appId[(braceEnd + 2)..];
                                    if (KnownFolderIds.TryGetValue(guid, out var basePath))
                                    {
                                        var candidate = Path.Combine(basePath, relativePath);
                                        if (File.Exists(candidate) && !AppFilter.IsUninstaller(candidate))
                                            exePath = candidate;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) && !AppFilter.IsUninstaller(exePath))
                            {
                                if (apps.Values.Any(a => a.AppPath.Equals(exePath, StringComparison.OrdinalIgnoreCase)))
                                    continue;

                                if (AddOrUpdateApp(apps, displayName, exePath, exePath, appId))
                                    sources[displayName] = "StartApp";
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static string ResolvePackageDisplayName(XDocument manifest, string packageFamily, string exePath, string aumid)
    {
        // Get VisualElements DisplayName (any namespace: uap, default, etc.)
        var ve = manifest.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "VisualElements");
        var displayName = ve?.Attribute("DisplayName")?.Value;

        if (string.IsNullOrEmpty(displayName))
        {
            // Try Properties/DisplayName
            displayName = manifest.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "DisplayName"
                                  && e.Parent?.Name.LocalName == "Properties")
                ?.Value;
        }

        // If it's a plain string (not ms-resource), resolve via Shell COM
        if (!string.IsNullOrEmpty(displayName) && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return displayName;

        // Try Shell COM for localized display name from shell:AppsFolder
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
                dynamic item = appsFolder.ParseName(aumid);
                if (item != null)
                {
                    string name = item.Name;
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }
        catch { }

        // Fallback: FileVersionInfo
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrEmpty(fvi.FileDescription))
                return fvi.FileDescription;
        }
        catch { }

        return "";
    }

    private static void ScanRegistryUninstall(Dictionary<string, InstalledAppInfo> apps, Dictionary<string, string> sources)
    {
        var registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var regPath in registryPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(regPath);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var displayName = subKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(displayName)) continue;

                        // Skip system components, updates, hotfixes
                        if (AppFilter.IsSystemComponent(subKey)) continue;

                        // Clean trailing version/build suffixes from display name
                        displayName = StripRegistryVersionSuffix(displayName);

                        // Skip known system services (e.g., "HonorAPOService")
                        if (AppFilter.IsUninstallService(displayName)) continue;

                        var installLocation = subKey.GetValue("InstallLocation") as string ?? "";
                        var displayIcon = subKey.GetValue("DisplayIcon") as string ?? "";
                        var uninstallString = subKey.GetValue("UninstallString") as string ?? "";

                        // Skip Chromium web apps (PWAs installed via browser), they have no standalone exe
                        if (!string.IsNullOrEmpty(uninstallString) && uninstallString.Contains("--uninstall-app-id", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Resolve the exe path: icon often contains the exe path
                        var exePath = ResolveExeFromRegistry(displayIcon, installLocation, displayName);

                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                        {
                            // Try to find exe in install location
                            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                            {
                                var foundExe = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                                    .FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    ?? Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                                        .FirstOrDefault(e => !AppFilter.IsUninstaller(e));
                                if (foundExe != null) exePath = foundExe;
                            }
                        }

                        // Fallback: extract directory from UninstallString and search for app exe
                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                        {
                            if (!string.IsNullOrEmpty(uninstallString))
                            {
                                var uninstExe = ParseUninstallExePath(uninstallString);
                                var uninstDir = !string.IsNullOrEmpty(uninstExe) ? Path.GetDirectoryName(uninstExe) : null;
                                if (!string.IsNullOrEmpty(uninstDir) && Directory.Exists(uninstDir))
                                {
                                    var foundExe = Directory.EnumerateFiles(uninstDir, "*.exe", SearchOption.TopDirectoryOnly)
                                        .FirstOrDefault(e => !AppFilter.IsUninstaller(e));
                                    if (foundExe != null) exePath = foundExe;
                                }
                            }
                        }

                        // Safety net: if resolved exe is an uninstaller, search broader for the real app exe
                        if (!string.IsNullOrEmpty(exePath) && AppFilter.IsUninstaller(exePath))
                        {
                            var searchDir = Path.GetDirectoryName(exePath);
                            if (!string.IsNullOrEmpty(searchDir) && Directory.Exists(searchDir))
                            {
                                var foundExe = Directory.EnumerateFiles(searchDir, "*.exe", SearchOption.TopDirectoryOnly)
                                    .FirstOrDefault(e => !AppFilter.IsUninstaller(e) && !e.Equals(exePath, StringComparison.OrdinalIgnoreCase));
                                if (foundExe != null) exePath = foundExe;
                            }
                        }

                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            var iconPath = displayIcon.Replace('/', '\\');
                            // Clean icon path (remove icon index like ",0")
                            if (!string.IsNullOrEmpty(iconPath))
                            {
                                var commaIdx = iconPath.IndexOf(',');
                                if (commaIdx > 0) iconPath = iconPath[..commaIdx].Trim();
                                if (!File.Exists(iconPath)) iconPath = exePath;
                            }
                            else
                            {
                                iconPath = exePath;
                            }

                            if (AddOrUpdateApp(apps, displayName, exePath, iconPath))
                                sources[displayName] = "RegistryUninstall";
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Also check HKCU
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key != null)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;
                        var displayName = subKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(displayName)) continue;
                        if (AppFilter.IsSystemComponent(subKey)) continue;

                        // Clean trailing version/build suffixes from display name
                        displayName = StripRegistryVersionSuffix(displayName);

                        // Skip known system services (e.g., "HonorAPOService")
                        if (AppFilter.IsUninstallService(displayName)) continue;

                        var displayIcon = subKey.GetValue("DisplayIcon") as string ?? "";
                        var installLocation = subKey.GetValue("InstallLocation") as string ?? "";
                        var uninstallString = subKey.GetValue("UninstallString") as string ?? "";

                        // Skip Chromium web apps (PWAs installed via browser)
                        if (!string.IsNullOrEmpty(uninstallString) && uninstallString.Contains("--uninstall-app-id", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var exePath = ResolveExeFromRegistry(displayIcon, installLocation, displayName);

                        // Try to find exe in install location (same as HKLM branch)
                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                        {
                            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                            {
                                var foundExe = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                                    .FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    ?? Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                                        .FirstOrDefault(e => !AppFilter.IsUninstaller(e));
                                if (foundExe != null) exePath = foundExe;
                            }
                        }

                        // Fallback: extract directory from UninstallString and search for app exe
                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                        {
                            if (!string.IsNullOrEmpty(uninstallString))
                            {
                                var uninstExe = ParseUninstallExePath(uninstallString);
                                var uninstDir = !string.IsNullOrEmpty(uninstExe) ? Path.GetDirectoryName(uninstExe) : null;
                                if (!string.IsNullOrEmpty(uninstDir) && Directory.Exists(uninstDir))
                                {
                                    var foundExe = Directory.EnumerateFiles(uninstDir, "*.exe", SearchOption.TopDirectoryOnly)
                                        .FirstOrDefault(e => !AppFilter.IsUninstaller(e));
                                    if (foundExe != null) exePath = foundExe;
                                }
                            }
                        }

                        // Safety net: if resolved exe is an uninstaller, search broader for the real app exe
                        if (!string.IsNullOrEmpty(exePath) && AppFilter.IsUninstaller(exePath))
                        {
                            var searchDir = Path.GetDirectoryName(exePath);
                            if (!string.IsNullOrEmpty(searchDir) && Directory.Exists(searchDir))
                            {
                                var foundExe = Directory.EnumerateFiles(searchDir, "*.exe", SearchOption.TopDirectoryOnly)
                                    .FirstOrDefault(e => !AppFilter.IsUninstaller(e) && !e.Equals(exePath, StringComparison.OrdinalIgnoreCase));
                                if (foundExe != null) exePath = foundExe;
                            }
                        }

                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            if (AddOrUpdateApp(apps, displayName, exePath, displayIcon))
                                sources[displayName] = "RegistryUninstall-HKCU";
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void ScanRegistryAppPaths(Dictionary<string, InstalledAppInfo> apps, Dictionary<string, string> sources)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key == null) return;

            foreach (var exeName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(exeName);
                    if (subKey == null) continue;

                    var path = (subKey.GetValue("") as string ?? subKey.GetValue("Path") as string) ?? "";
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    if (AppFilter.IsUninstaller(path)) continue;
                    path = path.Replace('/', '\\');

                    var name = Path.GetFileNameWithoutExtension(exeName);
                    // Try to get a proper display name from the exe's file description
                    // (e.g., "POWERPNT" → "Microsoft PowerPoint")
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(path);
                        if (!string.IsNullOrEmpty(fvi.FileDescription))
                            name = fvi.FileDescription;
                    }
                    catch { }

                    // Skip known system components and helper utilities (e.g., "Microsoft Office component")
                    if (AppFilter.IsRegistryAppPathsComponent(name)) continue;

                    if (apps.TryGetValue(name, out var existing) && (string.IsNullOrEmpty(existing.AppPath) || !File.Exists(existing.AppPath)))
                    {
                        // Existing entry has stale/non-existent exe path → overwrite with valid one
                        existing.AppPath = path;
                        existing.IconPath = path;
                    }
                    else if (!apps.ContainsKey(name))
                    {
                        apps[name] = new InstalledAppInfo
                        {
                            Name = name,
                            AppPath = path,
                            IconPath = path
                        };
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanSystemPrograms(Dictionary<string, InstalledAppInfo> apps, Dictionary<string, string> sources)
    {
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);

        (string exe, string name)[] commonExes = [
            ("calc.exe", "计算器"),
            ("notepad.exe", "记事本"),
            ("mspaint.exe", "画图"),
            ("cmd.exe", "命令提示符"),
            ("powershell.exe", "PowerShell"),
            ("explorer.exe", "文件资源管理器"),
            ("taskmgr.exe", "任务管理器"),
            ("regedit.exe", "注册表编辑器"),
            ("control.exe", "控制面板"),
            ("cleanmgr.exe", "磁盘清理"),
        ];

        foreach (var (exe, name) in commonExes)
        {
            var path = System.IO.Path.Combine(systemDir, exe);
            if (File.Exists(path) && !apps.ContainsKey(name))
            {
                apps[name] = new InstalledAppInfo
                {
                    Name = name,
                    AppPath = path,
                    IconPath = path,
                    IconDisplayChar = name[..1]
                };
                sources[name] = "SystemProgram";
            }
        }
    }

    private static bool AddOrUpdateApp(Dictionary<string, InstalledAppInfo> apps, string name, string exePath, string iconPath, string aumid = "", string arguments = "", string shortcutPath = "")
    {
        exePath = exePath.Replace('/', '\\');
        iconPath = iconPath.Replace('/', '\\');

        // If same exe path already exists with a different name, update the existing entry's name
        // (registry names like "长安幻想.7.release" are more descriptive than generic "launcher")
        if (!apps.TryGetValue(name, out var existing))
        {
            var existingByPath = apps.Values
                .FirstOrDefault(a => string.Equals(
                    Path.GetFullPath(a.AppPath),
                    Path.GetFullPath(exePath),
                    StringComparison.OrdinalIgnoreCase));
            if (existingByPath != null)
            {
                var oldName = existingByPath.Name;

                // If one name is a prefix of the other, prefer the shorter name
                // (e.g., "极空间" vs "极空间 2.40.2026042001" → keep "极空间")
                if (name.Length != oldName.Length)
                {
                    var (shorter, longer) = name.Length < oldName.Length
                        ? (name, oldName) : (oldName, name);
                    if (longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
                    {
                        // Prefer the shorter name
                        existingByPath.Name = shorter;
                    }
                    else
                    {
                        // No prefix relationship, new name may be more descriptive
                        existingByPath.Name = name;
                    }
                }
                else
                {
                    existingByPath.Name = name;
                }

                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                    existingByPath.IconPath = iconPath;
                if (!string.IsNullOrEmpty(aumid))
                    existingByPath.Aumid = aumid;
                if (!string.IsNullOrEmpty(arguments))
                    existingByPath.Arguments = arguments;
                if (!string.IsNullOrEmpty(shortcutPath))
                    existingByPath.ShortcutPath = shortcutPath;
                if (existingByPath.Name != oldName)
                {
                    apps.Remove(oldName);
                    apps[existingByPath.Name] = existingByPath;
                }
                return true;
            }
        }

        if (!apps.TryGetValue(name, out existing))
        {
            apps[name] = new InstalledAppInfo
            {
                Name = name,
                AppPath = exePath,
                Arguments = arguments,
                ShortcutPath = shortcutPath,
                IconPath = !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath) ? iconPath : exePath,
                IconDisplayChar = name.Length > 0 ? name[..1].ToUpper() : "?",
                Aumid = aumid
            };
            return true;
        }

        if (AppFilter.IsWindowsSystemBinary(existing.AppPath))
        {
            existing.AppPath = exePath;
            existing.IconPath = !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath) ? iconPath : exePath;
            if (!string.IsNullOrEmpty(aumid)) existing.Aumid = aumid;
            if (!string.IsNullOrEmpty(arguments)) existing.Arguments = arguments;
            if (!string.IsNullOrEmpty(shortcutPath)) existing.ShortcutPath = shortcutPath;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parse the executable path from an UninstallString, handling quoted paths with spaces.
    /// Examples:
    ///   "C:\Program Files\App\uninst.exe" /SILENT  -> C:\Program Files\App\uninst.exe
    ///   "C:\Program Files (x86)\滴答清单\unins000.exe" -> C:\Program Files (x86)\滴答清单\unins000.exe
    ///   C:\Program Files\App\uninst.exe -> C:\Program Files\App\uninst.exe  (unquoted with spaces)
    ///   MsiExec.exe /X{...} -> MsiExec.exe
    ///   msiexec /I{GUID} -> msiexec
    /// </summary>
    private static string? ParseUninstallExePath(string uninstallString)
    {
        var trimmed = uninstallString.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : trimmed.Trim('"');
        }

        var spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx < 0) return trimmed;

        // First token contains no path separator → bare executable name ("MsiExec.exe /args")
        if (!trimmed[..spaceIdx].Contains('\\'))
            return trimmed[..spaceIdx];

        // First token contains '\' so spaces are part of the path itself
        // Return the full string as-is; caller will use Path.GetDirectoryName on it
        return trimmed;
    }

    private static readonly Dictionary<string, string> KnownFolderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        { "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) },
        { "{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) },
        { "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.GetFolderPath(Environment.SpecialFolder.System) },
        { "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64") },
        { "{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetFolderPath(Environment.SpecialFolder.Windows) },
    };

    private static string ResolveExeFromRegistry(string displayIcon, string installLocation, string displayName)
    {
        // DisplayIcon often contains the exe path, possibly with an icon index
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var commaIdx = displayIcon.IndexOf(',');
            var path = commaIdx > 0 ? displayIcon[..commaIdx].Trim() : displayIcon.Trim();
            if (path.StartsWith('"') && path.EndsWith('"')) path = path[1..^1];
            if (File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return path;
        }

        // Try install location + common exe name patterns
        if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
        {
            var exeName = displayName.Replace(" ", "").Replace("-", "").Replace("_", "");
            var possible = Path.Combine(installLocation, exeName + ".exe");
            if (File.Exists(possible)) return possible;

            // Try finding any exe with similar name
            var found = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(e => Path.GetFileNameWithoutExtension(e)
                    .IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (found != null) return found;
        }

        return "";
    }

    /// <summary>
    /// Merge launcher entries with their main app when both are in the same directory.
    /// e.g., "暗黑破坏神III" (Diablo III.exe) + "Diablo III Launcher" (Diablo III Launcher.exe)
    /// → redirect "暗黑破坏神III" to use the launcher exe, remove the launcher entry.
    /// Also merges same-directory entries where one name is a prefix of the other
    /// (e.g., "国机司库" + "国机集团司库信息系统" → keep the longer/descriptive name).
    /// </summary>
    private static void MergeLauncherEntries(Dictionary<string, InstalledAppInfo> apps)
    {
        // Group apps by parent directory
        var byDir = apps.Values
            .Where(a => !string.IsNullOrEmpty(a.AppPath))
            .GroupBy(a => Path.GetDirectoryName(a.AppPath))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in byDir)
        {
            // Merge launchers: rename path → launcher, remove launcher entry
            var launchers = group.Where(a =>
            {
                var name = Path.GetFileNameWithoutExtension(a.AppPath);
                return name.Contains("launcher", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (launchers.Count == 1)
            {
                var launcher = launchers[0];
                var mainApps = group.Where(a => a != launcher).ToList();
                if (mainApps.Count > 0)
                {
                    foreach (var main in mainApps)
                    {
                        main.AppPath = launcher.AppPath;
                        main.IconPath = launcher.AppPath;
                    }
                    apps.Remove(launcher.Name);
                    continue;
                }
            }

            // Merge same-app entries where one name is a prefix of another
            // (e.g., "国机司库" + "国机集团司库信息系统")
            var items = group.ToList();
            for (int i = 0; i < items.Count; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    var a = items[i];
                    var b = items[j];
                    if (a.AppPath.Equals(b.AppPath, StringComparison.OrdinalIgnoreCase))
                        continue; // Same exe, already handled by path-based dedup

                    string shorter, longer;
                    InstalledAppInfo keep, remove;
                    if (a.Name.Length < b.Name.Length)
                    {
                        shorter = a.Name; longer = b.Name;
                        keep = b; remove = a;
                    }
                    else if (a.Name.Length > b.Name.Length)
                    {
                        shorter = b.Name; longer = a.Name;
                        keep = a; remove = b;
                    }
                    else continue;

                    // Check if one name is a prefix of the other
                    if (longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
                    {
                        apps.Remove(remove.Name);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Strip trailing version/build suffixes from registry display names.
    /// e.g., "长安幻想.7.release" → "长安幻想", "极空间 2.40.2026042001" → "极空间",
    ///        "WPS Office (12.1.0.26373)" → "WPS Office"
    /// </summary>
    private static string StripRegistryVersionSuffix(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 3)
            return name;

        // Match: base name followed by space/dot/paren + digit(s) + dot-separated word groups at end
        var match = Regex.Match(name, @"^(.+?)\s*[\.\s\(\[\{]+\d+(\.\w+)+\s*[\)\]\}]?\s*$");
        if (match.Success)
        {
            var cleaned = match.Groups[1].Value.Trim();
            if (cleaned.Length >= 2)
                return cleaned;
        }
        return name;
    }

    private static (string target, string arguments, string iconPath) ResolveShortcut(string lnkPath)
    {
        Type? shellType = null;
        try
        {
            shellType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
        }
        catch { }

        if (shellType == null) return ("", "", "");

        var shellLink = Activator.CreateInstance(shellType);
        if (shellLink == null) return ("", "", "");

        try
        {
            var persistFile = (IPersistFile?)shellLink;
            persistFile?.Load(lnkPath, 0);

            var link = (IShellLinkW?)shellLink;
            if (link == null) return ("", "", "");

            var sb = new StringBuilder(260);
            var fd = new WIN32_FIND_DATAW();
            link.GetPath(sb, sb.Capacity, out fd, SLGP_RAWPATH);
            var targetPath = sb.ToString();

            // Get arguments
            var argsSb = new StringBuilder(260);
            link.GetArguments(argsSb, argsSb.Capacity);
            var args = argsSb.ToString().Trim();

            // Get icon location
            var iconSb = new StringBuilder(260);
            link.GetIconLocation(iconSb, iconSb.Capacity, out _);
            var iconPath = iconSb.ToString();

            return (targetPath, args, iconPath);
        }
        catch { return ("", "", ""); }
        finally
        {
            if (shellLink is IDisposable d) d.Dispose();
        }
    }

    private const uint SLGP_RAWPATH = 0x00000004;

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out] StringBuilder pszFile, int cchMaxPath, out WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out] StringBuilder pszDescription, int cchMaxDescription);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        void GetWorkingDirectory([Out] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(nint hWnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    /// <summary>
    /// Read Windows FeatureUsage/AppSwitched registry to get app usage counts.
    /// </summary>
    private static Dictionary<string, int> GetAppUsageCounts()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Cache known folder GUIDs once
        var knownFolderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched");
            if (key == null) return result;

            int totalValues = 0, skippedPid = 0, notInt = 0, notResolved = 0, notExists = 0, resolved = 0;
            foreach (var valueName in key.GetValueNames())
            {
                totalValues++;
                if (valueName.StartsWith("*PID", StringComparison.OrdinalIgnoreCase))
                {
                    skippedPid++;
                    continue;
                }

                if (key.GetValue(valueName) is not int count || count <= 0)
                {
                    notInt++;
                    continue;
                }

                var path = ResolveAppSwitchedPath(valueName, knownFolderCache);
                if (string.IsNullOrEmpty(path))
                {
                    notResolved++;
                    continue;
                }
                if (!File.Exists(path))
                {
                    notExists++;
                    continue;
                }

                resolved++;

                // Take the highest count if the same path appears multiple times
                if (!result.TryGetValue(path, out var existing) || count > existing)
                    result[path] = count;
            }
            // Write detailed scan stats to temp file (LogPath is overwritten by scanner log)
            var tmpLog = Path.Combine(Path.GetTempPath(), "WinAssistant_usage_debug.txt");
            try
            {
                File.AppendAllText(tmpLog,
                    $"[{DateTime.Now:HH:mm:ss}] scan: total={totalValues} skipPid={skippedPid} notInt={notInt} notResolved={notResolved} notExists={notExists} resolved={resolved}\n");
            }
            catch { }
        }
        catch { }

        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "WinAssistant_usage_debug.txt"),
                $"[{DateTime.Now:HH:mm:ss}] AppSwitched resolved {result.Count} paths, cache has {knownFolderCache.Count} GUIDs\n");
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Resolve an AppSwitched value name to a file path.
    /// Handles: full paths (C:\...), GUID-prefixed paths ({GUID}\...), and AUMIDs.
    /// </summary>
    private static string? ResolveAppSwitchedPath(string value, Dictionary<string, string> knownFolderCache)
    {
        // Direct file path (C:\...)
        if (value.Length > 2 && value[1] == ':' && value[2] == '\\')
            return value;

        // GUID-prefixed path: {GUID}\relative\path.exe
        if (value.StartsWith('{'))
        {
            var braceEnd = value.IndexOf('}', 1);
            if (braceEnd > 0 && value.Length > braceEnd + 1 && value[braceEnd + 1] == '\\')
            {
                var guid = value[..(braceEnd + 1)];
                var relativePath = value[(braceEnd + 2)..];

                if (!knownFolderCache.TryGetValue(guid, out var basePath))
                {
                    try
                    {
                        basePath = SHGetKnownFolderPath(guid);
                        knownFolderCache[guid] = basePath ?? "";
                    }
                    catch
                    {
                        knownFolderCache[guid] = "";
                    }
                }

                if (!string.IsNullOrEmpty(basePath))
                {
                    var fullPath = Path.Combine(basePath, relativePath);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }
        }

        return null; // AUMID or other identifier — can't resolve to a file path
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);

    private static string? SHGetKnownFolderPath(string guidString)
    {
        if (!Guid.TryParse(guidString, out var guid)) return null;
        var hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var ptr);
        if (hr == 0 && ptr != IntPtr.Zero)
        {
            var path = Marshal.PtrToStringUni(ptr);
            Marshal.FreeCoTaskMem(ptr);
            return path;
        }
        return null;
    }
}
