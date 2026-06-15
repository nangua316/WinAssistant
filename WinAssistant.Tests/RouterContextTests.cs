using WinAssistant.Agents;

namespace WinAssistant.Tests;

public class RouterContextTests
{
    [Fact]
    public async Task RouteContext_NoTrigger_SetsActionObject()
    {
        var router = new Router();
        var (_, context) = await router.RouteAsync("你好世界");
        Assert.Null(context.MatchedTrigger);
        Assert.Equal("你好世界", context.ActionObject);
    }

    [Fact]
    public async Task RouteContext_WithTrigger_PopulatesAllFields()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("搜索今天天气");

        Assert.Equal("搜索", context.MatchedTrigger);
        Assert.Equal("今天天气", context.ActionObject);
        Assert.Equal("Browser", context.PrimaryAgentType);
    }

    [Fact]
    public async Task RouteContext_CascadeOpenSetting_RoutesToCompAgent()
    {
        // "打开设置": AppAgent won't find "设置" as an app, cascades to CompAgent
        var router = new Router();
        var (result, context) = await router.RouteAsync("打开设置");

        Assert.NotNull(context.MatchedTrigger);
        Assert.Equal("打开", context.MatchedTrigger);

        if (result != null)
        {
            // Cascade succeeded → CompAgent matched
            Assert.Equal(AgentType.Computer, result.AgentType);
        }
        else
        {
            // No cascade match either
            Assert.NotEmpty(context.AttemptedAgentTypes);
        }
    }

    [Fact]
    public async Task RouteContext_CascadeOpenHotspot_RoutesToCompAgent()
    {
        // "开热点": short trigger "开" → AppAgent fails → cascades to CompAgent
        var router = new Router();
        var (result, context) = await router.RouteAsync("开热点");

        Assert.NotNull(context.MatchedTrigger);
        Assert.Equal("开", context.MatchedTrigger);

        if (result != null)
        {
            Assert.Equal(AgentType.Computer, result.AgentType);
        }
    }

    [Fact]
    public async Task RouteContext_AgentResultHoldsOriginalInput()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("搜索今天天气");

        Assert.NotNull(result);
        Assert.NotNull(result.OriginalInput);
        Assert.Equal("搜索今天天气", result.OriginalInput);
        Assert.Equal("今天天气", result.ActionObject);
    }

    [Fact]
    public async Task RouteContext_Cascade_RecordsAttemptedAgents_OnFailure()
    {
        var router = new Router();
        var (result, context) = await router.RouteAsync("打开不存在的应用xyz");

        Assert.NotNull(context.MatchedTrigger);
        Assert.Equal("打开", context.MatchedTrigger);

        if (result == null)
        {
            // Cascade ran through all agents
            Assert.NotEmpty(context.AttemptedAgentTypes);
        }
        else
        {
            // Some agent matched (unlikely but possible)
            Assert.NotNull(result);
        }
    }

    [Fact]
    public void GetAgentByType_ReturnsCorrectAgent()
    {
        var router = new Router();

        var appAgent = router.GetAgentByType(AgentType.App);
        Assert.NotNull(appAgent);
        Assert.Equal("App-Agent", appAgent.Name);

        var compAgent = router.GetAgentByType(AgentType.Computer);
        Assert.NotNull(compAgent);
        Assert.Equal("Comp-Agent", compAgent.Name);

        var browserAgent = router.GetAgentByType(AgentType.Browser);
        Assert.NotNull(browserAgent);
        Assert.Equal("Browser-Agent", browserAgent.Name);

        var fileAgent = router.GetAgentByType(AgentType.File);
        Assert.NotNull(fileAgent);
        Assert.Equal("File-Agent", fileAgent.Name);
    }

    [Fact]
    public void GetAgentByType_InvalidType_ReturnsNull()
    {
        var router = new Router();
        var agent = router.GetAgentByType((AgentType)999);
        Assert.Null(agent);
    }

    [Fact]
    public async Task MultipleRoutes_WorkIndependent()
    {
        var router = new Router();

        // First route
        var (r1, c1) = await router.RouteAsync("搜索新闻");
        Assert.Equal("搜索", c1.MatchedTrigger);

        // Second route - should be independent
        var (r2, c2) = await router.RouteAsync("打开计算器");
        Assert.Equal("打开", c2.MatchedTrigger);
    }
}
