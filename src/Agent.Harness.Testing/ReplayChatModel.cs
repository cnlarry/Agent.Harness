using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>
/// 按 <see cref="TurnTranscript"/> 回放模型输出，并逐次校验引擎发出的消息与工具声明。
/// 请求不一致时抛 <see cref="TranscriptMismatchException"/>，避免“回放通过但输入已经变了”。
/// </summary>
public sealed class ReplayChatModel : IChatModel
{
    private readonly TurnTranscript _transcript;
    private int _index;

    public ReplayChatModel(TurnTranscript transcript)
    {
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
    }

    public string ModelName => _transcript.ModelName;

    public bool IsConfigured => true;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_index >= _transcript.Calls.Count)
        {
            throw new TranscriptMismatchException(
                $"回放 {_transcript.Name}：模型被调用了 {_index + 1} 次，但只录制了 {_transcript.Calls.Count} 次。");
        }

        var call = _transcript.Calls[_index];
        EnsureSameCall(call, messages, tools, _index);
        _index++;

        foreach (var delta in call.Output)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return delta;
        }
    }

    public void EnsureCompleted()
    {
        if (_index != _transcript.Calls.Count)
        {
            throw new TranscriptMismatchException(
                $"回放 {_transcript.Name}：只消费了 {_index}/{_transcript.Calls.Count} 次模型调用。");
        }
    }

    private static void EnsureSameCall(
        ChatCallTranscript expected,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int callIndex)
    {
        if (expected.Messages.Count != messages.Count)
        {
            throw new TranscriptMismatchException(
                $"回放调用 {callIndex}：消息数不同，期望 {expected.Messages.Count}，实际 {messages.Count}。");
        }

        for (var index = 0; index < expected.Messages.Count; index++)
        {
            EnsureSameMessage(expected.Messages[index], messages[index], callIndex, index);
        }

        var expectedTools = expected.Tools ?? [];
        var actualTools = tools ?? [];
        if (expectedTools.Count != actualTools.Count)
        {
            throw new TranscriptMismatchException(
                $"回放调用 {callIndex}：工具数不同，期望 {expectedTools.Count}，实际 {actualTools.Count}。");
        }

        for (var index = 0; index < expectedTools.Count; index++)
        {
            var expectedTool = expectedTools[index];
            var actualTool = actualTools[index];

            if (expectedTool.Name != actualTool.Name ||
                expectedTool.Description != actualTool.Description ||
                !JsonEqual(expectedTool.ParametersJson, actualTool.ParametersJson))
            {
                throw new TranscriptMismatchException(
                    $"回放调用 {callIndex}：工具 {expectedTool.Name} 的声明不一致。");
            }
        }
    }

    private static void EnsureSameMessage(
        ChatMessage expected,
        ChatMessage actual,
        int callIndex,
        int messageIndex)
    {
        if (expected.Role != actual.Role)
        {
            throw new TranscriptMismatchException(
                $"回放调用 {callIndex} 消息 {messageIndex}：角色不同，期望 {expected.Role}，实际 {actual.Role}。");
        }

        if (!string.Equals(expected.Content, actual.Content, StringComparison.Ordinal))
        {
            throw new TranscriptMismatchException(
                $"回放调用 {callIndex} 消息 {messageIndex}：正文不同。");
        }

        if (!string.Equals(expected.ToolCallId, actual.ToolCallId, StringComparison.Ordinal))
        {
            throw new TranscriptMismatchException(
                $"回放调用 {callIndex} 消息 {messageIndex}：ToolCallId 不同。");
        }

        var expectedCalls = expected.ToolCalls ?? [];
        var actualCalls = actual.ToolCalls ?? [];
        if (expectedCalls.Count != actualCalls.Count)
        {
            throw new TranscriptMismatchException(
                $"回放调用 {callIndex} 消息 {messageIndex}：tool call 数不同，期望 {expectedCalls.Count}，实际 {actualCalls.Count}。");
        }

        for (var index = 0; index < expectedCalls.Count; index++)
        {
            var expectedCall = expectedCalls[index];
            var actualCall = actualCalls[index];

            if (expectedCall.Id != actualCall.Id ||
                expectedCall.Name != actualCall.Name ||
                !JsonEqual(expectedCall.ArgumentsJson, actualCall.ArgumentsJson))
            {
                throw new TranscriptMismatchException(
                    $"回放调用 {callIndex} 消息 {messageIndex}：tool call {expectedCall.Id} 不一致。");
            }
        }
    }

    private static bool JsonEqual(string? expected, string? actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
