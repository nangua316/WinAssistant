using System.Diagnostics;
using Microsoft.Win32;
using WinAssistant.Controls.Tools;
using WinAssistant.Models;

namespace WinAssistant.Services;

public class SkillExecutionService
{
    private readonly QwenService _qwen;

    public SkillExecutionService(QwenService qwen)
    {
        _qwen = qwen;
    }

    public async Task<ExecResult> ExecuteAsync(SkillDefinition skill, string originalInput)
    {
        try
        {
            return skill.ActionType switch
            {
                SkillActionType.invoke_tool => ExecResult.Ok(ExecuteTool(skill)),
                SkillActionType.run_program => RunProgram(skill),
                SkillActionType.run_powershell => await RunPowerShell(skill),
                SkillActionType.open_url => ExecResult.Ok(OpenUrl(skill)),
                SkillActionType.open_folder => ExecResult.Ok(OpenFolder(skill)),
                SkillActionType.ask_llm => ExecResult.Ok(await AskLLM(skill, originalInput)),
                SkillActionType.show_message => ExecResult.Ok(ShowMessage(skill)),
                _ => ExecResult.Fail($"未知动作类型: {skill.ActionType}")
            };
        }
        catch (Exception ex)
        {
            return ExecResult.Fail($"执行失败: {ex.Message}");
        }
    }

    private static string ExecuteTool(SkillDefinition skill)
    {
        var toolId = skill.ActionParams.GetValueOrDefault("toolId");
        if (string.IsNullOrEmpty(toolId)) return "未指定工具 ID";
        var tool = ToolRegistry.Get(toolId);
        if (tool == null) return $"未找到工具: {toolId}";
        if (tool.IsOneClickAction)
            return tool.Activate() ?? $"✅ 已执行工具: {tool.Name}";
        // Window-based tool: open the tool window asynchronously
        App.DispatcherQueue.TryEnqueue(() => ToolHostWindow.OpenOrActivate(tool));
        return $"✅ 已打开工具: {tool.Name}";
    }

    private static ExecResult RunProgram(SkillDefinition skill)
    {
        var path = skill.ActionParams.GetValueOrDefault("path");
        if (string.IsNullOrEmpty(path)) return ExecResult.Fail("未指定程序路径");
        var args = skill.ActionParams.GetValueOrDefault("arguments") ?? "";

        // Auto-discover if path doesn't exist
        var resolvedPath = FindExecutable(path);
        if (resolvedPath == null)
            return ExecResult.Fail($"找不到程序: {path}\n请确认程序已安装或指定正确路径。");

        Process.Start(new ProcessStartInfo(resolvedPath, args) { UseShellExecute = true });
        return ExecResult.Ok($"✅ 已启动: {resolvedPath}");
    }

    private async Task<ExecResult> RunPowerShell(SkillDefinition skill)
    {
        var script = skill.ActionParams.GetValueOrDefault("script");
        if (string.IsNullOrEmpty(script)) return ExecResult.Fail("未指定脚本内容");

        var psi = new ProcessStartInfo("powershell")
        {
            Arguments = $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(15000);
        var output = await outTask;
        var error = await errTask;

        if (!exited)
        {
            try { process.Kill(); } catch { }
            return ExecResult.Fail("❌ PowerShell 脚本执行超时（15秒）");
        }

        var result = output.Trim();
        var errMsg = error.Trim();

        if (process.ExitCode != 0 && string.IsNullOrEmpty(result))
            return ExecResult.Fail($"❌ 脚本执行失败 (exit={process.ExitCode}): {errMsg}");

        return ExecResult.Ok(string.IsNullOrEmpty(result) ? "✅ 脚本已执行（无输出）" : result);
    }

    private static string OpenUrl(SkillDefinition skill)
    {
        var url = skill.ActionParams.GetValueOrDefault("url");
        if (string.IsNullOrEmpty(url)) return "未指定网址";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return $"✅ 已打开: {url}";
    }

    private static string OpenFolder(SkillDefinition skill)
    {
        var path = skill.ActionParams.GetValueOrDefault("path");
        if (string.IsNullOrEmpty(path)) return "未指定路径";
        Process.Start("explorer.exe", path);
        return $"✅ 已打开文件夹: {path}";
    }

    private async Task<string> AskLLM(SkillDefinition skill, string originalInput)
    {
        var question = skill.ActionParams.GetValueOrDefault("question") ?? originalInput;
        return await _qwen.ChatAsync(question);
    }

    private static string ShowMessage(SkillDefinition skill)
    {
        var title = skill.ActionParams.GetValueOrDefault("title") ?? "提示";
        var content = skill.ActionParams.GetValueOrDefault("content") ?? "";
        return $"【{title}】{content}";
    }

    /// <summary>
    /// Try to locate an executable by checking the path directly, then PATH via where.exe,
    /// then the App Paths registry key, then common directories.
    /// </summary>
    private static string? FindExecutable(string path)
    {
        if (File.Exists(path)) return path;

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName)) return null;

        // 1. Try where.exe (searches system PATH)
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            if (proc.ExitCode == 0 && output.Length > 0)
            {
                var found = output.Split('\n', '\r')[0].Trim();
                if (File.Exists(found)) return found;
            }
        }
        catch { }

        // 2. Try registry: App Paths (e.g. HKLM\...\App Paths\WeChat.exe)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @$"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
            if (key != null)
            {
                var defPath = key.GetValue("") as string;
                if (!string.IsNullOrEmpty(defPath) && File.Exists(defPath))
                    return defPath;
            }
        }
        catch { }

        // 3. Try common directories
        var dirs = new List<string>();
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); } catch { }
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)); } catch { }
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));

        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }
}

public class ExecResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    public static ExecResult Ok(string msg) => new() { Success = true, Message = msg };
    public static ExecResult Fail(string msg) => new() { Success = false, Message = msg };
}
