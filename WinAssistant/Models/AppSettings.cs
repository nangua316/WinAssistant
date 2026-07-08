namespace WinAssistant.Models;

public enum ToastPosition
{
    BottomLeft,
    BottomRight,
    TopCenter,
    BottomCenter
}

public class AppSettings
{
    public bool IsAutoStart { get; set; }
    public bool IsLaunchpadEnabled { get; set; } = true;
    public List<string> MouseTriggers { get; set; } = ["XButton2"];
    public List<string> KeyboardTriggers { get; set; } = ["AltSpace"];
    public string LaunchpadHotKey { get; set; } = "Ctrl+Q";
    public List<HotKeyBinding> Bindings { get; set; } = [];
    public List<LaunchpadItem> LaunchpadItems { get; set; } = [];
    public Dictionary<string, string> ToolSettings { get; set; } = [];
    public string LastSearchText { get; set; } = "";

    // 启动台窗口大小（像素，0 表示使用默认自动计算）
    public int LaunchpadWindowWidth { get; set; }
    public int LaunchpadWindowHeight { get; set; }

    // AI / 技能库配置
    public string? AiApiKey { get; set; }
    public string AiEndpoint { get; set; } = "https://dashscope.aliyuncs.com";
    public string AiChatModel { get; set; } = "qwen-plus";
    public string AiEmbeddingModel { get; set; } = "text-embedding-v3";

    // 输入法相关 Toast 提示
    public bool IsCapsLockToastEnabled { get; set; } = true;
    public bool IsCnEnToastEnabled { get; set; } = true;
    public bool IsImeSwitchToastEnabled { get; set; } = true;

    // Toast 显示位置（默认左下）
    public ToastPosition ToastPosition { get; set; } = ToastPosition.BottomLeft;

    // 主题模式：0=跟随系统, 1=浅色, 2=深色
    public int ThemeMode { get; set; } = 0;
}
