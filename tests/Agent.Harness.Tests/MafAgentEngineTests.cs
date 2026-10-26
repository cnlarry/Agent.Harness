using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Agent.Harness.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class MafAgentEngineTests
{
    [Fact]
    public async Task StreamAsync_EmitsTextAndCompletion_WithFakeModel()
    {
        var engine = new MafAgentEngine(
            FakeChatModel.Text("hello"),
            new ToolRegistry([]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance);

        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hi",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in engine.StreamAsync(request))
        {
            events.Add(evt);
        }

        Assert.Equal("hello", events.OfType<AgentEvent.TextDelta>().Single().Text);
        Assert.Equal("hello", events.OfType<AgentEvent.Completed>().Single().Result.Text);
    }

    [Fact]
    public async Task StreamAsync_ExecutesTool_WithFakeModel()
    {
        var tool = new EchoTool();
        var model = new FakeChatModel(
            [
                new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        new ProposedToolCallFragment(
                            Index: 0,
                            Id: "call_1",
                            Name: "echo",
                            ArgumentsFragment: """{"text":"hi"}"""),
                    ],
                    FinishReason: "tool_calls"),
            ],
            [
                new ChatDelta(
                    ContentDelta: "done",
                    Usage: null,
                    FinishReason: "stop"),
            ]);

        var engine = new MafAgentEngine(
            model,
            new ToolRegistry([tool]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance);

        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hi",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in engine.StreamAsync(request))
        {
            events.Add(evt);
        }

        Assert.Equal(1, tool.Calls);
        Assert.Single(events.OfType<AgentEvent.ToolStarted>());
        Assert.Single(events.OfType<AgentEvent.ToolFinished>());
        Assert.Equal("done", events.OfType<AgentEvent.Completed>().Single().Result.Text);
        Assert.Single(events.OfType<AgentEvent.Completed>().Single().Result.ToolCalls);
    }

    [Fact]
    public async Task StreamAsync_ExecutesMultipleToolCallsInOneTurn()
    {
        var tool = new EchoTool();
        var model = new FakeChatModel(
            [
                new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        new ProposedToolCallFragment(
                            Index: 0,
                            Id: "call_1",
                            Name: "echo",
                            ArgumentsFragment: """{"text":"one"}"""),
                        new ProposedToolCallFragment(
                            Index: 1,
                            Id: "call_2",
                            Name: "echo",
                            ArgumentsFragment: """{"text":"two"}"""),
                    ],
                    FinishReason: "tool_calls"),
            ],
            [
                new ChatDelta(
                    ContentDelta: "done",
                    Usage: null,
                    FinishReason: "stop"),
            ]);

        var engine = new MafAgentEngine(
            model,
            new ToolRegistry([tool]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance);

        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hi",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in engine.StreamAsync(request))
        {
            events.Add(evt);
        }

        Assert.Equal(2, tool.Calls);
        Assert.Equal(2, events.OfType<AgentEvent.ToolStarted>().Count());
        Assert.Equal(2, events.OfType<AgentEvent.ToolFinished>().Count());

        var completed = events.OfType<AgentEvent.Completed>().Single();
        Assert.Equal("done", completed.Result.Text);
        Assert.Equal("stop", completed.Result.FinishReason);
        Assert.Equal(2, completed.Result.ToolCalls.Count);
    }

    [Fact]
    public async Task StreamAsync_ClosesToolCallThatExceedsMaxRounds()
    {
        var tool = new EchoTool();
        var engine = new MafAgentEngine(
            new LoopingToolCallModel(),
            new ToolRegistry([tool]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance);

        var request = new AgentTurnRequest(
            TenantId: "tenant",
            UserId: "user",
            ConversationId: "conversation",
            Input: "hi",
            History: [],
            SystemPrompt: "system",
            CorrelationId: "correlation",
            MaxToolRounds: 1);

        var events = new List<AgentEvent>();
        await foreach (var evt in engine.StreamAsync(request))
        {
            events.Add(evt);
        }

        var started = events.OfType<AgentEvent.ToolStarted>().Count();
        var finished = events.OfType<AgentEvent.ToolFinished>().ToArray();
        var completed = events.OfType<AgentEvent.Completed>().Single();

        Assert.Equal(started, finished.Length);
        Assert.Contains(finished, result => !result.Ok &&
                                            result.Digest.Contains("最大工具轮次", StringComparison.Ordinal));
        Assert.Equal(AgentFinishReasons.MaxToolRounds, completed.Result.FinishReason);
        Assert.Empty(completed.Result.Text);
        Assert.Equal(started, completed.Result.ToolCalls.Count);
    }

    [Fact]
    public async Task StreamAsync_DeniedTool_ReportsNotOk()
    {
        var tool = new EchoTool();
        var model = new FakeChatModel(
            [
                new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        new ProposedToolCallFragment(0, "call_1", "echo", """{"text":"hi"}"""),
                    ],
                    FinishReason: "tool_calls"),
            ],
            [new ChatDelta("denied", null, FinishReason: "stop")]);

        var engine = new MafAgentEngine(
            model,
            new ToolRegistry([tool]),
            new DenyAllToolPolicy(),
            NullLoggerFactory.Instance);

        var request = new AgentTurnRequest("tenant", "user", "conversation", "hi", [], "system", "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in engine.StreamAsync(request))
        {
            events.Add(evt);
        }

        var finished = Assert.Single(events.OfType<AgentEvent.ToolFinished>());
        Assert.False(finished.Ok);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task StreamAsync_CapturesDraftFromToolResult()
    {
        var tool = new DraftTool();
        var model = new FakeChatModel(
            [
                new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        new ProposedToolCallFragment(0, "call_1", "draft", "{}"),
                    ],
                    FinishReason: "tool_calls"),
            ],
            [new ChatDelta("drafted", null, FinishReason: "stop")]);

        var engine = new MafAgentEngine(
            model,
            new ToolRegistry([tool]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance);

        var request = new AgentTurnRequest("tenant", "user", "conversation", "hi", [], "system", "correlation");

        var events = new List<AgentEvent>();
        await foreach (var evt in engine.StreamAsync(request))
        {
            events.Add(evt);
        }

        var completed = Assert.Single(events.OfType<AgentEvent.Completed>());
        Assert.Single(completed.Result.Drafts);
    }

    private sealed class EchoTool : ITool
    {
        public int Calls { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "echo",
            "echo",
            """{"type":"object","properties":{"text":{"type":"string"}}}""",
            ToolRisk.Read);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ToolResult.Success("ok"));
        }
    }

    private sealed class LoopingToolCallModel : IChatModel
    {
        private int _calls;

        public string ModelName => "looping-tool-call";

        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            yield return new ChatDelta(
                ContentDelta: null,
                Usage: new ChatUsage(1, 1, 0),
                ToolCallDeltas:
                [
                    new ProposedToolCallFragment(
                        Index: 0,
                        Id: $"call_{_calls}",
                        Name: "echo",
                        ArgumentsFragment: """{"text":"hi"}"""),
                ],
                FinishReason: "tool_calls");
        }
    }

    private sealed class DenyAllToolPolicy : IToolPolicy
    {
        public ValueTask<ToolDecision> EvaluateAsync(
            ToolDescriptor tool,
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ToolDecision>(
                new ToolDecision.Deny("forbidden", "no access"));
    }

    private sealed class DraftTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "draft",
            "draft",
            """{"type":"object","properties":{}}""",
            ToolRisk.Draft,
            ReadOnly: false);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ToolResult(
                Ok: true,
                ContentForModel: "draft ok",
                Draft: new { draftId = "D1" }));
    }
}
