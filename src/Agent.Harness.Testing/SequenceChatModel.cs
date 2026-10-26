using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>
/// 按**序号**回放模型输出，不校验请求内容，并记下每次调用收到的消息角色序列。
///
/// <para>
/// 与 <see cref="ReplayChatModel"/> 的分工：那个用于**同一个引擎**的回归——请求变了就该红，
/// 所以逐字校验；这个用于**跨引擎**对比——两端组装的请求本来就不同，逐字校验只会把
/// 「引擎差异」报成「回放不匹配」。**不要用它做单引擎回归**，那会放过提示词悄悄变化。
/// </para>
/// </summary>
public sealed class SequenceChatModel : IChatModel
{
    private readonly IReadOnlyList<ParityModelCall> _calls;
    private readonly List<string> _requestShapes = [];
    private int _index;

    public SequenceChatModel(IReadOnlyList<ParityModelCall> calls, string modelName = "parity-model")
    {
        ArgumentNullException.ThrowIfNull(calls);
        _calls = calls;
        ModelName = modelName;
    }

    public string ModelName { get; }

    public bool IsConfigured => true;

    /// <summary>已发生的模型调用次数（含正在进行的这次）。</summary>
    public int Calls => _index;

    /// <summary>每次调用收到的消息角色序列与工具声明数，如 <c>System,User tools=3</c>。</summary>
    public IReadOnlyList<string> RequestShapes => _requestShapes;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _requestShapes.Add(Shape(messages, tools));

        if (_index >= _calls.Count)
        {
            throw new TranscriptMismatchException(
                $"回放：模型被调用了 {_index + 1} 次，但用例只脚本化了 {_calls.Count} 次。");
        }

        var call = _calls[_index];
        _index++;

        foreach (var delta in call.Output)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return delta;
        }
    }

    /// <summary>
    /// 请求形状：角色序列 + 本轮下发的工具数。
    ///
    /// <para>
    /// 工具数必须记：两个引擎在"最后一轮还发不发工具"上语义不同（旧引擎收尾轮撤掉工具声明，
    /// MAF 照发），而这个差异只体现在请求上——不记就永远看不见。
    /// </para>
    /// </summary>
    private static string Shape(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools)
    {
        var roles = string.Join(',', messages.Select(message => message.Role.ToString()));
        return tools is null ? $"{roles} tools=none" : $"{roles} tools={tools.Count}";
    }
}
