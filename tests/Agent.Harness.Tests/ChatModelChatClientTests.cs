using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Agent.Harness.Abstractions;
using Agent.Harness.Maf;
using Xunit;
using MeaiChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using MeaiTextReasoningContent = Microsoft.Extensions.AI.TextReasoningContent;
using MeaiUsageContent = Microsoft.Extensions.AI.UsageContent;

namespace Agent.Harness.Tests;

public sealed class ChatModelChatClientTests
{
    [Fact]
    public async Task GetStreamingResponseAsync_SplitsMultipleToolResultsPerCall()
    {
        var recorder = new RecordingChatModel();
        var client = new ChatModelChatClient(recorder);
        var message = new MeaiChatMessage(
            MeaiChatRole.Tool,
            [
                new MeaiFunctionResultContent("call_1", "one"),
                new MeaiFunctionResultContent("call_2", "two"),
            ]);

        await foreach (var _ in client.GetStreamingResponseAsync([message], null))
        {
        }

        Assert.Equal(2, recorder.Messages.Count);
        Assert.Equal(ChatRole.Tool, recorder.Messages[0].Role);
        Assert.Equal("call_1", recorder.Messages[0].ToolCallId);
        Assert.Equal("one", recorder.Messages[0].Content);
        Assert.Equal("call_2", recorder.Messages[1].ToolCallId);
        Assert.Equal("two", recorder.Messages[1].Content);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PrependsInstructionsAsSystemMessage()
    {
        // MAF 把 system prompt 放在 ChatOptions.Instructions；供应商协议要的是消息列表里的
        // 一条 system。不映射就是"提示词整段消失且不报错"。
        var recorder = new RecordingChatModel(new ChatDelta("ok", null));
        var client = new ChatModelChatClient(recorder);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new MeaiChatMessage(MeaiChatRole.User, "hi")],
            new MeaiChatOptions { Instructions = "你是企业管理系统里的助手。" }))
        {
        }

        Assert.Equal(2, recorder.Messages.Count);
        Assert.Equal(ChatRole.System, recorder.Messages[0].Role);
        Assert.Equal("你是企业管理系统里的助手。", recorder.Messages[0].Content);
        Assert.Equal(ChatRole.User, recorder.Messages[1].Role);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_DoesNotDuplicateExistingSystemMessage()
    {
        var recorder = new RecordingChatModel(new ChatDelta("ok", null));
        var client = new ChatModelChatClient(recorder);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [
                new MeaiChatMessage(MeaiChatRole.System, "已有的系统提示"),
                new MeaiChatMessage(MeaiChatRole.User, "hi"),
            ],
            new MeaiChatOptions { Instructions = "另一段指令" }))
        {
        }

        Assert.Equal(2, recorder.Messages.Count);
        Assert.Equal("已有的系统提示", recorder.Messages[0].Content);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_GroupsToolCallsAndPropagatesFinishReason()
    {
        var recorder = new RecordingChatModel(
            new ChatDelta(
                ContentDelta: null,
                Usage: null,
                ToolCallDeltas:
                [
                    new ProposedToolCallFragment(0, "call_1", "echo", """{"text":"a"}"""),
                    new ProposedToolCallFragment(1, "call_2", "echo", """{"text":"b"}"""),
                ],
                FinishReason: "tool_calls"));

        var client = new ChatModelChatClient(recorder);
        var updates = new List<MeaiChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new MeaiChatMessage(MeaiChatRole.User, "hi")],
            null))
        {
            updates.Add(update);
        }

        var calls = updates
            .SelectMany(update => update.Contents)
            .OfType<MeaiFunctionCallContent>()
            .ToArray();

        Assert.Equal(2, calls.Length);
        Assert.False(string.IsNullOrEmpty(updates[0].MessageId));
        Assert.All(updates, update => Assert.Equal(updates[0].MessageId, update.MessageId));
        Assert.Contains(updates, update => update.FinishReason == MeaiChatFinishReason.ToolCalls);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EmitsReasoningAndUsage()
    {
        var recorder = new RecordingChatModel(
            new ChatDelta(
                ContentDelta: null,
                Usage: new ChatUsage(7, 3, 1),
                ReasoningDelta: "why"),
            new ChatDelta(
                ContentDelta: "done",
                Usage: null,
                FinishReason: "stop"));

        var client = new ChatModelChatClient(recorder);
        var updates = new List<MeaiChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new MeaiChatMessage(MeaiChatRole.User, "hi")],
            null))
        {
            updates.Add(update);
        }

        var reasoning = updates
            .SelectMany(update => update.Contents)
            .OfType<MeaiTextReasoningContent>()
            .Single();
        Assert.Equal("why", reasoning.Text);

        var usage = updates
            .SelectMany(update => update.Contents)
            .OfType<MeaiUsageContent>()
            .Single();
        Assert.Equal(7, usage.Details.InputTokenCount);
        Assert.Equal(3, usage.Details.OutputTokenCount);
        Assert.Contains(updates, update => update.FinishReason == MeaiChatFinishReason.Stop);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_MapsAssistantToolCallMessage()
    {
        var recorder = new RecordingChatModel();
        var client = new ChatModelChatClient(recorder);
        var message = new MeaiChatMessage(
            MeaiChatRole.Assistant,
            [
                new MeaiFunctionCallContent(
                    "call_1",
                    "echo",
                    new Dictionary<string, object?> { ["text"] = "hi" }),
            ]);

        await foreach (var _ in client.GetStreamingResponseAsync([message], null))
        {
        }

        var mapped = Assert.Single(recorder.Messages);
        Assert.Equal(ChatRole.Assistant, mapped.Role);
        var call = Assert.Single(mapped.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("echo", call.Name);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"text":"hi"}"""),
            JsonNode.Parse(call.ArgumentsJson)));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PropagatesCancellation()
    {
        var client = new ChatModelChatClient(new CanceledChatModel());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new MeaiChatMessage(MeaiChatRole.User, "hi")],
                null,
                cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PropagatesChatModelException()
    {
        var client = new ChatModelChatClient(new FailingChatModel());

        var exception = await Assert.ThrowsAsync<ChatModelException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new MeaiChatMessage(MeaiChatRole.User, "hi")],
                null))
            {
            }
        });

        Assert.Equal("rate_limited", exception.Code);
    }

    private sealed class RecordingChatModel(params ChatDelta[] deltas) : IChatModel
    {
        public List<ChatMessage> Messages { get; } = [];

        public string ModelName => "recording-model";

        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);

            foreach (var delta in deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private sealed class CanceledChatModel : IChatModel
    {
        public string ModelName => "canceled-model";

        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatDelta("never", null);
        }
    }

    private sealed class FailingChatModel : IChatModel
    {
        public string ModelName => "failing-model";

        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatDelta("partial", null);
            await Task.Yield();
            throw new ChatModelException(
                "rate_limited",
                "模型请求过于频繁，请稍后重试。",
                isTransient: true);
        }
    }
}
