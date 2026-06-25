namespace WinAssistant.Models;

public class LaunchpadItem
{
    public string Name { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Aumid { get; set; } = "";
    public string? ToolId { get; set; }

    /// <summary>
    /// Resolved icon path from scanner, may differ from AppPath
    /// (e.g. Electron apps where the shortcut icon points to a branded .ico).
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>Web URL shortcut. When set, this item opens the URL instead of an executable.</summary>
    public string Url { get; set; } = "";

    /// <summary>Optional browser executable to open the URL. Empty means use the system default browser.</summary>
    public string BrowserPath { get; set; } = "";
}
