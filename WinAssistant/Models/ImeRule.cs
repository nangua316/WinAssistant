namespace WinAssistant.Models;

/// <summary>
/// 输入法规则：为特定应用程序自动切换键盘布局和输入法状态。
/// </summary>
public class ImeRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ProcessName { get; set; } = "";          // e.g. "WeChat.exe"
    public string DisplayName { get; set; } = "";           // e.g. "微信"
    public string Klid { get; set; } = "";                  // Keyboard layout ID, e.g. "00000804" or "00000409"
    public string ImeDisplayName { get; set; } = "";        // Display name, e.g. "中文(简体) 微软拼音"
    public bool UseEnglishMode { get; set; }                // Within-IME Chinese/English mode
    public bool UseFullWidth { get; set; }                  // Full-width punctuation
    public bool CapsLockState { get; set; }                 // CapsLock on/off
    public bool IsEnabled { get; set; } = true;

    /// <summary>Display-only subtitle for rule list UI.</summary>
    public string SubtitleText =>
        $"{ImeDisplayName} · {(UseEnglishMode ? "英文" : "中文")} · {(UseFullWidth ? "全角" : "半角")} · CapsLock {(CapsLockState ? "开" : "关")}";
}
