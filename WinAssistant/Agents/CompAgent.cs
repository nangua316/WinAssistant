using WinAssistant.Controls.Tools;
using WinAssistant.Models;

namespace WinAssistant.Agents;

public class CompAgent : IAgent
{
    public string Name => "Comp-Agent";
    public AgentType Type => AgentType.Computer;
    public string[] Triggers => ["设置", "调整", "切换", "开关", "开启", "查", "查看"];

    // 预置系统操作表
    private static readonly List<SystemAction> SystemActions =
    [
        new() { Triggers = ["设置"], Command = "ms-settings:", ActionType = "open_url" },
        new() { Triggers = ["壁纸", "背景", "桌面"], Command = "ms-settings:personalization-background", ActionType = "open_url" },
        new() { Triggers = ["亮度", "屏幕"], Command = "ms-settings:display", ActionType = "open_url" },
        new() { Triggers = ["声音", "音量"], Command = "ms-settings:sound", ActionType = "open_url" },
        new() { Triggers = ["网络", "wifi"], Command = "ms-settings:network-wifi", ActionType = "open_url" },
        new() { Triggers = ["蓝牙"], Command = "ms-settings:bluetooth", ActionType = "open_url" },
        new() { Triggers = ["任务管理器"], Command = "taskmgr", ActionType = "run_program" },
        new() { Triggers = ["控制面板"], Command = "control", ActionType = "run_program" },
        new() { Triggers = ["注册表"], Command = "regedit", ActionType = "run_program" },
        new() { Triggers = ["设备管理器"], Command = "devmgmt.msc", ActionType = "run_program" },
        new() { Triggers = ["截图", "截屏", "截图工具"], Command = "SnippingTool", ActionType = "run_program" },
        new() { Triggers = ["cmd", "命令行", "终端"], Command = "cmd", ActionType = "run_program" },
    ];

    public Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        if (string.IsNullOrEmpty(actionObject))
            return Task.FromResult<AgentMatchResult?>(null);

        var lower = actionObject.ToLowerInvariant();

        // 1. 匹配 ToolRegistry 内置工具
        var tool = ToolRegistry.All.FirstOrDefault(t =>
            t.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase));
        if (tool != null)
        {
            return Task.FromResult<AgentMatchResult?>(new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.Computer,
                ActionType = SkillActionType.invoke_tool,
                ActionParams = new() { ["toolId"] = tool.Id },
                Reply = $"正在执行「{tool.Name}」..."
            });
        }

        // 2. 匹配预置系统操作表
        var sysAction = SystemActions.FirstOrDefault(sa =>
            sa.Triggers.Any(t => lower.Contains(t, StringComparison.OrdinalIgnoreCase)));
        if (sysAction != null)
        {
            return Task.FromResult<AgentMatchResult?>(new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.Computer,
                ActionType = Enum.Parse<SkillActionType>(sysAction.ActionType),
                ActionParams = sysAction.ActionType == "open_url"
                    ? new() { ["url"] = sysAction.Command }
                    : new() { ["path"] = sysAction.Command },
                Reply = $"正在执行系统操作..."
            });
        }

        return Task.FromResult<AgentMatchResult?>(null);
    }

    public Task<SkillDefinition?> LearnAsync(string originalInput, string actionObject, Dictionary<string, string>? llmParams = null)
    {
        // 尝试匹配系统操作来创建技能
        if (!string.IsNullOrEmpty(actionObject))
        {
            var lower = actionObject.ToLowerInvariant();
            var sysAction = SystemActions.FirstOrDefault(sa =>
                sa.Triggers.Any(t => lower.Contains(t, StringComparison.OrdinalIgnoreCase)));
            if (sysAction != null)
            {
                return Task.FromResult<SkillDefinition?>(new SkillDefinition
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = $"系统{sysAction.Triggers[0]}",
                    Description = $"通过 CompAgent 学习：{sysAction.Triggers[0]}",
                    IconGlyph = SkillDefinition.GetDefaultIcon(
                        Enum.Parse<SkillActionType>(sysAction.ActionType)),
                    AgentType = AgentType.Computer,
                    ActionType = Enum.Parse<SkillActionType>(sysAction.ActionType),
                    ActionParams = sysAction.ActionType == "open_url"
                        ? new() { ["url"] = sysAction.Command }
                        : new() { ["path"] = sysAction.Command },
                    Keywords = [originalInput],
                    CreatedAt = DateTime.Now
                });
            }
        }

        return Task.FromResult<SkillDefinition?>(null);
    }
}

public class SystemAction
{
    public string[] Triggers { get; set; } = [];
    public string Command { get; set; } = "";
    public string ActionType { get; set; } = "run_program";
}
