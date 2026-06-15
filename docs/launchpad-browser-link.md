# 启动台：右键添加浏览器链接

> 状态：方案已定，待编码

## 一、数据模型变更

### LaunchpadItem.cs

```csharp
public class LaunchpadItem
{
    // 现有字段
    public string Name { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Aumid { get; set; } = "";
    public string? ToolId { get; set; }

    // 🔴 新增
    public LaunchpadItemType ItemType { get; set; } = LaunchpadItemType.App;  // 默认 App，向后兼容
    public string? Url { get; set; }                                          // 仅 ItemType=Url 时有效
    public string? BrowserPath { get; set; }                                  // null = 默认浏览器
}

public enum LaunchpadItemType
{
    App = 0,     // 已有数据自动映射到 App
    Folder = 1,  // 已有
    Url = 2      // 新增
}
```

**向后兼容**：`ItemType=App(0)` 是 `enum` 默认值，旧 `settings.json` 没有该字段时自动命中，零迁移成本。

## 二、浏览器发现机制

### BrowserDetector.cs（新增）

自动发现系统已安装的浏览器，供用户选择。

**扫描策略：**

| 优先级 | 方法 | 说明 |
|--------|------|------|
| 1 | 注册表 `HKEY_LOCAL_MACHINE\SOFTWARE\Clients\StartMenuInternet` | 获取系统已注册的浏览器列表 |
| 2 | 各浏览器注册的 App Paths 完整路径 | `App Paths\chrome.exe` 等 |
| 3 | 常见安装路径兜底 | 扫描 `Program Files` 下的标准路径 |

**预置浏览器列表（自动按安装情况返回）：**

| 浏览器 | RegKey | 常见路径 |
|--------|--------|---------|
| Google Chrome | `ChromeHTML` | `%ProgramFiles%\Google\Chrome\Application\chrome.exe` |
| Microsoft Edge | `EdgeHTML` / `MSEdgeHTM` | `%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe` |
| Mozilla Firefox | `FirefoxURL` | `%ProgramFiles%\Mozilla Firefox\firefox.exe` |
| Brave | `BraveHTML` | `%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe` |
| Opera | `OperaURL` | `%ProgramFiles%\Opera\launcher.exe` |
| 360安全浏览器 | `360Chrome` | 常见 `%ProgramFiles(x86)%\360\Chrome\Application\360chrome.exe` |

**返回值：**

```csharp
public record BrowserInfo(string DisplayName, string ExePath);
public static List<BrowserInfo> DetectInstalledBrowsers();
public static BrowserInfo? GetDefaultBrowser(); // 系统默认浏览器
```

**默认浏览器**：查询注册表 `HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice` 拿 ProgId，再映射到对应浏览器路径。

## 三、自动获取 Favicon

### WebIconService.cs（新增）

```
用户输入 URL → HttpClient GET → 下载 favicon.ico
                                    ↓
                            保存到 AppData 缓存目录
                            缓存文件名: md5(domain).ico
                                    ↓
                            显示为启动台图标
```

**规则：**
1. 首选 `https://domain/favicon.ico`
2. 如果 404 → 尝试从 HTML 的 `<link rel="icon">` 中提取
3. 全部失败 → 显示默认地球图标 🌐（内置 fallback）
4. 下载到的图标缓存到 `%AppData%\WinAssistant\Cache\favicons\`，后续直接读缓存

**对话框交互：**
- 输入 URL → 失焦后自动触发后台下载
- 若下载成功 → 图标区域实时预览显示
- 若失败 → 显示默认 🌐 图标
- 用户也可手动选择一个本地图片作为图标（可选增强）

## 四、添加对话框

右键启动台空白处 → 选择「添加浏览器链接」：

```
┌─────────────────────────────────────────────┐
│  添加浏览器链接                              │
│                                              │
│  URL:     [https://github.com/likan]    ← 必填 │
│                                              │
│  ┌──────┐                                    │
│  │ 🌐   │  名称: [github]              ← 自动填 │
│  │ fav  │  域名: github.com              域名 │
│  └──────┘                                    │
│                                              │
│  浏览器: ▾ 默认浏览器 （Microsoft Edge）  ← 可选 │
│           Google Chrome                       │
│           Mozilla Firefox                     │
│           Brave                               │
│           自定义... → 浏览 .exe               │
│                                              │
│       [  取消  ]              [  添加  ]      │
└─────────────────────────────────────────────┘
```

**交互细节：**
1. URL 输入框失去焦点时 → 自动补 `https://`（如果缺前缀）
2. 同时触发后台：下载 favicon + 提取域名 → 自动填入名称
3. 名称可手动修改
4. 浏览器下拉：`BrowserDetector.DetectInstalledBrowsers()` 结果 + "默认浏览器" + "自定义..."
5. 选择"自定义..." → 弹出文件选择器选浏览器 exe
6. URL 空 / 格式不合法 → 添加按钮禁用

## 五、编辑功能（已添加的链接）

右键点击已存在的 URL 类型 Item → 菜单增加「编辑链接」：

```
右键菜单（URL 类型 Item）:
  ├─ 编辑链接     ← 弹出修改对话框，同添加界面，预填当前值
  └─ 移除         ← 已有
```

**编辑可修改：**
- URL
- 名称
- 浏览器（可切换为默认浏览器或另一款）

保存后：如有 URL 变更 → 重新下载 favicon + 更新名称建议（但保留用户手动修改的名称）

**普通 App/Folder Item 的右键菜单不变：** 只有"移除"。

## 六、启动逻辑

### AppLauncher.cs 修改

```csharp
public static string LaunchOrActivate(LaunchpadItem item)
{
    if (item.ItemType == LaunchpadItemType.Url && !string.IsNullOrEmpty(item.Url))
    {
        if (!string.IsNullOrEmpty(item.BrowserPath) && File.Exists(item.BrowserPath))
        {
            // 指定浏览器打开
            Process.Start(new ProcessStartInfo
            {
                FileName = item.BrowserPath,
                Arguments = item.Url,
                UseShellExecute = true
            });
        }
        else
        {
            // 默认浏览器打开
            Process.Start(new ProcessStartInfo
            {
                FileName = item.Url,
                UseShellExecute = true
            });
        }
        return "launch";
    }
    // 原有 App/Folder 逻辑不变...
}
```

**注意**：`LaunchOrActivate` 需要在 `LaunchpadPage.xaml.cs` 或 ViewModel 中传入整个 `LaunchpadItem`，而非只传 path。现在调用处用 `vm.Model` 传进去。

## 七、启动台显示

URL 类型 Item 在网格中：

```
┌──────────┐
│  🌐      │   ← favicon（下载失败时显示内置地球图标）
│  github  │
│ github.io│   ← 小字显示域名（新加 ViewModel 属性 DomainDisplay）
└──────────┘
```

**LaunchpadItemViewModel 新增属性：**
- `IsUrl` → `Model.ItemType == LaunchpadItemType.Url`
- `DomainDisplay` → 从 URL 中提取的域名（`new Uri(url).Host`），仅 URL 类型显示
- `ContextMenuText` → URL 类型显示"移除链接"，App 类型显示"移除应用"

## 八、搜索支持

URL 类型的 Item 参与搜索，匹配：
- 名称（已有）
- 域名（新增拼音搜索，如输入 `github` 匹配 `github.com`）

## 九、图标缓存策略

| 场景 | 行为 |
|------|------|
| 首次添加 | 后台下载 favicon → 磁盘缓存 `favicons/{md5domain}.ico` |
| 启动台加载 | 先读缓存 → 有则直接显示 → 无则显示默认 🌐 |
| 编辑更改 URL | 重新下载 favicon → 覆盖缓存 |
| 删除 URL Item | 不删除缓存（可能被其他 Item 引用）→ 留待自然覆盖 |

## 十、兼容性

| 场景 | 表现 |
|------|------|
| 旧版 settings.json（无 ItemType） | `enum` 默认值 0 = App，原有 app 和 folder 不受影响 |
| URL Item 手动修改 settings.json 删除 Url | 启动时 Url 为空 → 不执行，静默跳过 |
| 选中的浏览器 exe 被卸载 | BrowserPath 指向不存在的文件 → 自动降级为默认浏览器 + 显示 Toast 提示 |
| 离线状态添加 URL | favicon 下载失败 → 显示默认 🌐，不影响添加 |

## 十一、文件变更清单

| 操作 | 文件 | 说明 |
|------|------|------|
| ✏️ 修改 | `Models/LaunchpadItem.cs` | 加 `ItemType` / `Url` / `BrowserPath` |
| 🆕 新增 | `Services/BrowserDetector.cs` | 自动发现已安装浏览器 |
| 🆕 新增 | `Services/WebIconService.cs` | 下载并缓存 favicon |
| ✏️ 修改 | `Helpers/AppLauncher.cs` | 新增 `LaunchOrActivate(LaunchpadItem)` 重载，处理 URL 分支 |
| ✏️ 修改 | `ViewModels/LaunchpadPageViewModel.cs` | 新增 URL 类型添加/编辑/启动/图标加载/搜索 |
| ✏️ 修改 | `Models/LaunchpadItemViewModel.cs` | 加 `IsUrl` / `DomainDisplay` 属性 |
| ✏️ 修改 | `Pages/LaunchpadPage.xaml.cs` | 右键菜单新增「添加浏览器链接」、「编辑链接」|
| ✏️ 修改 | `Pages/LaunchpadPage.xaml` | URL Item 显示差异（域名小字、编辑菜单） |
| ❌ 不改 | `App.xaml.cs` | 无需注册新服务（按需调用） |

## 十二、实现阶段建议

| 阶段 | 内容 | 预估 |
|------|------|------|
| Phase 1 | 数据模型 + BrowserDetector + 添加对话框 + 启动逻辑 | 核心功能可用 |
| Phase 2 | WebIconService + favicon 缓存 + 图标显示 | 体验完整 |
| Phase 3 | 编辑功能 + 搜索匹配域名 + 降级处理 | 完善收尾 |

建议一次性全做，改动量不大（模型+UI+2个服务类）。
