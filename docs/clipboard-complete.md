# WinAssistant 剪贴板功能 — 完整方案

## 概述

两功能一体，共享一套剪贴板监听机制：

```
ClipboardMonitorService（监听剪贴板变化）
        │
        ├─→ 🔔 复制成功提醒（Toast）
        │     + 常规设置开关
        │
        └─→ 📂 剪贴板历史（SQLite 存储）
              + 整合进启动台 Tab 2
```

---

## 一、复制成功提醒（Toast）

### 功能
复制文本/图片/文件时，用 HotKeyToast 确认操作成功。

### 格式检测 + Toast 摘要

```
if (ContainsFileDropList)
    → 单个文件: 📁 filename.ext (size)
    → 多个文件: 📁 已复制 N 个文件

elif (ContainsImage)
    → 🖼️ 已复制图片 (1920×1080, PNG)

elif (ContainsText && 非空)
    → ≤ 20 字: 📋 text（完整显示）
    → > 20 字: 📋 已复制文本 (N 字)

else → 不提示（不支持的类型）
```

### HotKeyToast 复用

零改动。全部使用现有接口：
```csharp
// 短文本
HotKeyToast.Show("📋 复制成功", "会议安排.docx");
// 长文本/文件/图片
HotKeyToast.Show("📋 已复制文本 (243字)");
HotKeyToast.Show("📁 已复制: report.pdf (1.5MB)");
HotKeyToast.Show("🖼️ 已复制图片 (1920×1080)");
```

### 设置开关

常规设置页面新增卡片（开机自启下方）：

```
📋 复制成功提醒      [开/关]
  复制文件、图片或文本时显示 Toast 确认
```

`AppSettings.ClipboardCopyToastEnabled`，默认 `true`。关闭后不监听剪贴板。

### 边界情况

| 场景 | 处理 |
|------|------|
| 应用自身写剪贴板 | 标志位跳过 |
| 同内容连续复制 | 500ms 防抖 + 内容指纹匹配跳过 |
| 空剪贴板 / 0 字节 | 不提示 |
| 后台程序刷剪贴板 | 防抖覆盖 |
| 不支持的格式 | 不提示 |
| 短文本 < 20 字 | 正常显示全文 |

---

## 二、剪贴板历史 — 整合进启动台

### 三合一启动台

将现在的启动台从单页应用搜索 **改造为 Tab 切换**，所有 Tab 共享同一个搜索框：

```
┌──────────────────────────────────────────────┐
│  [ 应用台 ]   [ 📋 剪贴板 ]   [ 📁 文件 ]   │  ← Tab 标签，点击切换
├──────────────────────────────────────────────┤
│                                              │
│  内容区域（根据当前 Tab 动态变化）             │
│                                              │
│  ┌──────────────────────────────────┐        │
│  │ [🔍]  搜索...                    │        │  ← 搜索框，搜索当前 Tab 内容
│  └──────────────────────────────────┘        │
└──────────────────────────────────────────────┘
```

### 设计要点

- **Tab 标签在最上方**，三个标签一目了然
- **激活的 Tab 高亮**，其他 Tab 灰显
- **搜索框在下方**，仅搜索当前 Tab 范围内的内容
- 搜索框左侧图标跟随当前 Tab：🔍 / 📋 / 📁
- 所有 Tab 共用一个搜索框，不用时收起来不占空间

### Tab 切换方式

| 方式 | 说明 |
|------|------|
| ① 鼠标点击 Tab 标签 | **主要方式**，打开启动台一眼可见，点就切 |
| ② Ctrl+Tab | 键盘切换，不抬手 |

只有这两种方式，**不需要输入前缀**，**不需要记忆任何命令**。

### Tab 1：应用台（现有功能，不变）

```
┌──────────────────────────────┐
│ [应用台]                      │
│                              │
│  [微信] [Chrome] [VS Code]   │
│  [飞书] [网易云] [计算器]     │
└──────────────────────────────┘
```

右键菜单：
- 添加应用 ✅
- 添加文件夹 ✅
- 添加浏览器链接（待实现）
- 打开设置 ✅

### Tab 2：剪贴板（新增）

```
┌──────────────────────────────┐
│ [📋 剪贴板]                   │
│                              │
│ ★ 今天会议的安排...    10:30 │
│   report.pdf (3个文件) 10:28 │
│   🖼️ 截图 1920×1080   10:25 │
│   https://github.com/  10:20 │
│ ★ const x = 42;       10:15 │
│                              │
│  ─── 昨天 ───                │
│   服务器配置文档       昨天  │
└──────────────────────────────┘
```

#### 列表项显示

每条历史显示：
- **类型图标**：📝 文本 / 🖼️ 图片 / 📁 文件 / 🔗 链接
- **内容预览**：文本前 50 字 / 图片缩略图 / 文件名
- **来源**：小字显示从哪个应用复制来的
- **时间**：相对时间（10分钟前 / 昨天 14:30）
- **收藏标记**：⭐ 置顶

#### 时间分组

自动按时间分段：今天 / 昨天 / 本周 / 更早，中间显示分隔线。

#### 交互

| 操作 | 行为 |
|------|------|
| 单击条目 | 内容写入剪贴板，用户手动 Ctrl+V |
| 双击/回车 | 内容写回剪贴板 + 自动粘贴到前台窗口 |
| 右键 | 收藏 / 删除 |
| 鼠标悬停 | 图片放大预览 / 长文本更多预览 |

#### 自动粘贴流程（双击/回车）

```
用户双击某条目
  → 内容写入剪贴板
  → 隐藏启动台
  → 激活启动台打开前的窗口（启动台打开时记录 ForegroundWindow）
  → SendInput Ctrl+V 模拟粘贴
```

这是 Ditto 的经典工作流，熟手用起来极快。

### Tab 3：文件搜索（Everything 集成）

#### 方案

集成 **Everything**（voidtools），Windows 上最快的文件搜索引擎。通过 Everything.NET 库（命名管道）通信。

```
┌──────────────────────────────┐
│ [📁 文件]                     │
│                              │
│  [🔍]  搜索文件...            │
│                              │
│  📄 report.docx          C:\ │
│  📄 会议纪要.docx        D:\ │
│  📂 Project Alpha        C:\ │
│  📷 screenshot.png       D:\ │
│                              │
│  共 42 个结果  0.003s        │
└──────────────────────────────┘
```

#### 集成方式

| 选项 | 说明 | 推荐度 |
|------|------|--------|
| **Everything.NET NuGet** | 命名管道通信，无 DLL 依赖，异步，.NET 9 兼容 | ⭐⭐⭐⭐⭐ |
| P/Invoke Everything.dll | 需分发 dll，较复杂 | ⭐⭐ |
| es.exe CLI | 启动进程，慢 | ⭐ |

**采用 Everything.NET 方案：**

```xml
<!-- WinAssistant.csproj -->
<PackageReference Include="EverythingNET" Version="4.2.0" />
```

```csharp
// FileSearchService.cs
using EverythingNET;

public class FileSearchService
{
    public async Task<List<string>> SearchAsync(string query)
    {
        var search = new EverythingSearch(query);
        var results = await search.SearchAsync();
        return results.Select(r => r.FullPath).ToList();
    }
}
```

#### 没装 Everything 的情况

Everything.NET 自带检测，未安装时会自动 fallback 到标准 `Directory.GetFiles` 搜索（速度和范围有限）。

额外处理：
- 搜索框下方显示提示：`⚠️ 检测到未安装 Everything，搜索速度受限`
- 提供一键下载链接：`voidtools.com`
- 用户选择安装后，检测到 Everything 服务启动则自动切换

#### 搜索体验

- 搜索框输入即搜（300ms 防抖）
- 结果显示：图标 + 文件名 + 所在文件夹（缩短显示）
- 双击结果 → 打开文件（`Process.Start("explorer.exe", "/select,\"path\"")` 打开所在位置）
- 回车 → 打开文件
- 显示搜索结果数量和耗时（Everything 通常 < 0.01s）

#### 依赖关系

- ✅ **Everything 可直接打包进 WinAssistant**，安装时一并部署
- Everything 是免费软件（freeware），允许自由分发和捆绑
- WinAssistant 安装目录下放 `tools\Everything\Everything.exe`，约 2MB
- 首次启动时自动运行 Everything 建立索引
- Everything 在后台静默运行，用户无感知

#### 部署方式

**方案：便携模式（推荐）**

Everything 支持便携模式运行，无需安装，不写注册表：

```
WinAssistant 安装目录\
├── WinAssistant.exe
├── tools\
│   └── Everything\
│       ├── Everything.exe         ← 打包的 Everything
│       ├── Everything.ini          ← 预配置，开机不自启、不显示托盘
│       └── Everything.db           ← 索引数据库（首次运行后生成）
```

**Everything.ini 预配置：**

```ini
[General]
run_as_admin=0
show_tray_icon=0         ← 不显示托盘图标
auto_start=0             ← 不注册开机自启（WinAssistant 管理生命周期）
etp_server=0
http_server=0
```

**WinAssistant 启动时：**

```csharp
// 检测 Everything 进程是否运行
// 如果没运行，启动 tools\Everything\Everything.exe（带 -silent 参数）
// 等待索引加载完成
// 搜索时通过 Everything.NET 通信
```

**WinAssistant 退出时：**

可选项：保持 Everything 后台运行（下次搜索更快），或关闭它。建议保持运行，内存占用 ~50MB，对现代电脑无感知。

---

## 三、数据存储

```
%AppData%\WinAssistant\
├── Clipboard\
│   ├── clipboard.db          ← SQLite（文本/HTML/文件列表/元数据）
│   └── images\
│       ├── {guid}.png         ← 图片原文件
│       └── {guid}_thumb.png   ← 缩略图 128x128
```

### SQLite 表结构

```sql
CREATE TABLE IF NOT EXISTS clipboard_history (
    id          TEXT PRIMARY KEY,          -- GUID
    type        INTEGER NOT NULL,          -- 0=文本 1=图片 2=文件 3=HTML/富文本
    content     TEXT,                      -- 文本内容 / HTML / 文件路径JSON
    source_app  TEXT,                      -- 来源进程名
    image_path  TEXT,                      -- 图片文件路径（type=1 时）
    thumbnail   TEXT,                      -- 缩略图路径
    is_pinned   INTEGER DEFAULT 0,         -- 0=不固定 1=固定
    created_at  TEXT NOT NULL              -- ISO8601 时间戳
);

CREATE INDEX idx_clipboard_created ON clipboard_history(created_at DESC);
```

### 淘汰策略

- 最大条数可配置（默认 500）
- 超出时删除最旧的未收藏条目
- 收藏条目保留不动

### 图片存储

- 原图最大 20MB，超出不存储
- 自动生成 128x128 缩略图用于列表预览
- 删除条目时同时删除对应图片文件

---

## 四、设置项

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `ClipboardCopyToastEnabled` | bool | true | 复制时是否显示 Toast |
| `ClipboardHistoryEnabled` | bool | true | 是否记录剪贴板历史 |
| `MaxClipboardHistory` | int | 500 | 最大历史条数 |
| `MaxClipboardImageSize` | int | 20 | 单张图片最大 MB 数 |
| `ClipboardAutoPaste` | bool | true | 双击条目时自动粘贴到前台 |

---

## 五、文件变更清单

| 操作 | 文件 | 说明 |
|------|------|------|
| 🆕 | `Services/ClipboardMonitorService.cs` | 监听剪贴板变化，发 Toast + 入库 |
| 🆕 | `Services/ClipboardHistoryDb.cs` | SQLite 存储 / 图片文件管理 / 淘汰 |
| 🆕 | `Models/ClipboardEntry.cs` | 历史条目 + 类型枚举 |
| ✏️ | `Pages/LaunchpadPage.xaml` | 三 Tab 布局 + 剪贴板列表模板 |
| ✏️ | `Pages/LaunchpadPage.xaml.cs` | Tab 切换逻辑 / 粘贴逻辑 |
| ✏️ | `ViewModels/LaunchpadPageViewModel.cs` | 扩展为支持三 Tab |
| 🆕 | `ViewModels/ClipboardHistoryViewModel.cs` | 剪贴板列表逻辑 |
| ✏️ | `Models/AppSettings.cs` | 5 个剪贴板相关属性 |
| ✏️ | `MainPage.xaml` | 常规设置加 Toast 开关卡片 |
| ✏️ | `Helpers/AppLauncher.cs` | 支持模拟粘贴到前台窗口 |
| ❌ 不改 | `Helpers/HotKeyToast.cs` | 完全复用 |

---

## 六、与现有功能的关系

| 现有功能 | 与剪贴板功能的关系 |
|---------|------------------|
| 启动台（Tab 1 应用台） | 不变，Tab 切换不影响现有搜索/启动逻辑 |
| 输入法管理 | 各自独立，无关联 |
| AI 兜底+学习 | 各自独立，无关联 |
| 浏览器链接 | 同属启动台扩展，**建议先做剪贴板** |
| 全局热键 | 无新增热键，不增加冲突 |

---

## 七、隐私策略

- 不排除任何内容类型，正常记录所有复制操作
- 数据仅存储在本地，不上传
- 提供一键清空历史功能
- 图片物理删除，不留残留

---

## 八、实现建议

### 阶段一：基础（可独立发布）

| 顺序 | 内容 |
|------|------|
| 1 | ClipboardMonitorService — 监听 + 发 Toast（复制提醒） |
| 2 | 常规设置开关卡片 |
| 3 | ClipboardHistoryDb — SQLite 存储 |
| 4 | Tab 改造 + 剪贴板历史列表（只读浏览） |

### 阶段二：体验完善

| 顺序 | 内容 |
|------|------|
| 5 | 双击粘贴功能 |
| 6 | 收藏/删除 |
| 7 | 图片缩略图显示 |
| 8 | 时间分组 |

### 阶段三：文件搜索 Tab

| 顺序 | 内容 |
|------|------|
| 9 | 文件搜索 Tab 实现 |
