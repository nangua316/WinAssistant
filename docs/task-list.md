# WinAssistant 功能清单

## 🟡 待编译验证

| 功能 | 文档 | 说明 |
|------|------|------|
| AI（LLM兜底+学习机制） | `docs/ai-llm-learning.md` | Router 元组、QwenService Update、skills.json 分组、UI 按 Agent 分组。45 单元测试已完成 |

## ⚪ 待编码

| 功能 | 文档 | 阶段 | 说明 |
|------|------|------|------|
| 剪贴板功能（完整） | `docs/clipboard-complete.md` | 方案 | 复制提醒 + 剪贴板历史整合进启动台（三Tab） |
| 启动台浏览器链接 | `docs/launchpad-browser-link.md` | 方案 | 右键添加 URL、浏览器选择、favicon 自动获取、编辑功能 |

## ✅ 已完成并验证

| 功能 | 说明 |
|------|------|
| Toast 显示修复 | HotKeyToast 改用 Win32 GDI 同步绘制 + DispatcherTimer，正常显示 |
| CapsLock 按键监听 | 键盘钩子监听 VK_CAPITAL，Toast 提示大写锁定状态 |

## 🔴 待排查

---

> 状态更新请告知，会自动同步此文件。
