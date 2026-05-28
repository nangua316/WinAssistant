namespace WinAssistant.Models;

public enum SkillActionType
{
    invoke_tool,
    run_program,
    run_powershell,
    open_url,
    open_folder,
    ask_llm,
    show_message
}

public class SkillDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "🤖";
    public SkillActionType ActionType { get; set; }
    public Dictionary<string, string> ActionParams { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
    public int UsageCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public static string GetDefaultIcon(SkillActionType type) => type switch
    {
        SkillActionType.invoke_tool => "🔧",
        SkillActionType.run_program => "▶️",
        SkillActionType.run_powershell => "⚙️",
        SkillActionType.open_url => "🌐",
        SkillActionType.open_folder => "📁",
        SkillActionType.ask_llm => "🤖",
        SkillActionType.show_message => "💬",
        _ => "🤖"
    };
}
