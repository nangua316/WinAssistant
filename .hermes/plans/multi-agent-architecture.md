# WinAssistant 多 Agent 架构技术方案

## 概述

将现有单一 AI 匹配层拆分为**路由层 + 多个专注 Agent**，每个 Agent 负责自己的领域，本地匹配优先，LLM 兜底。

```
用户输入 → Router →  App-Agent      (打开/启动/关闭应用)
                    │  Comp-Agent     (系统设置/工具/开关)
                    │  Browser-Agent  (搜索/上网/打开网址)
                    │  File-Agent     (文件操作)
                    └→ 全未命中 → LLM（兜底 + 学习）
```

---

## 一、路由层（Router）

### 职责
根据用户输入的**触发词**，决定交给哪个 Agent 处理。

### 触发词表

```
App-Agent:      打开, 启动, 运行, 关闭, 退出, 启动台
Comp-Agent:     设置, 调整, 切换, 开关, 开启, 查, 查看
Browser-Agent:  搜索, 百度, 谷歌, 上网, 打开网页, 浏览器
File-Agent:     清理, 查找, 找到, 打开文件夹, 删除
```

### 路由逻辑

```csharp
public class Router
{
    private readonly List<IAgent> _agents;
    
    // 1. 用触发词 + 对象分类
    public async Task<RouterResult> RouteAsync(string input)
    {
        // 提取触发词（第一个匹配的动词）
        var trigger = ExtractTrigger(input);
        if (trigger == null) 
            return RouterResult.ToLLM(input); // 无触发词 → 聊天
        
        // 提取动作对象（触发词后面的内容）
        var actionObject = ExtractActionObject(input, trigger);
        
        // 按触发词命中 Agent
        var agent = GetAgentByTrigger(trigger);
        if (agent == null)
            return RouterResult.ToLLM(input);
        
        // Agent 尝试匹配
        var match = await agent.TryMatchAsync(actionObject);
        if (match != null)
            return RouterResult.Matched(match);
        
        // Agent 匹配不到 → 传给 LLM
        return RouterResult.ToLLMWithContext(input, agent, actionObject);
    }
}
```

### 路由结果

```csharp
public class RouterResult
{
    public AgentType? TargetAgent { get; set; }
    public string? ActionObject { get; set; }
    public bool RouteToLLM { get; set; }  // 是否需要走 LLM
    public bool HasLocalMatch { get; set; }
}
```

---

## 二、Agent 接口定义

```csharp
public interface IAgent
{
    string Name { get; }                    // "App-Agent"
    AgentType Type { get; }
    string[] Triggers { get; }              // 路由触发词
    
    /// <summary>
    /// Agent 尝试本地匹配用户意图
    /// </summary>
    Task<AgentMatchResult?> TryMatchAsync(string actionObject);
    
    /// <summary>
    /// Agent 从 LLM 返回结果中学习（追加关键词/创建新映射）
    /// </summary>
    Task LearnAsync(string userInput, string actionObject, SkillDefinition skill);
}
```

---

## 三、App-Agent（应用管理）

### 数据源（优先级从高到低）

| 优先级 | 数据源 | 来源 | 匹配方式 |
|--------|--------|------|---------|
| P0 | 常用应用缓存 | AppScanner 排名前20 | 精确匹配 |
| P1 | AppScanner 全量应用表 | `AppScanner.ScanInstalledApps()` | 包含匹配 + 拼音 |
| P2 | App-Agent 技能库 | LLM 历史学习的模糊映射 | 关键词匹配 |

### 匹配流程

```csharp
public class AppAgent : IAgent
{
    private List<InstalledAppInfo> _allApps;
    private List<AgentSkill> _learnedSkills;  // Agent 私有技能
    
    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        // P0: 常用缓存
        var hot = _hotApps.FirstOrDefault(a => 
            a.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase));
        if (hot != null) return BuildResult(hot);
        
        // P1: 全量应用表（包含 + 拼音）
        var matched = _allApps.FirstOrDefault(a =>
            a.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase)
            || MatchPinyin(a.Name, actionObject));
        if (matched != null) return BuildResult(matched);
        
        // P2: Agent 技能库（LLM 历史学习的模糊映射）
        var skill = _learnedSkills.FirstOrDefault(s =>
            s.Keywords.Any(kw => actionObject.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        if (skill != null) {
            var app = _allApps.FindByName(skill.TargetAppName);
            if (app != null) return BuildResult(app);
        }
        
        return null; // 本地匹配不到，需要 LLM
    }
    
    private AgentMatchResult BuildResult(InstalledAppInfo app)
    {
        return new AgentMatchResult
        {
            Success = true,
            AgentType = AgentType.App,
            TargetApp = app,
            ActionType = SkillActionType.run_program,
            ActionParams = new() { ["path"] = app.AppPath, ["arguments"] = app.Arguments }
        };
    }
}
```

---

## 四、Comp-Agent（系统设置/工具）

### 数据源

| 优先级 | 数据源 | 内容 |
|--------|--------|------|
| P0 | ToolRegistry | 内置工具（热点、WiFi密码、主题切换等） |
| P1 | 系统操作表 | 预置的系统功能（见下表） |
| P2 | Comp-Agent 技能库 | LLM 学习的模糊映射 |

### 系统操作表

```csharp
// 硬编码的常见系统操作，不需要扫码
private static readonly List<SystemAction> SystemActions = new()
{
    // 系统设置
    new() { Triggers = ["设置", "系统设置"], Command = "ms-settings:", ActionType = "open_url" },
    new() { Triggers = ["壁纸", "背景", "桌面"], Command = "ms-settings:personalization-background", ActionType = "open_url" },
    new() { Triggers = ["亮度", "屏幕"], Command = "ms-settings:display", ActionType = "open_url" },
    new() { Triggers = ["声音", "音量"], Command = "ms-settings:sound", ActionType = "open_url" },
    new() { Triggers = ["网络", "WiFi"], Command = "ms-settings:network-wifi", ActionType = "open_url" },
    new() { Triggers = ["蓝牙"], Command = "ms-settings:bluetooth", ActionType = "open_url" },
    
    // 系统工具
    new() { Triggers = ["任务管理器"], Command = "taskmgr", ActionType = "run_program" },
    new() { Triggers = ["控制面板"], Command = "control", ActionType = "run_program" },
    new() { Triggers = ["注册表"], Command = "regedit", ActionType = "run_program" },
    new() { Triggers = ["设备管理器"], Command = "devmgmt.msc", ActionType = "run_program" },
    new() { Triggers = ["磁盘管理"], Command = "diskmgmt.msc", ActionType = "run_program" },
    new() { Triggers = ["计算器"], Command = "calc", ActionType = "run_program" },
    
    // 特殊
    new() { Triggers = ["截图", "截屏", "截图工具"], Command = "SnippingTool", ActionType = "run_program" },
    new() { Triggers = ["cmd", "命令行", "终端"], Command = "cmd", ActionType = "run_program" },
};
```

### 匹配逻辑

```csharp
public class CompAgent : IAgent
{
    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        // P0: ToolRegistry 匹配
        var tool = ToolRegistry.All.FirstOrDefault(t =>
            t.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase));
        if (tool != null) return BuildToolResult(tool);
        
        // P1: 系统操作表匹配
        var sysAction = SystemActions.FirstOrDefault(a =>
            a.Triggers.Any(t => actionObject.Contains(t, StringComparison.OrdinalIgnoreCase)));
        if (sysAction != null) return BuildSystemActionResult(sysAction);
        
        // P2: 技能库
        var skill = _learnedSkills.FirstOrDefault(s =>
            s.Keywords.Any(kw => actionObject.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        if (skill != null) return BuildFromSkill(skill);
        
        return null;
    }
}
```

---

## 五、Browser-Agent（浏览器操作）

### 匹配内容

```csharp
public class BrowserAgent : IAgent
{
    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        // 直接搜索
        if (!string.IsNullOrEmpty(actionObject))
        {
            return new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.Browser,
                ActionType = SkillActionType.open_url,
                ActionParams = new()
                {
                    ["url"] = $"https://www.baidu.com/s?wd={Uri.EscapeDataString(actionObject)}"
                }
            };
        }
        
        // 打开浏览器（无搜索词时）
        return new AgentMatchResult
        {
            Success = true,
            AgentType = AgentType.Browser,
            ActionType = SkillActionType.run_program,
            ActionParams = new() { ["path"] = FindDefaultBrowser() }
        };
    }
    
    private string FindDefaultBrowser()
    {
        // 从注册表读默认浏览器
        // 或用 AppScanner 找 Chrome/Edge/Firefox
    }
}
```

---

## 六、File-Agent（文件操作）

保留扩展接口，当前可简单实现：

```csharp
public class FileAgent : IAgent
{
    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        // 预置功能
        if (actionObject.Contains("垃圾", StringComparison.OrdinalIgnoreCase)
            || actionObject.Contains("缓存", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.File,
                ActionType = SkillActionType.run_powershell,
                ActionParams = new()
                {
                    ["script"] = " CleanMgr /sagerun:1 | Out-Null"
                }
            };
        }
        
        return null;
    }
}
```

---

## 七、LLM 兜底与学习机制

### 当所有 Agent 都匹配不到时

```csharp
public async Task<AnalysisResult> AnalyzeWithAgentContextAsync(
    string userInput, 
    IAgent? preferredAgent,   // 路由推荐的 Agent
    string? actionObject)     // 路由提取的动作对象
{
    // 构建带 Agent 上下文的 Prompt
    var prompt = BuildMultiAgentPrompt(userInput, preferredAgent, actionObject);
    
    // 调用 LLM
    var result = await _qwen.AnalyzeAsync(prompt);
    
    // LLM 决定：create / update / chat
    // 如果是 create → 通知对应 Agent 学习
    if (result.Action == AnalysisAction.Create && preferredAgent != null)
    {
        await preferredAgent.LearnAsync(userInput, actionObject, result.NewSkill);
    }
    
    return result;
}
```

### LLM 返回格式（增加了 Agent 上下文）

```csharp
// system prompt 增加：
// 当前路由上下文：Comp-Agent, 动作对象="热点"
// 建议先在该 Agent 的范围内匹配

// LLM 返回：
{
  "type": "create",
  "agentType": "computer",           // ← 新增：指定给哪个 Agent
  "skill": {
    "name": "开启热点",
    "actionType": "invoke_tool",
    "actionParams": { "toolId": "mobile-hotspot" },
    "keywords": ["热点", "开热点", "加热点", "hotspot"]
  },
  "reply": "正在开启热点..."
}
```

---

## 八、存储结构（skills.json 重构）

```json
{
  "version": 2,
  "learnedSkills": {
    "app": [
      {
        "id": "a1b2",
        "keywords": ["算个东西", "算个数", "算数字"],
        "targetAppName": "计算器",
        "targetAppPath": "C:\\Windows\\System32\\calc.exe",
        "createdAt": "2026-05-29T15:00:00",
        "usageCount": 3
      },
      {
        "id": "c3d4",
        "keywords": ["画个图", "画图"],
        "targetAppName": "画图",
        "targetAppPath": "C:\\Windows\\System32\\mspaint.exe",
        "createdAt": "2026-05-29T15:30:00",
        "usageCount": 1
      }
    ],
    "computer": [
      {
        "id": "e5f6",
        "keywords": ["加热", "热点"],
        "toolId": "mobile-hotspot",
        "createdAt": "2026-05-29T14:00:00",
        "usageCount": 5
      }
    ],
    "browser": [],
    "file": []
  }
}
```

---

## 九、与现有代码的兼容方案

### 不改动现有的 SkillDefinition 和 QwenService

```
旧结构:
  ChatWindow → QwenService.AnalyzeAsync → SkillExecutionService.ExecuteAsync
  
新结构（增加 Router，不删现有代码）:
  ChatWindow → Router.RouteAsync
                   ├── AppAgent.TryMatchAsync → SkillExecutionService.ExecuteAsync
                   ├── CompAgent.TryMatchAsync → SkillExecutionService.ExecuteAsync
                   ├── BrowserAgent.TryMatchAsync → SkillExecutionService.ExecuteAsync
                   ├── FileAgent.TryMatchAsync → SkillExecutionService.ExecuteAsync
                   └── 全部未命中 → QwenService.AnalyzeAsync（兜底）
```

- `SkillExecutionService` **完全保留**，Agent 匹配后也用它执行
- `QwenService` **保留**，作为兜底 LLM
- `SkillLibraryService` **保留**，但只存 Agent 学习到的模糊映射（不再存储全量技能）
- `ChatWindow` **基本不改**，加一个路由调用

### 现有技能数据迁移

用户已有的 skills.json → 按 ActionType 拆分到对应 Agent：

| 原 ActionType | 迁移到 |
|--------------|--------|
| `run_program` | App-Agent |
| `invoke_tool` | Comp-Agent |
| `open_url` | Browser-Agent |
| `open_folder` | File-Agent |
| `run_powershell` | File-Agent |
| `ask_llm` | 不迁移（本来就是聊天） |
| `show_message` | 任意（使用频率低） |

---

## 十、实施步骤（给 Claude Code）

### 阶段一：基础结构（1次任务）
1. 创建 `IAgent` 接口和 `Router` 类
2. 实现 `AppAgent`（基于 AppScanner）
3. 实现 `CompAgent`（基于 ToolRegistry + 系统操作表）
4. 实现 `BrowserAgent`、`FileAgent`
5. 修改 `ChatWindow.SendMessage()` 增加路由调用

### 阶段二：匹配逻辑（✅ 已完成 2026-05-29）
1. 实现触发词提取
2. 实现拼音匹配
3. 实现 Agent 级联匹配
4. 处理边界情况（"开热点"→ Comp-Agent, "打开热点"→ 也走 Comp）

### 阶段三：LLM 兜底 + 学习（✅ 已完成 2026-05-30）
1. 修改 QwenService 接受 Agent 上下文
2. 实现 Agent.LearnAsync()
3. 重构 skills.json 存储
4. 旧数据迁移

### 阶段四：设置 UI 调整（✅ 已完成 2026-05-30）
1. 设置页技能列表改为按 Agent 分组显示
2. 增加"清除某 Agent 学习记录"功能
3. 增加路由/Agent 状态预览

---

## 十一、关键边界决策

| 场景 | 处理 |
|------|------|
| "打开计算器" | App-Agent 命中，不经过 Comp-Agent |
| "计算器"（无触发词） | 路由判断无触发词 → 走 LLM |
| "开热点" | Router "开"→ Comp-Agent 命中 |
| "帮我打开热点" | Router "打开"→ App-Agent 找不到 → 传给 LLM → LLM 创建映射 |
| "打开微信" | App-Agent 命中 ✅ |
| "打开设置" | App-Agent 找不到"设置" → Comp-Agent 命中 |
| "搜索今天的新闻" | Browser-Agent 命中 |
| "你好" | 无触发词 → LLM 聊天 |
