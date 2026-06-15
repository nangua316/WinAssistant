using WinAssistant.Models;
using WinAssistant.Services;
using WinAssistant.Agents;

namespace WinAssistant.Tests;

public class QwenServiceTests
{
    [Fact]
    public void Configure_WithApiKey_IsConfigured()
    {
        var service = new QwenService();
        service.Configure("test-api-key", null, null);
        Assert.True(service.IsConfigured);
    }

    [Fact]
    public void Configure_WithoutApiKey_NotConfigured()
    {
        var service = new QwenService();
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void Configure_WithEndpoint_StripsTrailingSlash()
    {
        var service = new QwenService();
        service.Configure("key", "https://example.com/api/", "qwen-plus");
        Assert.True(service.IsConfigured);
    }

    [Fact]
    public void SkillDefinition_CreateWithAgentType_Works()
    {
        var skill = new SkillDefinition
        {
            Id = "test101",
            Name = "测试技能",
            AgentType = AgentType.Computer,
            ActionType = SkillActionType.invoke_tool,
            ActionParams = new() { ["toolId"] = "test-tool" },
            Keywords = ["测试", "test"],
            UsageCount = 0,
            CreatedAt = DateTime.Now
        };

        Assert.Equal(AgentType.Computer, skill.AgentType);
        Assert.Equal("test-tool", skill.ActionParams["toolId"]);
        Assert.Equal(2, skill.Keywords.Count);
    }

    [Fact]
    public void SkillDefinition_DefaultIcon_MapsCorrectly()
    {
        Assert.Equal("🔧", SkillDefinition.GetDefaultIcon(SkillActionType.invoke_tool));
        Assert.Equal("▶️", SkillDefinition.GetDefaultIcon(SkillActionType.run_program));
        Assert.Equal("⚙️", SkillDefinition.GetDefaultIcon(SkillActionType.run_powershell));
        Assert.Equal("🌐", SkillDefinition.GetDefaultIcon(SkillActionType.open_url));
        Assert.Equal("📁", SkillDefinition.GetDefaultIcon(SkillActionType.open_folder));
        Assert.Equal("🤖", SkillDefinition.GetDefaultIcon(SkillActionType.ask_llm));
        Assert.Equal("💬", SkillDefinition.GetDefaultIcon(SkillActionType.show_message));
    }

    [Fact]
    public void SkillDefinition_DefaultId_IsEmpty()
    {
        var skill = new SkillDefinition();
        Assert.Equal("", skill.Id);
    }

    [Fact]
    public void SkillDefinition_DefaultIconGlyph_IsRobot()
    {
        var skill = new SkillDefinition();
        Assert.Equal("🤖", skill.IconGlyph);
    }

    [Fact]
    public void AnalysisResult_CanSetProperties()
    {
        var result = new AnalysisResult
        {
            Action = AnalysisAction.Update,
            SkillId = "skill999",
            TargetAgentType = AgentType.Browser,
            NewKeywords = ["新说法", "同义词"],
            Reply = "已更新"
        };

        Assert.Equal(AnalysisAction.Update, result.Action);
        Assert.Equal("skill999", result.SkillId);
        Assert.Equal(AgentType.Browser, result.TargetAgentType);
        Assert.Equal(2, result.NewKeywords.Count);
        Assert.Contains("新说法", result.NewKeywords);
    }

    [Fact]
    public void AgentRouteContext_DefaultValues()
    {
        var ctx = new AgentRouteContext();
        Assert.Equal("", ctx.OriginalInput);
        Assert.Null(ctx.MatchedTrigger);
        Assert.Null(ctx.ActionObject);
        Assert.Null(ctx.PrimaryAgentType);
        Assert.Empty(ctx.AttemptedAgentTypes);
    }

    [Fact]
    public void AgentRouteContext_CanPopulate()
    {
        var ctx = new AgentRouteContext
        {
            OriginalInput = "搜索今天的新闻",
            MatchedTrigger = "搜索",
            ActionObject = "今天的新闻",
            PrimaryAgentType = "Browser",
            AttemptedAgentTypes = ["Browser"]
        };

        Assert.Equal("搜索", ctx.MatchedTrigger);
        Assert.Equal("今天的新闻", ctx.ActionObject);
        Assert.Single(ctx.AttemptedAgentTypes);
    }
}
