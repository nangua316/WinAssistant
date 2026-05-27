namespace WinAssistant.Models;

public class LaunchpadItem
{
    public string Name { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Aumid { get; set; } = "";
    public string? ToolId { get; set; }
}
