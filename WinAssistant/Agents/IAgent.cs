using WinAssistant.Models;

namespace WinAssistant.Agents;

public enum AgentType
{
    App,
    Computer,
    Browser,
    File
}

public class AgentMatchResult
{
    public bool Success { get; set; }
    public AgentType AgentType { get; set; }
    public SkillActionType ActionType { get; set; }
    public Dictionary<string, string> ActionParams { get; set; } = [];
    public string? Reply { get; set; }
    // 新增：LLM 兜底时用于学习
    public string? OriginalInput { get; set; }
    public string? ActionObject { get; set; }
}

public interface IAgent
{
    string Name { get; }
    AgentType Type { get; }
    string[] Triggers { get; }

    Task<AgentMatchResult?> TryMatchAsync(string actionObject);

    /// <summary>
    /// 学习新映射：将原始输入/actionObject 转化为该 Agent 的技能定义。
    /// 当 LLM 解析出某个意图对应此 Agent 时调用。
    /// </summary>
    Task<SkillDefinition?> LearnAsync(string originalInput, string actionObject, Dictionary<string, string>? llmParams = null);
}
