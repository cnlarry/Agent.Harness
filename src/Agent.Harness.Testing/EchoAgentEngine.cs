using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>不依赖模型的最小引擎：用于跑通管线、SSE 与宿主接线。</summary>
public sealed class EchoAgentEngine : IAgentEngine
{
    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = $"Echo: {request.Input}";
        yield return new AgentEvent.TextDelta(text);
        await Task.Yield();

        yield return new AgentEvent.Completed(new AgentTurnResult(
            text,
            "echo-model",
            AgentUsage.Empty,
            [],
            [],
            "stop"));
    }
}
