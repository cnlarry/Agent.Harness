namespace Agent.Harness.Abstractions;

/// <summary>引擎对外产出的稳定事件流。宿主可映射为 SSE、SignalR 或控制台输出。</summary>
public abstract record AgentEvent
{
    public sealed record TextDelta(string Text) : AgentEvent;

    public sealed record ReasoningDelta(string Text) : AgentEvent;

    public sealed record ToolStarted(string CallId, string Name, ToolRisk Risk) : AgentEvent;

    public sealed record ToolFinished(string CallId, string Name, string Digest, bool Ok) : AgentEvent;

    public sealed record Completed(AgentTurnResult Result) : AgentEvent;

    public sealed record Failed(string Code, string Message) : AgentEvent;
}
