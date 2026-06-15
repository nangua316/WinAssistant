using WinAssistant.Agents;
using WinAssistant.Models;
using WinAssistant.Services;

namespace WinAssistant.Tests;

public class SkillLibraryServiceTests : IDisposable
{
    private readonly SkillLibraryService _service;

    public SkillLibraryServiceTests()
    {
        _service = new SkillLibraryService();
    }

    public void Dispose()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var file = Path.Combine(appData, "WinAssistant", "skills.json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyGroups()
    {
        _service.Load();
        Assert.Empty(_service.AllSkills);
        Assert.All(_service.GroupedSkills.Values, list => Assert.Empty(list));
    }

    [Fact]
    public void AddAndGetById_WorksCorrectly()
    {
        var skill = new SkillDefinition
        {
            Id = "test001",
            Name = "打开计算器",
            AgentType = AgentType.App,
            ActionType = SkillActionType.run_program,
            ActionParams = new() { ["path"] = "calc.exe" },
            Keywords = ["计算器"],
            CreatedAt = DateTime.Now
        };

        _service.Add(skill);

        var retrieved = _service.GetById("test001");
        Assert.NotNull(retrieved);
        Assert.Equal("打开计算器", retrieved.Name);
        Assert.Equal(AgentType.App, retrieved.AgentType);
        Assert.Single(_service.GetByAgentType(AgentType.App));
    }

    [Fact]
    public void Update_ModifiesSkill()
    {
        var skill = new SkillDefinition
        {
            Id = "test002",
            Name = "旧名称",
            AgentType = AgentType.Computer,
            ActionType = SkillActionType.invoke_tool,
            ActionParams = new() { ["toolId"] = "wifi-password" },
            Keywords = ["wifi"]
        };
        _service.Add(skill);

        skill.Name = "新名称";
        skill.Keywords.Add("新关键词");
        _service.Update(skill);

        var retrieved = _service.GetById("test002");
        Assert.NotNull(retrieved);
        Assert.Equal("新名称", retrieved.Name);
        Assert.Contains("新关键词", retrieved.Keywords);
    }

    [Fact]
    public void Delete_RemovesSkill()
    {
        var skill = new SkillDefinition
        {
            Id = "test003",
            Name = "测试技能",
            AgentType = AgentType.Browser,
            ActionType = SkillActionType.open_url,
            ActionParams = new() { ["url"] = "https://baidu.com" }
        };
        _service.Add(skill);
        Assert.Single(_service.GetByAgentType(AgentType.Browser));

        _service.Delete("test003");
        Assert.Null(_service.GetById("test003"));
        Assert.Empty(_service.GetByAgentType(AgentType.Browser));
    }

    [Fact]
    public void AllSkills_ReturnsFlattenedList()
    {
        _service.Add(new SkillDefinition { Id = "a1", Name = "App1", AgentType = AgentType.App, ActionType = SkillActionType.run_program });
        _service.Add(new SkillDefinition { Id = "c1", Name = "Comp1", AgentType = AgentType.Computer, ActionType = SkillActionType.invoke_tool });
        _service.Add(new SkillDefinition { Id = "b1", Name = "Browser1", AgentType = AgentType.Browser, ActionType = SkillActionType.open_url });
        _service.Add(new SkillDefinition { Id = "f1", Name = "File1", AgentType = AgentType.File, ActionType = SkillActionType.show_message });

        Assert.Equal(4, _service.AllSkills.Count);
    }

    [Fact]
    public void Skills_AreGroupedByAgentType()
    {
        _service.Add(new SkillDefinition { Id = "g1", Name = "G1", AgentType = AgentType.App, ActionType = SkillActionType.run_program });
        _service.Add(new SkillDefinition { Id = "g2", Name = "G2", AgentType = AgentType.App, ActionType = SkillActionType.run_program });

        Assert.Equal(2, _service.GetByAgentType(AgentType.App).Count);
        Assert.Empty(_service.GetByAgentType(AgentType.Computer));
        Assert.Empty(_service.GetByAgentType(AgentType.Browser));
        Assert.Empty(_service.GetByAgentType(AgentType.File));
    }
}
