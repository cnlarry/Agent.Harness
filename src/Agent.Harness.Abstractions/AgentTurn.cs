namespace Agent.Harness.Abstractions;

/// <summary>一次回合中的消息角色。</summary>
public enum AgentRole
{
    System = 3,
    User = 1,
    Assistant = 2,
    Tool = 4,
}

/// <summary>进入引擎的一条消息。工具轮的消息只在单回合内存上下文中存在。</summary>
public sealed record AgentMessage(
    AgentRole Role,
    string Content,
    IReadOnlyList<CompletedToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>模型已经产出、等待回喂或执行的完整工具调用。</summary>
public sealed record CompletedToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>当前页面处境。只作定位，不作授权依据。</summary>
public sealed record AgentPageContext(
    int? ModuleId = null,
    string? PageType = null,
    string? DocumentNumber = null,
    IReadOnlyList<string>? Selection = null,
    IReadOnlyDictionary<string, string>? Filters = null);

/// <summary>一次 Agent 回合的输入。历史、系统提示与预算策略都由宿主组装。</summary>
public sealed record AgentTurnRequest(
    string TenantId,
    string UserId,
    string ConversationId,
    string Input,
    IReadOnlyList<AgentMessage> History,
    string SystemPrompt,
    string CorrelationId,
    AgentPageContext? PageContext = null,
    int MaxToolRounds = 4);

/// <summary>一次回合的用量。</summary>
public sealed record AgentUsage(int PromptTokens = 0, int CompletionTokens = 0, int ElapsedMs = 0)
{
    public static AgentUsage Empty { get; } = new();

    public int TotalTokens => PromptTokens + CompletionTokens;

    public AgentUsage Add(AgentUsage other) => new(
        PromptTokens + other.PromptTokens,
        CompletionTokens + other.CompletionTokens,
        ElapsedMs + other.ElapsedMs);
}

/// <summary>一次 Agent 回合的最终结果。</summary>
public sealed record AgentTurnResult(
    string Text,
    string ModelName,
    AgentUsage Usage,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<object> Drafts,
    string? FinishReason);

/// <summary>稳定的结束原因；宿主要能区分正常完成与达到工具轮次上限。</summary>
public static class AgentFinishReasons
{
    public const string Stop = "stop";

    public const string ToolCalls = "tool_calls";

    public const string MaxToolRounds = "max_tool_rounds";
}
