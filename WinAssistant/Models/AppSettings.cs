namespace WinAssistant.Models;

public class AppSettings
{
    public bool IsAutoStart { get; set; }
    public bool IsLaunchpadEnabled { get; set; }
    public List<string> MouseTriggers { get; set; } = [];
    public List<string> KeyboardTriggers { get; set; } = [];
    public string LaunchpadHotKey { get; set; } = "Ctrl+Q";
    public List<HotKeyBinding> Bindings { get; set; } = [];
    public List<LaunchpadItem> LaunchpadItems { get; set; } = [];
    public Dictionary<string, string> ToolSettings { get; set; } = [];
}
