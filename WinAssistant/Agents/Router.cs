using WinAssistant.Services;

namespace WinAssistant.Agents;

/// <summary>
/// Routes user input to the appropriate Agent based on trigger words.
/// Uses longest-trigger-first matching. If the primary agent returns null,
/// cascades to all other agents regardless of trigger length.
/// </summary>
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

    /// <summary>
    /// Route user input to an agent.
    /// 1. Find the longest matching trigger across all agents
    /// 2. Try the owning agent with the extracted actionObject
    /// 3. If the trigger is short (&lt;= 1 char) and the primary agent fails, cascade to other agents
    /// </summary>
    public async Task<(AgentMatchResult? Result, AgentRouteContext Context)> RouteAsync(string input)
    {
        var context = new AgentRouteContext
        {
            OriginalInput = input
        };

        if (string.IsNullOrWhiteSpace(input))
            return (null, context);

        // 1. Collect all (trigger, agent) pairs sorted by trigger length descending
        var pairs = _agents
            .SelectMany(a => a.Triggers.Select(t => (trigger: t, agent: a)))
            .OrderByDescending(p => p.trigger.Length)
            .ToList();

        string? matchedTrigger = null;
        IAgent? matchedAgent = null;

        // 2. Find the longest trigger present in the input
        foreach (var (trigger, agent) in pairs)
        {
            if (input.IndexOf(trigger, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matchedTrigger = trigger;
                matchedAgent = agent;
                break;
            }
        }

        if (matchedTrigger == null || matchedAgent == null)
        {
            context.ActionObject = input;
            return (null, context);
        }

        // 3. Extract actionObject (text after the trigger)
        var idx = input.IndexOf(matchedTrigger, StringComparison.OrdinalIgnoreCase);
        var actionObject = input[(idx + matchedTrigger.Length)..].Trim();

        context.MatchedTrigger = matchedTrigger;
        context.ActionObject = actionObject;
        context.PrimaryAgentType = matchedAgent.Type.ToString();

        // 4. Try the owning agent
        var result = await matchedAgent.TryMatchAsync(actionObject);
        System.IO.File.AppendAllText(@"C:\Users\likan\AppData\Local\Temp\winasst_debug.log",
            $"[{DateTime.Now:HH:mm:ss.fff}] Router: matched trigger '{matchedTrigger}' for agent '{matchedAgent.Name}', actionObject='{actionObject}', result={(result != null ? "HIT" : "MISS")}{Environment.NewLine}");
        if (result != null)
        {
            result.OriginalInput = input;
            result.ActionObject = actionObject;
            return (result, context);
        }

        context.AttemptedAgentTypes.Add(matchedAgent.Type.ToString());

        // 5. If primary agent returns null, cascade to all other agents
        //    (e.g. "打开设置" → AppAgent 找不到"设置" → CompAgent 命中"设置")
        foreach (var other in _agents.Where(a => a.Type != matchedAgent.Type))
        {
            result = await other.TryMatchAsync(actionObject);
            if (result != null)
            {
                result.OriginalInput = input;
                result.ActionObject = actionObject;
                return (result, context);
            }
            context.AttemptedAgentTypes.Add(other.Type.ToString());
        }

        return (null, context);
    }

    /// <summary>
    /// Get an agent by its AgentType.
    /// </summary>
    public IAgent? GetAgentByType(AgentType type) =>
        _agents.FirstOrDefault(a => a.Type == type);
}

public class AgentRouteContext
{
    public string OriginalInput { get; set; } = string.Empty;
    public string ActionObject { get; set; } = string.Empty;
    public string? MatchedTrigger { get; set; }
    public string? PrimaryAgentType { get; set; }
    public List<string> AttemptedAgentTypes { get; set; } = [];
}
