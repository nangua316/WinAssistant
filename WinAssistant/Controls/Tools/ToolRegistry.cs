namespace WinAssistant.Controls.Tools;

public static class ToolRegistry
{
    private static readonly Dictionary<string, IAssistantTool> _tools = [];

    static ToolRegistry()
    {
        Register(new ThemeSwitcherTool());
    }

    public static IReadOnlyCollection<IAssistantTool> All => _tools.Values;

    public static void Register(IAssistantTool tool) => _tools[tool.Id] = tool;

    public static IAssistantTool? Get(string id) => _tools.GetValueOrDefault(id);

    public static string GetGlyphOrDefault(string? toolId) =>
        toolId != null && _tools.TryGetValue(toolId, out var tool) ? tool.IconGlyph : "";
}
