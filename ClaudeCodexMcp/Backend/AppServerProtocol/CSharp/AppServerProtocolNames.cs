namespace ClaudeCodexMcp.Backend.AppServerProtocol.CSharp;

public static class AppServerProtocolNames
{
    public const string Initialize = "initialize";
    public const string ThreadStart = "thread/start";
    public const string TurnStart = "turn/start";
    public const string TurnSteer = "turn/steer";
    public const string TurnInterrupt = "turn/interrupt";
    public const string ThreadRead = "thread/read";
    public const string ThreadTurnsList = "thread/turns/list";
    public const string ThreadList = "thread/list";
    public const string ThreadLoadedList = "thread/loaded/list";
    public const string ThreadResume = "thread/resume";
    public const string ThreadUnsubscribe = "thread/unsubscribe";
    public const string SkillsList = "skills/list";
    public const string PluginList = "plugin/list";
    public const string PluginRead = "plugin/read";
    public const string ModelList = "model/list";
    public const string AccountRead = "account/read";
    public const string AccountRateLimitsRead = "account/rateLimits/read";

    public const string ThreadStarted = "thread/started";
    public const string ThreadStatusChanged = "thread/status/changed";
    public const string TurnStarted = "turn/started";
    public const string TurnCompleted = "turn/completed";
    public const string TurnDiffUpdated = "turn/diff/updated";
    public const string TurnPlanUpdated = "turn/plan/updated";
    public const string ItemStarted = "item/started";
    public const string ItemCompleted = "item/completed";
    public const string ItemAgentMessageDelta = "item/agentMessage/delta";
    public const string ThreadTokenUsageUpdated = "thread/tokenUsage/updated";
    public const string AccountRateLimitsUpdated = "account/rateLimits/updated";
    public const string Error = "error";
    public const string Warning = "warning";

    public static readonly string[] ApprovedMvpMethods =
    [
        Initialize,
        ThreadStart,
        TurnStart,
        TurnSteer,
        TurnInterrupt,
        ThreadRead,
        ThreadTurnsList,
        ThreadList,
        ThreadLoadedList,
        ThreadResume,
        ThreadUnsubscribe,
        SkillsList,
        PluginList,
        PluginRead,
        ModelList,
        AccountRead,
        AccountRateLimitsRead
    ];

    public static readonly string[] ApprovedMvpNotifications =
    [
        ThreadStarted,
        ThreadStatusChanged,
        TurnStarted,
        TurnCompleted,
        TurnDiffUpdated,
        TurnPlanUpdated,
        ItemStarted,
        ItemCompleted,
        ItemAgentMessageDelta,
        ThreadTokenUsageUpdated,
        AccountRateLimitsUpdated,
        Error,
        Warning
    ];
}
