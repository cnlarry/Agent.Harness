using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>
/// 在 <see cref="IChatModel"/> 边界录制每个模型调用的输入、工具声明与流式输出。
/// 不访问网络，也不修改底层模型行为。
/// </summary>
public sealed class RecordingChatModel : IChatModel
{
    private readonly IChatModel _inner;
    private readonly List<ChatCallTranscript> _calls = [];

    public RecordingChatModel(IChatModel inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string ModelName => _inner.ModelName;

    public bool IsConfigured => _inner.IsConfigured;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messagesCopy = messages.ToArray();
        var toolsCopy = tools?.ToArray();
        var output = new List<ChatDelta>();

        await foreach (var delta in _inner
            .StreamAsync(messages, tools, cancellationToken)
            .ConfigureAwait(false))
        {
            output.Add(delta);
            yield return delta;
        }

        _calls.Add(new ChatCallTranscript(messagesCopy, toolsCopy, output.ToArray()));
    }

    public TurnTranscript Build(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new TurnTranscript(name, _inner.ModelName, _calls.ToArray());
    }
}
