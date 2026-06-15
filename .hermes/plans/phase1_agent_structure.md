# 阶段一：多 Agent 基础结构

## 任务概述
在 WinAssistant 2.0 项目中新增 Agent 架构：IAgent 接口 + Router + 4 个 Agent，并修改 ChatWindow 使用 Router。

项目路径：`D:\MyDoc\Code\WinAsistant2.0\WinAssistant\`

## 设计原则
1. 不删除/修改现有代码的核心逻辑（QwenService、SkillExecutionService、SkillLibraryService 完全保留）
2. Agent 只负责匹配和路由，执行仍然通过 SkillExecutionService
3. 新建 `Agents/` 文件夹放 Agent 相关文件
4. ChatWindow 改成先走 Router，Router 匹配不到再走 QwenService

## 需要创建的文件

### 1. `Agents/IAgent.cs` — Agent 接口

```csharp
namespace WinAssistant.Agents;

public enum AgentType { App, Computer, Browser, File }

public class AgentMatchResult
{
    public bool Success { get; set; }
    public AgentType AgentType { get; set; }
    public SkillActionType ActionType { get; set; }
    public Dictionary<string, string> ActionParams { get; set; } = [];
    public string? Reply { get; set; }  // 给用户的消息
}

public interface IAgent
{
    string Name { get; }
    AgentType Type { get; }
    string[] Triggers { get; }

    Task<AgentMatchResult?> TryMatchAsync(string actionObject);
}
```

### 2. `Agents/Router.cs` — 路由层

职责：根据输入提取触发词，匹配到对应 Agent，然后让 Agent 尝试匹配。

逻辑：
1. 按顺序检查所有 Agent 的 Triggers，看输入中是否包含（优先匹配最长的触发词，比如"打开网页"命中 Browser-Agent 而不是"打开"命中 App-Agent）
2. 提取触发词后面的内容作为 actionObject（"打开微信" → trigger="打开", actionObject="微信"）
3. 让对应 Agent 的 TryMatchAsync 去匹配
4. 匹配到 → 返回 AgentMatchResult
5. 匹配不到 → 返回 null（ChatWindow 再走 LLM）

也需要处理没有命中任何触发词的情况（直接返回 null 走 LLM）。

```csharp
public class Router
{
    private readonly List<IAgent> _agents;

    public Router()
    {
        _agents =
        [
            new AppAgent(),
            new CompAgent(),
            new BrowserAgent(),
            new FileAgent()
        ];
    }

    public async Task<AgentMatchResult?> RouteAsync(string input)
    {
        // 1. 按触发词长度降序匹配（长触发词优先，避免"打开网页"被"打开"截胡）
        // 2. 匹配到 → 提取 actionObject → 调对应 Agent.TryMatchAsync
        // 3. 有结果 → 返回，无结果 → 换下一个 Agent 再试
        // 4. 都未命中 → 返回 null
    }
}
```

匹配触发词时注意：如果"打开"匹配到了 App-Agent，但 App-Agent 的 TryMatchAsync 返回 null，Router 不应该再去试其他 Agent（因为触发词已经指明了意图是"打开"，去试 Comp-Agent 没有意义）。但如果"开"这个触发词，App-Agent 找不到对应应用，Router 可以尝试 Comp-Agent（"开热点"）。

实现一个级联逻辑：命中触发词后先走对应 Agent，如果匹配不到且触发词比较通用（如"开"、"打开"、"设置"），就尝试其他 Agent。

### 3. `Agents/AppAgent.cs` — 应用管理 Agent

基于 `AppScanner` 扫描的应用列表做匹配。

```csharp
public class AppAgent : IAgent
{
    public string Name => "App-Agent";
    public AgentType Type => AgentType.App;
    public string[] Triggers => ["打开", "启动", "运行", "关闭", "退出", "启动台"];

    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        var apps = AppScanner.ScanInstalledApps();
        
        // 1. 精确 + 包含匹配（不区分大小写）
        var matched = apps.FirstOrDefault(a =>
            a.Name.Equals(actionObject, StringComparison.OrdinalIgnoreCase)
            || a.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase));
        
        if (matched == null)
        {
            // 2. 拼音匹配（用 AppPickerItem 已有的拼音数据，或 PinyinHelper）
            // 遍历 apps, 调用 PinyinHelper.GetInitials/GetPinyin 看是否有匹配
            matched = apps.FirstOrDefault(a =>
            {
                var pinyin = PinyinHelper.GetPinyin(a.Name);
                var initials = PinyinHelper.GetInitials(a.Name);
                return pinyin.Contains(actionObject, StringComparison.OrdinalIgnoreCase)
                    || initials.Contains(actionObject, StringComparison.OrdinalIgnoreCase);
            });
        }
        
        if (matched != null)
        {
            return new AgentMatchResult
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
            };
        }
        
        return null;
    }
}
```

### 4. `Agents/CompAgent.cs` — 系统设置/工具 Agent

基于 `ToolRegistry` + 预置系统操作表。

```csharp
public class CompAgent : IAgent
{
    public string Name => "Comp-Agent";
    public AgentType Type => AgentType.Computer;
    public string[] Triggers => ["设置", "调整", "切换", "开关", "开启", "查", "查看"];

    // 预置系统操作表
    private static readonly List<SystemAction> SystemActions = new()
    {
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
    };

    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        if (string.IsNullOrEmpty(actionObject)) return null;
        
        var lower = actionObject.ToLowerInvariant();
        
        // 1. 匹配 ToolRegistry 内置工具
        var tool = ToolRegistry.All.FirstOrDefault(t =>
            t.Name.Contains(actionObject, StringComparison.OrdinalIgnoreCase));
        if (tool != null)
        {
            return new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.Computer,
                ActionType = SkillActionType.invoke_tool,
                ActionParams = new() { ["toolId"] = tool.Id },
                Reply = $"正在执行「{tool.Name}」..."
            };
        }
        
        // 2. 匹配预置系统操作表
        var sysAction = SystemActions.FirstOrDefault(sa =>
            sa.Triggers.Any(t => lower.Contains(t, StringComparison.OrdinalIgnoreCase)));
        if (sysAction != null)
        {
            return new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.Computer,
                ActionType = Enum.Parse<SkillActionType>(sysAction.ActionType),
                ActionParams = sysAction.ActionType == "open_url"
                    ? new() { ["url"] = sysAction.Command }
                    : new() { ["path"] = sysAction.Command },
                Reply = $"正在执行系统操作..."
            };
        }
        
        return null;
    }
}

// 辅助类
public class SystemAction
{
    public string[] Triggers { get; set; } = [];
    public string Command { get; set; } = "";
    public string ActionType { get; set; } = "run_program";
}
```

### 5. `Agents/BrowserAgent.cs` — 浏览器/搜索 Agent

```csharp
public class BrowserAgent : IAgent
{
    public string Name => "Browser-Agent";
    public AgentType Type => AgentType.Browser;
    public string[] Triggers => ["搜索", "百度", "谷歌", "上网", "打开网页", "浏览器"];

    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
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
                },
                Reply = $"正在搜索「{actionObject}」..."
            };
        }
        
        return null;
    }
}
```

### 6. `Agents/FileAgent.cs` — 文件操作 Agent（骨架）

```csharp
public class FileAgent : IAgent
{
    public string Name => "File-Agent";
    public AgentType Type => AgentType.File;
    public string[] Triggers => ["清理", "查找", "找到", "打开文件夹", "删除"];

    public async Task<AgentMatchResult?> TryMatchAsync(string actionObject)
    {
        // 预置功能
        if (!string.IsNullOrEmpty(actionObject) &&
            (actionObject.Contains("垃圾") || actionObject.Contains("缓存")))
        {
            return new AgentMatchResult
            {
                Success = true,
                AgentType = AgentType.File,
                ActionType = SkillActionType.run_powershell,
                ActionParams = new()
                {
                    ["script"] = "Write-Output '清理完成'"
                },
                Reply = "正在清理系统垃圾..."
            };
        }
        
        return null;
    }
}
```

## 需要修改的文件

### `Controls/AiChat/ChatWindow.cs`

修改 `SendMessage()` 方法：在调用 `QwenService` 之前，先走 Router。

```csharp
private async void SendMessage()
{
    // ... 前面的检查代码不变 ...
    
    try
    {
        AddMessage("assistant", "⏳ 思考中...");
        
        // ★ 新增：先走 Router
        var router = new Router();
        var routeResult = await router.RouteAsync(text);
        
        if (routeResult != null)
        {
            // Router 匹配到了 → 直接执行，不走 LLM
            RemoveLastMessage();
            
            // 构建临时 Skill 让 SkillExecutionService 执行
            var skill = new SkillDefinition
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = routeResult.AgentType.ToString(),
                ActionType = routeResult.ActionType,
                ActionParams = routeResult.ActionParams
            };
            
            var execResult = await App.SkillExecutionService.ExecuteAsync(skill, text);
            AddMessage("assistant",
                $"{routeResult.Reply}\n\n{execResult.Message}",
                routeResult.AgentType.ToString());
            return;
        }
        
        // Router 没匹配到 → 走原来的 LLM 流程
        var result = await App.QwenService.AnalyzeAsync(text, App.SkillLibraryService.AllSkills.ToList());
        
        // ... 后面的 match/create/chat 逻辑不变 ...
    }
    // ... catch 不变 ...
}
```

## 编译要求
- 新增 `Agents/` 目录和文件
- 可能需要将 PinyinHelper 改为 public 以供 AppAgent 使用（当前在 Helpers 下，检查其访问修饰符）
- 编译必须 0 error
- 不要修改现有功能逻辑

## 验证方法
1. `dotnet build WinAssistant.csproj` 通过
2. ChatWindow 输入 "打开微信" -> 应该通过 AppAgent 匹配，不走 LLM
3. ChatWindow 输入 "开热点" -> 应该通过 Router 级联走到 CompAgent 匹配
4. ChatWindow 输入 "你好" -> 没有触发词，走 LLM 聊天
