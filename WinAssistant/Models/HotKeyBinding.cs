namespace WinAssistant.Models;

public class HotKeyBinding
{
    public string Name { get; set; } = string.Empty;
    public string AppPath { get; set; } = string.Empty;
    public string? IconPath { get; set; }
    public string Arguments { get; set; } = string.Empty;
    public string ShortcutPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Aumid { get; set; } = "";
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string HotKeyDisplay { get; set; } = string.Empty;
    public int HotKeyId { get; set; } = -1;
}
