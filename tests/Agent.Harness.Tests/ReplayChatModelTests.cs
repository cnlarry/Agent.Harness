using Agent.Harness.Abstractions;
using Agent.Harness.Testing;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ReplayChatModelTests
{
    [Fact]
    public async Task StreamAsync_ThrowsWhenMessagesDiffer()
    {
        var transcript = new TurnTranscript(
            "mismatch",
            "model",
            [
                new ChatCallTranscript(
                    [new ChatMessage(ChatRole.User, "expected")],
                    Tools: null,
                    Output: [new ChatDelta("ok", null, FinishReason: "stop")]),
            ]);

        var replay = new ReplayChatModel(transcript);

        var exception = await Assert.ThrowsAsync<TranscriptMismatchException>(async () =>
        {
            await foreach (var _ in replay.StreamAsync(
                [new ChatMessage(ChatRole.User, "actual")],
                tools: null))
            {
            }
        });

        Assert.Contains("正文不同", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCompleted_ThrowsWhenCallsRemain()
    {
        var transcript = new TurnTranscript(
            "incomplete",
            "model",
            [
                new ChatCallTranscript(
                    [],
                    Tools: null,
                    Output: [new ChatDelta("ok", null, FinishReason: "stop")]),
            ]);

        var replay = new ReplayChatModel(transcript);

        Assert.Throws<TranscriptMismatchException>(() => replay.EnsureCompleted());
    }
}
