using WinAssistant.Models;
using System.Linq;

namespace WinAssistant.Agents;

public class FileAgent : IAgent
{
    public string Name => "File-Agent";
    public AgentType Type => AgentType.File;
    public string[] Triggers => ["清理", "查找", "找到", "打开文件夹", "删除"];

    public Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        // 清理匹配（磁盘清理/系统垃圾）
        var cleanupKeywords = new[] { "垃圾", "缓存", "临时文件", "系统清理", "磁盘清理", "盘清理", "temp" };
        if (!string.IsNullOrEmpty(actionObject) &&
            cleanupKeywords.Any(kw => actionObject.Contains(kw, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult<AgentMatchResult?>(new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.File,
                ActionType = SkillActionType.run_powershell,
                ActionParams = new()
                {
                    ["script"] = "Start-Process cleanmgr -ArgumentList '/sagerun:1' -NoNewWindow -Wait; Write-Output '清理完成'"
                },
                Reply = "正在清理系统垃圾..."
            });
        }

        // 删除/查找等操作提示（暂未实现文件浏览）
        if (!string.IsNullOrEmpty(actionObject) &&
            (actionObject.Contains("删除") || actionObject.Contains("找到") || actionObject.Contains("查找")))
        {
            return Task.FromResult<AgentMatchResult?>(new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.File,
                ActionType = SkillActionType.show_message,
                ActionParams = new()
                {
                    ["message"] = "文件浏览与操作功能正在开发中，暂不支持通过指令查找或删除具体文件。请手动操作。"
                },
                Reply = "文件操作功能暂未实现..."
            });
        }

        return Task.FromResult<AgentMatchResult?>(null);
    }

    public Task<SkillDefinition?> LearnAsync(string originalInput, string actionObject, Dictionary<string, string>? llmParams = null)
    {
        // 阶段四再完善文件操作相关的学习
        return Task.FromResult<SkillDefinition?>(null);
    }
}
