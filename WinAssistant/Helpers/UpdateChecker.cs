using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WinAssistant.Helpers;

/// <summary>Checks GitHub Releases for a newer version and optionally opens the download page.</summary>
internal static class UpdateChecker
{
    private static readonly string ReleaseUrl =
        "https://github.com/nangua316/WinAssistant/releases/latest";

    /// <summary>Latest tag from GitHub (e.g. "v2026.7.6.1"), or null if check failed or up-to-date.</summary>
    public static string? LatestTag { get; private set; }

    /// <summary>Call once on startup (fire-and-forget). On completion, LatestTag is set if newer.</summary>
    public static async Task CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WinAssistant/1.0");
            var json = await http.GetStringAsync(
                "https://api.github.com/repos/nangua316/WinAssistant/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tag)) return;

            // Strip "v" prefix and compare
            var tagVer = tag[0] == 'v' ? tag[1..] : tag;
            var curVer = Assembly.GetExecutingAssembly().GetName().Version;
            if (curVer != null && Version.TryParse(tagVer, out var remote) && remote > curVer)
                LatestTag = tag;
        }
        catch
        {
            // Network / parse errors are non-critical — skip silently.
        }
    }

    /// <summary>Open the GitHub Releases page in the default browser.</summary>
    public static void OpenReleasePage() =>
        Process.Start(new ProcessStartInfo(ReleaseUrl) { UseShellExecute = true });
}
