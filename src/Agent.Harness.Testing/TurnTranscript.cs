using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>一次模型调用的输入与输出。录制点在 <see cref="IChatModel"/> 边界。</summary>
public sealed record ChatCallTranscript(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition>? Tools,
    IReadOnlyList<ChatDelta> Output);

/// <summary>一个回合内所有模型调用的录制。</summary>
public sealed record TurnTranscript(
    string Name,
    string ModelName,
    IReadOnlyList<ChatCallTranscript> Calls);

/// <summary>回放时请求与录制不一致。</summary>
public sealed class TranscriptMismatchException(string message) : Exception(message);
