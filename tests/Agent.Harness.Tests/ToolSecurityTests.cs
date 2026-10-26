using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Agent.Harness.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ToolSecurityTests
{
    [Fact]
    public async Task DeniedTool_DoesNotInvokeOrLeakSensitiveData()
    {
        var tool = new SecretTool();
        var run = await RunAsync(tool, new DenyAllPolicy(), Request());

        Assert.Equal(0, tool.Calls);
        var finished = Assert.Single(run.ToolFinished);
        Assert.False(finished.Ok);
        AssertNoSecret(run, SecretTool.Secret);
    }

    [Fact]
    public async Task ToolInvocation_UsesRequestIdentity()
    {
        var tool = new CapturingTool();
        var run = await RunAsync(tool, new AllowAllToolPolicy(), Request());

        var invocation = Assert.IsType<ToolInvocation>(tool.Invocation);
        Assert.Equal("tenant-a", invocation.TenantId);
        Assert.Equal("user-a", invocation.UserId);
        Assert.Equal("conversation-a", invocation.ConversationId);
        Assert.False(string.IsNullOrWhiteSpace(invocation.CallId));
        Assert.Contains(run.ToolFinished, result => result.Ok);
    }

    [Fact]
    public async Task ToolReauthorization_DeniesNonOwnerWithoutLeaking()
    {
        var tool = new OwnerOnlyTool();
        var run = await RunAsync(tool, new AllowAllToolPolicy(), Request("intruder"));

        Assert.Equal(1, tool.Calls);
        var finished = Assert.Single(run.ToolFinished);
        Assert.False(finished.Ok);
        AssertNoSecret(run, OwnerOnlyTool.Secret);
    }

    [Fact]
    public async Task ToolReauthorization_AllowsOwner()
    {
        var tool = new OwnerOnlyTool();
        var run = await RunAsync(tool, new AllowAllToolPolicy(), Request("owner"));

        var finished = Assert.Single(run.ToolFinished);
        Assert.True(finished.Ok);
        Assert.Contains(OwnerOnlyTool.Secret, finished.Digest, StringComparison.Ordinal);
    }

    private static async Task<TurnRun> RunAsync(
        ITool tool,
        IToolPolicy policy,
        AgentTurnRequest request)
    {
        var model = new FakeChatModel(
            [
                new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        new ProposedToolCallFragment(
                            Index: 0,
                            Id: "call-1",
                            Name: tool.Descriptor.Name,
                            ArgumentsFragment: "{}"),
                    ],
                    FinishReason: "tool_calls"),
            ],
            [new ChatDelta("done", null, FinishReason: "stop")]);

        var engine = new MafAgentEngine(
            model,
            new ToolRegistry([tool]),
            policy,
            NullLoggerFactory.Instance);

        return await TurnRun.CollectAsync(engine, request);
    }

    private static AgentTurnRequest Request(string userId = "user-a") => new(
        TenantId: "tenant-a",
        UserId: userId,
        ConversationId: "conversation-a",
        Input: "hi",
        History: [],
        SystemPrompt: "system",
        CorrelationId: "correlation");

    private static void AssertNoSecret(TurnRun run, string secret)
    {
        Assert.DoesNotContain(
            run.TextDeltas,
            delta => delta.Text.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(
            run.ToolFinished,
            result => result.Digest.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(
            run.Events.OfType<AgentEvent.Completed>(),
            completed => completed.Result.Text.Contains(secret, StringComparison.Ordinal));

        if (run.Completed is { } completed)
        {
            Assert.DoesNotContain(
                completed.Result.ToolCalls,
                call => call.ResultDigest.Contains(secret, StringComparison.Ordinal));
        }
    }

    private sealed class SecretTool : ITool
    {
        public const string Secret = "SECRET-42";

        public int Calls { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "secret",
            "读取一条敏感数据。",
            """{"type":"object","properties":{}}""",
            ToolRisk.Read);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(ToolResult.Success(Secret));
        }
    }

    private sealed class CapturingTool : ITool
    {
        public ToolInvocation? Invocation { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "capture",
            "捕获调用身份。",
            """{"type":"object","properties":{}}""",
            ToolRisk.Read);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocation = invocation;
            return ValueTask.FromResult(ToolResult.Success("captured"));
        }
    }

    private sealed class OwnerOnlyTool : ITool
    {
        public const string Secret = "OWNER-SECRET";

        public int Calls { get; private set; }

        public ToolDescriptor Descriptor { get; } = new(
            "owner_only",
            "只允许 owner 读取。",
            """{"type":"object","properties":{}}""",
            ToolRisk.Read);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(invocation.UserId == "owner"
                ? ToolResult.Success(Secret)
                : ToolResult.Deny("forbidden"));
        }
    }
}
