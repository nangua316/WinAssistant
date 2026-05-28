namespace WinAssistant.Models;

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string? SkillId { get; set; }
    public string? SkillName { get; set; }

    public bool IsUser => Role == "user";
    public bool HasSkill => SkillId != null;
}
