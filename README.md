# WinAssistant

一个基于 WinUI 3 / Windows App SDK 的 Windows 桌面效率助手，目标是让常用操作可以通过启动台或全局热键快速完成。

## 主要功能

- **启动台（Launchpad）**
  - 快速启动应用、文件夹、浏览器链接
  - 拼音 / 关键词搜索
  - 拖拽排序、交换位置、移至末尾
  - 浅色 / 深色主题实时切换

- **输入法管理**
  - 按窗口自动切换输入法、语言、全半角、大小写状态
  - 实时 Toast 提示 CapsLock / 输入法 / 中英文 / 全半角变化

- **剪贴板增强（规划中）**
  - 复制成功 Toast 提醒
  - 剪贴板历史记录与快速粘贴

- **全局热键与 Toast**
  - 全局快捷键唤起启动台
  - 低侵入式 Toast 提示

## 技术栈

- .NET 9
- WinUI 3 / Windows App SDK 1.6+
- C# / MVVM
- SQLite（剪贴板历史等本地数据）
- Win32 API / TSF（输入法相关）

## 项目结构

```text
WinAssistant/              主程序
WinAssistant.Tests/        单元测试
docs/                      功能设计文档
imetest/                   输入法监听测试工具
```

## 构建与运行

要求：Windows 10 19041+ 或 Windows 11，已安装 .NET 9 SDK。

```powershell
# Debug 构建并启动（从仓库根目录执行）
powershell -Command "Get-Process WinAssistant -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3; dotnet build WinAssistant\WinAssistant.csproj -c Debug --verbosity quiet; Start-Process -FilePath 'WinAssistant\bin\Debug\net9.0-windows10.0.26100.0\win-x64\WinAssistant.exe'"
```

```powershell
# 不重新编译，直接重启
powershell -Command "Get-Process WinAssistant -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3; Start-Process -FilePath 'WinAssistant\bin\Debug\net9.0-windows10.0.26100.0\win-x64\WinAssistant.exe'"
```

## 运行测试

```powershell
dotnet test WinAssistant.Tests\WinAssistant.Tests.csproj
```

## 查看调试日志

```powershell
powershell -Command 'cat "$env:TEMP\WinAssistant_dbg.txt" -Tail 50'
```

## 说明

本项目是个人开发中的效率工具，功能持续迭代中。欢迎提 Issue 或建议。
