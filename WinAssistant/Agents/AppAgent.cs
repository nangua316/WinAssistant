using WinAssistant.Helpers;
using WinAssistant.Models;
using WinAssistant.Services;

namespace WinAssistant.Agents;

public class AppAgent : IAgent
{
    public string Name => "App-Agent";
    public AgentType Type => AgentType.App;
    public string[] Triggers => ["打开", "启动", "运行", "关闭", "退出", "启动台", "开"];

    public Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        if (string.IsNullOrEmpty(actionObject))
            return Task.FromResult<AgentMatchResult?>(null);

        var apps = AppScanner.ScanInstalledApps();

        // 1. Exact + contains match (case-insensitive), with path validation
        var matched = apps.FirstOrDefault(a =>
            File.Exists(a.AppPath) && (
                a.Name.Equals(actionObject, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase)));

        // 2. Pinyin fuzzy match, with path validation
        if (matched == null)
        {
            matched = apps.Where(a => File.Exists(a.AppPath)).FirstOrDefault(a =>
            {
                try
                {
                    var pinyin = PinyinHelper.GetPinyin(a.Name);
                    var initials = PinyinHelper.GetInitials(a.Name);
                    return pinyin.Contains(actionObject, StringComparison.OrdinalIgnoreCase) ||
                           initials.Contains(actionObject, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        }

        if (matched != null)
        {
            return Task.FromResult<AgentMatchResult?>(new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.App,
                ActionType = SkillActionType.run_program,
                ActionParams = new()
                {
                    ["path"] = matched.AppPath,
                    ["arguments"] = matched.Arguments
                },
                Reply = $"正在启动「{matched.Name}」..."
            });
        }

        // For "关闭"/"退出" triggers, we don't have a simple app close mechanism yet
        if (actionObject.Length > 0)
            return Task.FromResult<AgentMatchResult?>(null);

        return Task.FromResult<AgentMatchResult?>(null);
    }

    public Task<SkillDefinition?> LearnAsync(string originalInput, string actionObject, Dictionary<string, string>? llmParams = null)
    {
        // Try to find the app in AppScanner to create a run_program skill
        var apps = AppScanner.ScanInstalledApps();
        var matched = apps.FirstOrDefault(a =>
            File.Exists(a.AppPath) && (
                a.Name.Equals(actionObject, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase)));

        if (matched == null)
        {
            // Try pinyin
            matched = apps.Where(a => File.Exists(a.AppPath)).FirstOrDefault(a =>
            {
                try
                {
                    var pinyin = PinyinHelper.GetPinyin(a.Name);
                    var initials = PinyinHelper.GetInitials(a.Name);
                    return pinyin.Contains(actionObject, StringComparison.OrdinalIgnoreCase) ||
                           initials.Contains(actionObject, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
        }

        if (matched != null)
        {
            var keywords = new List<string>();
            // Use original input's meaningful parts as keywords
            var trimmed = originalInput.Trim();
            foreach (var trigger in Triggers)
            {
                var idx = trimmed.IndexOf(trigger, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var afterTrigger = trimmed[(idx + trigger.Length)..].Trim();
                    if (!string.IsNullOrEmpty(afterTrigger) && afterTrigger.Length <= 20)
                        keywords.Add(trimmed);
                    break;
                }
            }
            if (keywords.Count == 0 && !string.IsNullOrEmpty(actionObject))
                keywords.Add(originalInput);

            return Task.FromResult<SkillDefinition?>(new SkillDefinition
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = $"打开{matched.Name}",
                Description = $"通过 AppAgent 学习：打开 {matched.Name}",
                IconGlyph = SkillDefinition.GetDefaultIcon(SkillActionType.run_program),
                AgentType = AgentType.App,
                ActionType = SkillActionType.run_program,
                ActionParams = new()
                {
                    ["path"] = matched.AppPath,
                    ["arguments"] = matched.Arguments
                },
                Keywords = keywords,
                CreatedAt = DateTime.Now
            });
        }

        return Task.FromResult<SkillDefinition?>(null);
    }
}
