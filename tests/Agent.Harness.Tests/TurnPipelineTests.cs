using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Testing;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class TurnPipelineTests
{
    [Fact]
    public async Task StreamAsync_ReservesAndSettlesBudget()
    {
        var budget = new InMemoryTurnBudget();
        var audit = new InMemoryAuditSink();
        var pipeline = new TurnPipeline(new EchoAgentEngine(), budget, audit);
        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hello",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in pipeline.StreamAsync(request))
        {
            events.Add(evt);
        }

        Assert.Contains(events, evt => evt is AgentEvent.TextDelta);
        Assert.Contains(events, evt => evt is AgentEvent.Completed);
        Assert.Single(budget.Reservations);
        Assert.Single(budget.Settlements);
        Assert.True(budget.Settlements[0].Completed);
        Assert.Equal(2, audit.Events.Count);
    }

    [Fact]
    public async Task FakeChatModel_ReturnsConfiguredTurns()
    {
        var model = FakeChatModel.Text("hello");

        var deltas = new List<ChatDelta>();
        await foreach (var delta in model.StreamAsync([], null))
        {
            deltas.Add(delta);
        }

        Assert.Single(deltas);
        Assert.Equal("hello", deltas[0].ContentDelta);
    }

    [Fact]
    public async Task StreamAsync_WhenEngineThrows_EmitsFailedAndSettlesIncomplete()
    {
        var budget = new InMemoryTurnBudget();
        var audit = new InMemoryAuditSink();
        var pipeline = new TurnPipeline(new FailingEngine(), budget, audit);
        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hello",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in pipeline.StreamAsync(request))
        {
            events.Add(evt);
        }

        Assert.Contains(events, evt => evt is AgentEvent.TextDelta);
        var failed = Assert.Single(events.OfType<AgentEvent.Failed>());
        Assert.Equal("rate_limited", failed.Code);
        Assert.Equal("模型请求过于频繁，请稍后重试。", failed.Message);
        Assert.Single(budget.Settlements);
        Assert.False(budget.Settlements[0].Completed);
        Assert.Equal(2, audit.Events.Count);
        Assert.False(audit.Events[1].Success);
    }

    [Fact]
    public async Task StreamAsync_WhenFinishReasonIsMaxToolRounds_MarksIncomplete()
    {
        var budget = new InMemoryTurnBudget();
        var audit = new InMemoryAuditSink();
        var pipeline = new TurnPipeline(new MaxRoundsEngine(), budget, audit);
        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hello",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in pipeline.StreamAsync(request))
        {
            events.Add(evt);
        }

        var completed = Assert.Single(events.OfType<AgentEvent.Completed>());
        Assert.Equal(AgentFinishReasons.MaxToolRounds, completed.Result.FinishReason);
        Assert.Single(budget.Settlements);
        Assert.False(budget.Settlements[0].Completed);
        Assert.Equal(2, audit.Events.Count);
        Assert.False(audit.Events[1].Success);
    }

    [Fact]
    public async Task StreamAsync_WhenEngineTimesOut_EmitsTimeoutFailure()
    {
        var budget = new InMemoryTurnBudget();
        var audit = new InMemoryAuditSink();
        var pipeline = new TurnPipeline(new TimeoutEngine(), budget, audit);
        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hello",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in pipeline.StreamAsync(request))
        {
            events.Add(evt);
        }

        var failed = Assert.Single(events.OfType<AgentEvent.Failed>());
        Assert.Equal("timeout", failed.Code);
        Assert.Single(budget.Settlements);
        Assert.False(budget.Settlements[0].Completed);
    }

    [Fact]
    public async Task StreamAsync_WhenUnknownException_DoesNotLeakMessage()
    {
        var budget = new InMemoryTurnBudget();
        var audit = new InMemoryAuditSink();
        var pipeline = new TurnPipeline(new LeakyEngine(), budget, audit);
        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hello",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in pipeline.StreamAsync(request))
        {
            events.Add(evt);
        }

        var failed = Assert.Single(events.OfType<AgentEvent.Failed>());
        Assert.Equal("agent_error", failed.Code);
        Assert.DoesNotContain("sk-live-secret", failed.Message, StringComparison.Ordinal);
    }

    private sealed class FailingEngine : IAgentEngine
    {
        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentEvent.TextDelta("partial");
            await Task.Yield();
            throw new ChatModelException(
                "rate_limited",
                "模型请求过于频繁，请稍后重试。",
                isTransient: true);
        }
    }

    private sealed class TimeoutEngine : IAgentEngine
    {
        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentEvent.TextDelta("partial");
            await Task.Yield();
            throw new TaskCanceledException("simulated engine timeout");
        }
    }

    private sealed class LeakyEngine : IAgentEngine
    {
        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentEvent.TextDelta("partial");
            await Task.Yield();
            throw new InvalidOperationException("sk-live-secret");
        }
    }

    private sealed class MaxRoundsEngine : IAgentEngine
    {
        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AgentEvent.Completed(new AgentTurnResult(
                Text: string.Empty,
                ModelName: "test-model",
                Usage: AgentUsage.Empty,
                ToolCalls: [],
                Drafts: [],
                FinishReason: AgentFinishReasons.MaxToolRounds));
        }
    }
}
