using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Maf;

/// <summary>
/// 对“没有正文、没有工具调用、正常结束”的模型响应重试一次。
/// 只读/写路径都安全：没有工具调用意味着没有副作用；最大重试次数由 options 控制。
/// </summary>
internal sealed class EmptyResponseRetryChatModel(IChatModel inner, int maxRetries) : IChatModel
{
    private readonly int _maxRetries = Math.Max(0, maxRetries);

    public string ModelName => inner.ModelName;

    public bool IsConfigured => inner.IsConfigured;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sawContent = false;
            var sawToolCall = false;
            string? finishReason = null;

            await foreach (var delta in inner
                .StreamAsync(messages, tools, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(delta.ContentDelta))
                {
                    sawContent = true;
                }

                if (delta.ToolCallDeltas is { Count: > 0 })
                {
                    sawToolCall = true;
                }

                if (!string.IsNullOrEmpty(delta.FinishReason))
                {
                    finishReason = delta.FinishReason;
                }

                yield return delta;
            }

            if (sawContent || sawToolCall || attempt >= _maxRetries)
            {
                yield break;
            }

            // 只对正常结束（stop/null）的空响应重试；length/tool_calls 不重试。
            if (finishReason is not null &&
                !string.Equals(finishReason, "stop", StringComparison.Ordinal))
            {
                yield break;
            }
        }
    }
}
