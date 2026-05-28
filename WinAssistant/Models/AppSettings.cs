namespace WinAssistant.Models;

public class AppSettings
{
    public bool IsAutoStart { get; set; }
    public bool IsLaunchpadEnabled { get; set; } = true;
    public List<string> MouseTriggers { get; set; } = ["XButton2"];
    public List<string> KeyboardTriggers { get; set; } = ["SingleCtrl", "AltSpace"];
    public string LaunchpadHotKey { get; set; } = "Ctrl+Q";
    public List<HotKeyBinding> Bindings { get; set; } = [];
    public List<LaunchpadItem> LaunchpadItems { get; set; } = [];
    public Dictionary<string, string> ToolSettings { get; set; } = [];
    public string LastSearchText { get; set; } = "";

    // AI / 技能库配置
    public string? AiApiKey { get; set; }
    public string AiEndpoint { get; set; } = "https://dashscope.aliyuncs.com";
    public string AiChatModel { get; set; } = "qwen-plus";
    public string AiEmbeddingModel { get; set; } = "text-embedding-v3";
}
