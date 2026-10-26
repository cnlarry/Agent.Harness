using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>按预设回合返回增量的测试模型。用于回放与引擎契约测试，不访问网络。</summary>
public sealed class FakeChatModel : IChatModel
{
    private readonly Queue<IReadOnlyList<ChatDelta>> _turns;

    public FakeChatModel(params IReadOnlyList<ChatDelta>[] turns)
    {
        _turns = new Queue<IReadOnlyList<ChatDelta>>(turns);
    }

    public string ModelName => "fake-model";

    public bool IsConfigured => true;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_turns.Count == 0)
        {
            yield break;
        }

        foreach (var delta in _turns.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return delta;
        }
    }

    public static FakeChatModel Text(string text) =>
        new([new ChatDelta(text, null)]);
}
