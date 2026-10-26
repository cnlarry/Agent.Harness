using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Spike;

/// <summary>永远请求调用 get_order 的脚本模型，用于验证最大轮次收尾。</summary>
internal sealed class ScriptedToolCallModel : IChatModel
{
    public int Calls { get; private set; }

    public string ModelName => "scripted-tool-call";

    public bool IsConfigured => true;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();

        yield return new ChatDelta(
            ContentDelta: null,
            Usage: new ChatUsage(10, 5, 0),
            ToolCallDeltas:
            [
                new ProposedToolCallFragment(
                    Index: 0,
                    Id: $"call_script_{Calls}",
                    Name: "get_order",
                    ArgumentsFragment: """{"orderNo":"SO-1001"}"""),
            ],
            FinishReason: "tool_calls");
    }
}

/// <summary>固定返回指定状态码的 HTTP 桩，用于验证 401/402/429/413/5xx 映射。</summary>
internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(responder(request));
    }
}
