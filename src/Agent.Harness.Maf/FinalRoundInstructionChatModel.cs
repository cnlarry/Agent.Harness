using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Maf;

/// <summary>
/// 在**最后一次**模型调用前追加一条 system 说明，并撤掉工具声明。
///
/// <para>
/// 为什么需要它：达到工具轮上限的那一轮，模型往往"还想再查一次"。没有工具通道时，
/// 它会把调用标记写进正文（实测 DeepSeek 的 DSML 标记块），这一轮既没有回答、也没有工具可执行，
/// 整条回答只能作废。把"本轮是最后一步、直接用文字给结论"说在前面，它才会改用文字交代。
/// </para>
///
/// <para>
/// 放在 <see cref="EmptyResponseRetryChatModel"/> **外层**：一次重试属于同一次模型调用，
/// 若把本装饰器放内层，重试会被算成新一轮，收尾轮判断就提前了。
/// </para>
///
/// <para>
/// 说明文本由宿主持有（<see cref="MafAgentEngineOptions.FinalRoundInstruction"/>）：
/// 它是提示词，不是引擎语义，框架不替宿主决定措辞。
/// </para>
/// </summary>
internal sealed class FinalRoundInstructionChatModel(
    IChatModel inner,
    int maxToolRounds,
    string instruction) : IChatModel
{
    private readonly int _finalCallIndex = Math.Max(0, maxToolRounds) + 1;
    private int _calls;

    public string ModelName => inner.ModelName;

    public bool IsConfigured => inner.IsConfigured;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _calls++;
        var isFinalRound = _calls >= _finalCallIndex;

        IReadOnlyList<ChatMessage> requestMessages = isFinalRound
            ? [.. messages, new ChatMessage(ChatRole.System, instruction)]
            : messages;

        await foreach (var delta in inner
            .StreamAsync(requestMessages, isFinalRound ? null : tools, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return delta;
        }
    }
}
