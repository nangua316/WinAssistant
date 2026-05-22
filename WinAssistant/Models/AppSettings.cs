namespace WinAssistant.Models;

public class AppSettings
{
    public bool IsAutoStart { get; set; }
    public bool IsLaunchpadEnabled { get; set; }
    public string LaunchpadTrigger { get; set; } = "DoubleCtrl";
    public List<HotKeyBinding> Bindings { get; set; } = [];
    public List<LaunchpadItem> LaunchpadItems { get; set; } = [];
}
