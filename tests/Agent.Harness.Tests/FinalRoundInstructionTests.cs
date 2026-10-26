using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Harness.Tests;

/// <summary>
/// 收尾轮说明：达到工具轮上限的那一轮追加一条 system 说明并撤掉工具声明。
/// 缺了它，模型会把"还想再查一次"写成正文里的调用标记，整轮回答作废。
/// </summary>
public sealed class FinalRoundInstructionTests
{
    private const string Instruction = "本轮是最后一步：不能再调用任何工具，请直接用文字给出结论。";

    private static MafAgentEngine CreateEngine(
        ScriptedChatModel model,
        string? instruction,
        int maxEmptyResponseRetries = 0) =>
        new(
            model,
            new ToolRegistry([new EchoTool()]),
            new AllowAllToolPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions
            {
                MaxEmptyResponseRetries = maxEmptyResponseRetries,
                FinalRoundInstruction = instruction,
            });

    private static AgentTurnRequest Request(int maxToolRounds) => new(
        TenantId: "tenant",
        UserId: "user",
        ConversationId: "conversation",
        Input: "hi",
        History: [],
        SystemPrompt: "system",
        CorrelationId: "correlation",
        MaxToolRounds: maxToolRounds);

    [Fact]
    public async Task 收尾轮追加说明并撤掉工具声明()
    {
        // 2 轮工具 + 第 3 次模型调用收尾。
        var model = new ScriptedChatModel(toolRounds: 2);
        var engine = CreateEngine(model, Instruction);

        await foreach (var _ in engine.StreamAsync(Request(maxToolRounds: 2)))
        {
        }

        Assert.Equal(3, model.Calls.Count);

        // 前两轮：有工具声明，没有追加说明。
        for (var index = 0; index < 2; index++)
        {
            Assert.NotNull(model.Calls[index].Tools);
            Assert.DoesNotContain(
                model.Calls[index].Messages,
                message => message.Role == ChatRole.System && message.Content == Instruction);
        }

        // 收尾轮：撤掉工具声明，最后一条是 system 说明。
        var final = model.Calls[2];
        Assert.Null(final.Tools);
        Assert.Equal(ChatRole.System, final.Messages[^1].Role);
        Assert.Equal(Instruction, final.Messages[^1].Content);
    }

    [Fact]
    public async Task 不配置说明时不追加任何消息()
    {
        var model = new ScriptedChatModel(toolRounds: 2);
        var engine = CreateEngine(model, instruction: null);

        await foreach (var _ in engine.StreamAsync(Request(maxToolRounds: 2)))
        {
        }

        Assert.Equal(3, model.Calls.Count);
        // 系统提示词本身是一条 system 消息（由适配层从 ChatOptions.Instructions 转来），
        // 这里只断言**收尾轮说明**没被追加。
        Assert.DoesNotContain(
            model.Calls.SelectMany(call => call.Messages),
            message => message.Content == Instruction);
    }

    [Fact]
    public async Task 空正文重试不会被算成新的一轮()
    {
        // 第 2 轮先吐空正文触发重试，再吐工具调用：重试若被算成新一轮，
        // 收尾轮就会提前到第 3 次调用，模型拿不到工具却已经被要求"别再调工具"。
        var model = new ScriptedChatModel(toolRounds: 2, emptyBeforeSecondRound: true);
        var engine = CreateEngine(model, Instruction, maxEmptyResponseRetries: 1);

        await foreach (var _ in engine.StreamAsync(Request(maxToolRounds: 2)))
        {
        }

        // 4 次底层调用 = 第 2 轮的 1 次空响应 + 重试；收尾轮仍是最后 1 次。
        Assert.Equal(4, model.Calls.Count);
        Assert.Null(model.Calls[^1].Tools);
        Assert.Equal(Instruction, model.Calls[^1].Messages[^1].Content);
        Assert.NotNull(model.Calls[^2].Tools);
    }

    /// <summary>
    /// 按**逻辑轮**脚本化：前 <c>toolRounds</c> 轮吐工具调用，之后吐正文。
    /// 空响应的重试不算新的一轮（底层调用次数会多一次，逻辑轮不变）——这正是要验证的点。
    /// </summary>
    private sealed class ScriptedChatModel(int toolRounds, bool emptyBeforeSecondRound = false) : IChatModel
    {
        private int _logicalRounds;
        private bool _emptyEmitted;

        public List<(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition>? Tools)> Calls { get; } = [];

        public string ModelName => "scripted";

        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add((messages, tools));
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            if (emptyBeforeSecondRound && !_emptyEmitted && _logicalRounds == 1)
            {
                _emptyEmitted = true;
                yield return new ChatDelta(ContentDelta: null, Usage: null, FinishReason: "stop");
                yield break;
            }

            _logicalRounds++;

            if (_logicalRounds <= toolRounds)
            {
                yield return new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        new ProposedToolCallFragment(
                            Index: 0,
                            Id: $"call_{_logicalRounds}",
                            Name: "echo",
                            ArgumentsFragment: """{"text":"hi"}"""),
                    ],
                    FinishReason: "tool_calls");
                yield break;
            }

            yield return new ChatDelta("好的。", null);
        }
    }

    private sealed class EchoTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(
            "echo",
            "echo",
            """{"type":"object","properties":{"text":{"type":"string"}}}""",
            ToolRisk.Read);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.Success("ok"));
    }

    private sealed class AllowAllToolPolicy : IToolPolicy
    {
        public ValueTask<ToolDecision> EvaluateAsync(
            ToolDescriptor tool,
            ToolInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ToolDecision>(new ToolDecision.Allow());
    }
}
