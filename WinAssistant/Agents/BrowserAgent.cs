using WinAssistant.Models;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinAssistant.Agents;

public class BrowserAgent : IAgent
{
    public string Name => "Browser-Agent";
    public AgentType Type => AgentType.Browser;
    public string[] Triggers => ["搜索", "百度", "谷歌", "上网", "打开网页", "浏览器"];

    public Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        if (!string.IsNullOrEmpty(actionObject))
        {
            return Task.FromResult<AgentMatchResult?>(new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.Browser,
                ActionType = SkillActionType.open_url,
                ActionParams = new()
                {
                    ["url"] = $"https://www.baidu.com/s?wd={Uri.EscapeDataString(actionObject)}"
                },
                Reply = $"正在搜索「{actionObject}」..."
            });
        }

        // 无搜索词时打开默认浏览器
        var browserPath = FindDefaultBrowser();
        if (browserPath != null)
        {
            return Task.FromResult<AgentMatchResult?>(new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.Browser,
                ActionType = SkillActionType.run_program,
                ActionParams = new() { ["path"] = browserPath },
                Reply = "正在打开浏览器..."
            });
        }

        return Task.FromResult<AgentMatchResult?>(null);
    }

    private static string? FindDefaultBrowser()
    {
        // Try Edge first (always available on Windows)
        string[] paths =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        ];

        foreach (var path in paths)
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    public Task<SkillDefinition?> LearnAsync(string originalInput, string actionObject, Dictionary<string, string>? llmParams = null)
    {
        // Default: create an open_url skill using actionObject as search term, or use llmParams url
        string? url = null;
        if (llmParams != null && llmParams.TryGetValue("url", out var llmUrl) && !string.IsNullOrEmpty(llmUrl))
            url = llmUrl;
        else if (!string.IsNullOrEmpty(actionObject))
            url = $"https://www.baidu.com/s?wd={Uri.EscapeDataString(actionObject)}";
        else
            url = "https://www.baidu.com";

        return Task.FromResult<SkillDefinition?>(new SkillDefinition
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = $"搜索：{actionObject ?? "默认"}",
            Description = $"通过 BrowserAgent 学习：搜索 {actionObject ?? "默认"}",
            IconGlyph = SkillDefinition.GetDefaultIcon(SkillActionType.open_url),
            AgentType = AgentType.Browser,
            ActionType = SkillActionType.open_url,
            ActionParams = new() { ["url"] = url },
            Keywords = string.IsNullOrEmpty(actionObject) ? [originalInput] : [originalInput],
            CreatedAt = DateTime.Now
        });
    }
}
