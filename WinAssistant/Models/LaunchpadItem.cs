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
}
