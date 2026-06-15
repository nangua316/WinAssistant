using WinAssistant.Agents;
using WinAssistant.Models;

namespace WinAssistant.Tests;

public class AgentMatchingTests
{
    [Fact]
    public async Task Router_NullOrEmptyInput_ReturnsNull()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("");
        Assert.Null(result);
        Assert.Equal("", context.OriginalInput);

        (result, context) = await router.RouteAsync("   ");
        Assert.Null(result);
        Assert.Equal("   ", context.OriginalInput);
    }

    [Fact]
    public async Task Router_NoTriggerWord_ReturnsNull()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("你好");
        Assert.Null(result);
        Assert.Null(context.MatchedTrigger);
        Assert.Equal("你好", context.ActionObject);
    }

    [Fact]
    public async Task Router_ChatOnlyInput_ReturnsNull()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("今天天气怎么样");
        Assert.Null(result);
        Assert.Null(context.MatchedTrigger);
    }

    [Fact]
    public async Task Router_LongestTriggerWins()
    {
        var router = new Router();
        // "打开网页" (BrowserAgent, 4 chars) should beat "打开" (AppAgent, 2 chars)
        var (result, context) = await router.RouteAsync("打开网页百度");

        Assert.NotNull(context.MatchedTrigger);
        Assert.Equal("打开网页", context.MatchedTrigger);
        Assert.Equal("百度", context.ActionObject);
    }

    [Fact]
    public async Task Router_SearchTrigger_RoutesToBrowser()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("搜索今天天气");

        Assert.Equal("搜索", context.MatchedTrigger);
        Assert.Equal("今天天气", context.ActionObject);
        Assert.Equal("Browser", context.PrimaryAgentType);
    }

    [Fact]
    public async Task Router_BrowserTrigger_AlwaysMatches()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("百度新闻");

        Assert.Equal("百度", context.MatchedTrigger);
        Assert.Equal("新闻", context.ActionObject);
    }

    [Fact]
    public async Task AppAgent_EmptyAction_ReturnsNull()
    {
        var agent = new AppAgent();
        var result = await agent.TryMatchAsync("");
        Assert.Null(result);
    }

    [Fact]
    public async Task BrowserAgent_WithAction_CreatesSearchUrl()
    {
        var agent = new BrowserAgent();
        var result = await agent.TryMatchAsync("今天的新闻");
        Assert.NotNull(result);
        Assert.Equal(SkillActionType.open_url, result.ActionType);
        // URL is encoded, check for encoded Chinese characters
        Assert.Contains("%E4%BB%8A%E5%A4%A9%E7%9A%84%E6%96%B0%E9%97%BB", result.ActionParams["url"]);
        Assert.Contains("baidu.com", result.ActionParams["url"]);
    }

    [Fact]
    public async Task BrowserAgent_EmptyAction_MayOpenDefaultBrowser()
    {
        var agent = new BrowserAgent();
        var result = await agent.TryMatchAsync("");
        // If Edge is installed, result should be run_program
        if (result != null)
        {
            Assert.Equal(SkillActionType.run_program, result.ActionType);
        }
    }

    [Fact]
    public async Task CompAgent_SystemActions_MatchesHotspot()
    {
        var agent = new CompAgent();
        var result = await agent.TryMatchAsync("热点");
        Assert.NotNull(result);
        // ToolRegistry might match first (toolId) or SystemActions (url)
        Assert.True(result.ActionParams.ContainsKey("url") || result.ActionParams.ContainsKey("toolId"),
            "Expected either 'url' (SystemActions) or 'toolId' (ToolRegistry) match");
    }

    [Fact]
    public async Task CompAgent_SystemActions_MatchesTaskManager()
    {
        var agent = new CompAgent();
        var result = await agent.TryMatchAsync("任务管理器");
        Assert.NotNull(result);
        Assert.Equal("taskmgr", result.ActionParams["path"]);
        Assert.Equal(SkillActionType.run_program, result.ActionType);
    }

    [Fact]
    public async Task CompAgent_SystemActions_MatchesWallpaper()
    {
        var agent = new CompAgent();
        var result = await agent.TryMatchAsync("壁纸");
        Assert.NotNull(result);
        Assert.Contains("personalization-background", result.ActionParams["url"]);
    }

    [Fact]
    public async Task CompAgent_SystemActions_MatchesSound()
    {
        var agent = new CompAgent();
        var result = await agent.TryMatchAsync("声音");
        Assert.NotNull(result);
        Assert.Contains("ms-settings:sound", result.ActionParams["url"]);
    }

    [Fact]
    public async Task CompAgent_SystemActions_MatchesBluetooth()
    {
        var agent = new CompAgent();
        var result = await agent.TryMatchAsync("蓝牙");
        Assert.NotNull(result);
        Assert.Contains("ms-settings:bluetooth", result.ActionParams["url"]);
    }

    [Fact]
    public async Task CompAgent_ToolRegistry_NotEmpty()
    {
        // Just verify tools are registered
        var tools = WinAssistant.Controls.Tools.ToolRegistry.All;
        Assert.NotEmpty(tools);
    }

    [Fact]
    public async Task FileAgent_CleanupKeywords_TriggersDiskClean()
    {
        var agent = new FileAgent();
        var result = await agent.TryMatchAsync("清理C盘垃圾");
        Assert.NotNull(result);
        Assert.True(result.ActionParams.ContainsKey("script"));
        Assert.Contains("cleanmgr", result.ActionParams["script"].ToLower());
    }

    [Fact]
    public async Task FileAgent_CleanupTempFiles_TriggersDiskClean()
    {
        var agent = new FileAgent();
        var result = await agent.TryMatchAsync("清理临时文件");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FileAgent_DeleteKeyword_ShowsMessage()
    {
        var agent = new FileAgent();
        var result = await agent.TryMatchAsync("删除文件");
        Assert.NotNull(result);
        Assert.Equal(SkillActionType.show_message, result.ActionType);
    }

    [Fact]
    public async Task FileAgent_NonMatchingInput_ReturnsNull()
    {
        var agent = new FileAgent();
        var result = await agent.TryMatchAsync("复制文件到桌面");
        Assert.Null(result);
    }

    [Fact]
    public async Task CompAgent_NonMatchingInput_ReturnsNull()
    {
        var agent = new CompAgent();
        var result = await agent.TryMatchAsync("外星人abc");
        Assert.Null(result);
    }
}
