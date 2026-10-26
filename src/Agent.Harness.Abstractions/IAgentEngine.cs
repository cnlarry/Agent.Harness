namespace Agent.Harness.Abstractions;

/// <summary>
/// Agent 执行引擎。实现只负责“把一轮对话跑完并产出事件”；
/// 权限、提示词、落库、预算与审计由宿主/管线负责。
/// </summary>
public interface IAgentEngine
{
    IAsyncEnumerable<AgentEvent> StreamAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken = default);
}
