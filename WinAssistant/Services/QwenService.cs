using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinAssistant.Controls.Tools;
using WinAssistant.Models;

namespace WinAssistant.Services;

public class QwenService : IDisposable
{
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();
    private string? _apiKey;
    private string _endpoint = "https://dashscope.aliyuncs.com";
    private string _model = "qwen-plus";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions RequestJsonOpts = new();
    private const string SystemPromptTemplate = @"你是一个智能助手，根据用户输入匹配技能、创建新技能或直接对话。

## 当前技能库
{skillList}

## 可用动作类型
invoke_tool（调用内置工具）, run_program（运行程序）, run_powershell（执行脚本）, open_url（打开网址）, open_folder（打开文件夹）, ask_llm（AI问答）, show_message（弹出消息）

## 返回格式
必须返回一个 JSON 对象（不要 markdown，不要代码块，纯 JSON 文本）：

1. 匹配已有技能：
{""type"":""match"",""skillId"":""技能ID"",""reply"":""用户可见的回复""}

2. 创建新技能：
{""type"":""create"",""skill"":{""name"":""技能名称"",""description"":""描述"",""actionType"":""invoke_tool"",""actionParams"":{""toolId"":""wifi-password""},""keywords"":[]},""reply"":""回复""}

3. 纯聊天：
{""type"":""chat"",""reply"":""回答""}

## actionParams 示例
- invoke_tool: {""toolId"":""工具ID""}
- run_program: {""path"":""路径"",""arguments"":""""}
- run_powershell: {""script"":""脚本""}
- open_url: {""url"":""网址""}
- open_folder: {""path"":""路径""}
- ask_llm: {""question"":""问题""}
- show_message: {""title"":""标题"",""content"":""内容""}

## 规则
1. type 只能是 match、create 或 chat
2. 有明确需求时优先 create 或 match，闲聊时才 chat
3. actionParams 必须根据动作类型填对应参数，不能为空
4. 不要在 JSON 外添加 markdown 或代码块标记";

    public void Configure(string? apiKey, string? endpoint, string? model)
    {
        _apiKey = apiKey;
        if (!string.IsNullOrEmpty(endpoint))
        {
            // Extract only scheme+host to avoid path duplication in saved settings
            try { _endpoint = new Uri(endpoint.TrimEnd('/')).GetLeftPart(UriPartial.Authority).TrimEnd('/'); }
            catch { _endpoint = endpoint.TrimEnd('/'); }
        }
        if (!string.IsNullOrEmpty(model)) _model = model;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public async Task<AnalysisResult> AnalyzeAsync(string userInput, List<SkillDefinition> skills)
    {
        // Pre-check: match built-in tools or saved skills by keyword (faster and more reliable than LLM)
        var preMatch = TryMatchByKeyword(userInput, skills);
        if (preMatch != null) return preMatch;

        var skillListText = BuildSkillListText(skills);
        var systemPrompt = SystemPromptTemplate.Replace("{skillList}", skillListText);

        var response = await CallLLMAsync([
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userInput }
        ]);
        return ParseAnalysisResult(response, userInput);
    }

    public async Task<string> ChatAsync(string message, List<ChatMessage>? history = null)
    {
        var msgs = new List<object>
        {
            new { role = "system", content = "你是一个有用的AI助手。保持回答简洁准确，使用中文。" }
        };

        if (history != null)
        {
            foreach (var h in history.TakeLast(10))
                msgs.Add(new { role = h.Role, content = h.Content });
        }
        msgs.Add(new { role = "user", content = message });

        var response = await CallLLMAsync(msgs);
        return ExtractTextContent(response);
    }

    private async Task<string> CallLLMAsync(IEnumerable<object> messages)
    {
        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("API Key 未配置，请在设置中填入");

        var requestBody = new
        {
            model = _model,
            messages = messages,
            temperature = 0.3,
            max_tokens = 2048
        };

        var json = JsonSerializer.Serialize(requestBody, RequestJsonOpts);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_endpoint}/compatible-mode/v1/chat/completions")
        {
            Content = httpContent
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"API 返回 {(int)response.StatusCode}: {errorBody}");
        }
        return await response.Content.ReadAsStringAsync();
    }

    private static string ExtractTextContent(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);

            // Try OpenAI-compatible format first
            if (doc.RootElement.TryGetProperty("choices", out var choices))
            {
                if (choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                    return content.GetString() ?? "";
            }

            // Fallback: DashScope format
            if (doc.RootElement.TryGetProperty("output", out var output) &&
                output.TryGetProperty("choices", out choices))
            {
                if (choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                    return content.GetString() ?? "";
            }

            if (doc.RootElement.TryGetProperty("output", out var out2) &&
                out2.TryGetProperty("text", out var text))
                return text.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static AnalysisResult ParseAnalysisResult(string jsonResponse, string originalInput)
    {
        var content = ExtractTextContent(jsonResponse);
        if (string.IsNullOrEmpty(content))
        {
            return new AnalysisResult
            {
                Action = AnalysisAction.Chat,
                Reply = "抱歉，我没有理解你的意思，能再说一遍吗？"
            };
        }

        var json = ExtractJson(content);
        if (json == null)
        {
            return new AnalysisResult
            {
                Action = AnalysisAction.Chat,
                Reply = content
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            // If LLM mistakenly used an actionType as the top-level type (e.g. "invoke_tool"),
            // treat it as a create action.
            var fallbackActionType = "";
            if (type is not "match" and not "create" and not "chat")
            {
                fallbackActionType = type;
                type = "create";
            }

            switch (type)
            {
                case "match":
                    return new AnalysisResult
                    {
                        Action = AnalysisAction.Match,
                        SkillId = root.GetProperty("skillId").GetString(),
                        Reply = root.TryGetProperty("reply", out var r) ? r.GetString() ?? "" : ""
                    };

                case "create":
                {
                    var skillEl = root.TryGetProperty("skill", out var s) ? s : root;
                    var actionTypeStr = fallbackActionType;
                    if (string.IsNullOrEmpty(actionTypeStr) && skillEl.TryGetProperty("actionType", out var atEl))
                        actionTypeStr = atEl.GetString() ?? "";

                    var skill = new SkillDefinition
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Name = skillEl.TryGetProperty("name", out var n) ? n.GetString() ?? "新技能" : "新技能",
                        Description = skillEl.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        IconGlyph = SkillDefinition.GetDefaultIcon(
                            Enum.TryParse<SkillActionType>(actionTypeStr, out var at) ? at : SkillActionType.ask_llm),
                        ActionType = Enum.TryParse<SkillActionType>(actionTypeStr, out at) ? at : SkillActionType.ask_llm,
                        ActionParams = [],
                        Keywords = [],
                        CreatedAt = DateTime.Now
                    };

                    if (skillEl.TryGetProperty("actionParams", out var ap))
                    {
                        foreach (var p in ap.EnumerateObject())
                            skill.ActionParams[p.Name] = p.Value.GetString() ?? "";
                    }
                    // Fallback: if LLM put params at root level
                    if (skill.ActionParams.Count == 0)
                    {
                        foreach (var key in new[] { "toolId", "path", "url", "script", "question", "title", "content", "arguments" })
                        {
                            if (root.TryGetProperty(key, out var rv))
                                skill.ActionParams[key] = rv.GetString() ?? "";
                        }
                    }
                    if (skillEl.TryGetProperty("keywords", out var kw))
                    {
                        foreach (var k in kw.EnumerateArray())
                            skill.Keywords.Add(k.GetString() ?? "");
                    }

                    return new AnalysisResult
                    {
                        Action = AnalysisAction.Create,
                        NewSkill = skill,
                        Reply = root.TryGetProperty("reply", out var r2) ? r2.GetString() ?? "" : ""
                    };
                }

                default:
                    return new AnalysisResult
                    {
                        Action = AnalysisAction.Chat,
                        Reply = root.TryGetProperty("reply", out var r3) ? r3.GetString() ?? content : content
                    };
            }
        }
        catch
        {
            return new AnalysisResult
            {
                Action = AnalysisAction.Chat,
                Reply = content
            };
        }
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        // Skip markdown code block markers
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
            if (depth == 0)
                return text[start..(i + 1)];
        }
        return null;
    }

    private static string BuildSkillListText(List<SkillDefinition> skills)
    {
        if (skills.Count == 0) return "（暂无技能，直接根据用户输入创建新技能）";
        return string.Join("\n", skills.Select((s, i) =>
            $"{i + 1}. ID: {s.Id}, 名称: {s.Name}, 描述: {s.Description}, 类型: {s.ActionType}, 关键词: [{string.Join(", ", s.Keywords)}]"));
    }

    /// <summary>
    /// Pre-check: match input against built-in tools or saved skills by keyword,
    /// avoiding an LLM call for common requests like "查wifi密码".
    /// Built-in tools are checked first (they are the most commonly requested).
    /// </summary>
    private static AnalysisResult? TryMatchByKeyword(string input, List<SkillDefinition> savedSkills)
    {
        var lower = input.ToLowerInvariant();

        // Match built-in tools by name or id first (higher priority)
        foreach (var tool in ToolRegistry.All)
        {
            var toolNameLow = tool.Name.ToLowerInvariant();
            var toolIdLow = tool.Id.ToLowerInvariant().Replace("-", "");
            if ((toolNameLow.Length > 0 && lower.Contains(toolNameLow)) ||
                (toolIdLow.Length > 0 && lower.Contains(toolIdLow)))
            {
                var skill = new SkillDefinition
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = tool.Name,
                    Description = tool.Description,
                    ActionType = SkillActionType.invoke_tool,
                    ActionParams = new() { ["toolId"] = tool.Id },
                    IconGlyph = tool.IconGlyph,
                    CreatedAt = DateTime.Now
                };
                return new AnalysisResult
                {
                    Action = AnalysisAction.Create,
                    NewSkill = skill,
                    Reply = $"正在执行「{tool.Name}」..."
                };
            }
        }

        // Match saved skills by name or keywords (lower priority)
        foreach (var skill in savedSkills)
        {
            if (skill.Name.Length > 0 && lower.Contains(skill.Name.ToLowerInvariant()))
                return new AnalysisResult { Action = AnalysisAction.Match, SkillId = skill.Id, Reply = $"正在执行「{skill.Name}」..." };
            foreach (var kw in skill.Keywords)
            {
                if (kw.Length > 0 && lower.Contains(kw.ToLowerInvariant()))
                    return new AnalysisResult { Action = AnalysisAction.Match, SkillId = skill.Id, Reply = $"正在执行「{skill.Name}」..." };
            }
        }

        return null;
    }
}

public enum AnalysisAction { Match, Create, Chat }

public class AnalysisResult
{
    public AnalysisAction Action { get; set; }
    public string? SkillId { get; set; }
    public SkillDefinition? NewSkill { get; set; }
    public string Reply { get; set; } = "";
}
