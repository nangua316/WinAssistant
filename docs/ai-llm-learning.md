# AI 功能：LLM 兜底 + 学习机制

> 状态：代码完成，待编译验证 (45 单元测试已通过)

## 架构概览

```
用户输入 → Router.RouteAsync()
    ├── Agent 匹配成功 → 直接执行
    ├── 模糊匹配 → QwenService.AnalyzeWithAgentContextAsync()
    │       └── LLM 分析后返回动作 → 执行 + LearnAsync() 学习
    └── 完全不匹配 → 返回失败

学习结果持久化到 skills.json
```

## 核心变更

### Router.cs
- `RouteAsync(string input)` → 返回 `(AgentMatchResult?, AgentRouteContext)` 元组
  - `AgentMatchResult` — 匹配结果
  - `AgentRouteContext` — 路由上下文（含级联记录、使用的 Agent、ActionType 等）
- `GetAgentByType()` — 根据 AgentType 获取对应 Agent 实例

### QwenService.cs
- `AnalyzeWithAgentContextAsync(string input, AgentRouteContext context, List<SkillDefinition> skills)` — 带 Agent 上下文分析
- 支持返回三种动作：run_program / open_url / invoke_tool
- `Update` 动作：LLM 判断用户想修改已有技能

### IAgent 接口 + 四个 Agent 实现
- `LearnAsync(string originalInput, string actionObject, Dictionary<string,string>? llmParams)` — 学习接口
  - **AppAgent**：验证路径是否存在，识别可执行文件
  - **CompAgent**：匹配 ToolRegistry 工具 / 系统动作 / LLM 返回的 toolId
  - **BrowserAgent**：将 URL 操作记录为技能
  - **FileAgent**：记录文件路径操作

### SkillLibraryService.cs
- `skills.json` 按 Agent 分组存储：
  ```json
  {
    "Computer": [ ... ],
    "App": [ ... ],
    "Browser": [ ... ],
    "File": [ ... ]
  }
  ```
- 旧格式平铺列表 → 自动迁移到新格式
- `GetByAgent(AgentType)` / `GetAllFlat()` 查询方法

### 设置 UI（MainPage.xaml.cs）
- AI 设置页技能列表**按 Agent 分组**展示
- 每个组显示：Agent 图标、名称、技能数量、触发词预览
- 每组「清除」按钮 → 只删除该 Agent 的学习记录
- 路由状态预览：显示各 Agent 的触发词列表

### 测试 (WinAssistant.Tests)
- `SkillLibraryServiceTests` (7) — 空加载、CRUD、分组、展平
- `AgentMatchingTests` (18) — RouteAsync、最长触发词、级联匹配
- `RouterContextTests` (8) — 路由上下文字段、级联记录、GetAgentByType
- `QwenServiceTests` (12) — 配置、SkillDefinition 构建、AgentRouteContext

## 关键设计决策
1. 匹配优先于 LLM — 本地规则匹配成功不走 LLM
2. 学习结果反哺本地规则 — 下次同输入直接匹配，不经过 LLM
3. AppAgent 匹配时验证路径是否存在 → 不存在则触发 LearnAsync 纠正
4. skills.json 分组存储 + 旧格式自动迁移
