using System.Text.Json;
using WinAssistant.Models;

namespace WinAssistant.Services;

public class SkillLibraryService
{
    private readonly string _filePath;
    private List<SkillDefinition> _skills = [];
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SkillLibraryService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "WinAssistant");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "skills.json");
    }

    public IReadOnlyList<SkillDefinition> AllSkills => _skills.AsReadOnly();

    public void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _skills = JsonSerializer.Deserialize<List<SkillDefinition>>(json) ?? [];
        }
        catch
        {
            _skills = [];
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_skills, JsonOpts);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }

    public SkillDefinition? GetById(string id) =>
        _skills.FirstOrDefault(s => s.Id == id);

    public void Add(SkillDefinition skill)
    {
        _skills.Add(skill);
        Save();
    }

    public void Update(SkillDefinition skill)
    {
        var idx = _skills.FindIndex(s => s.Id == skill.Id);
        if (idx >= 0)
        {
            _skills[idx] = skill;
            Save();
        }
    }

    public void Delete(string id)
    {
        _skills.RemoveAll(s => s.Id == id);
        Save();
    }

    public void IncrementUsage(string id)
    {
        var skill = GetById(id);
        if (skill != null)
        {
            skill.UsageCount++;
            Save();
        }
    }
}
