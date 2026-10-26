using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agent.Harness.Abstractions;
using Microsoft.Extensions.AI;
using AbstractionsChatMessage = Agent.Harness.Abstractions.ChatMessage;
using AbstractionsChatRole = Agent.Harness.Abstractions.ChatRole;
using AbstractionsToolDefinition = Agent.Harness.Abstractions.ToolDefinition;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Harness.Maf;

/// <summary>
/// 把宿主的 <see cref="IChatModel"/> 适配成 MAF 能消费的 <see cref="IChatClient"/>。
/// 这是防腐层：先固定现有供应商行为，再按需换成原生连接器。
/// </summary>
public sealed class ChatModelChatClient(IChatModel model) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var requestMessages = BuildRequestMessages(messages, options);
        var tools = options?.Tools?
            .OfType<AIFunctionDeclaration>()
            .Select(tool => new AbstractionsToolDefinition(
                tool.Name,
                tool.Description ?? string.Empty,
                tool.JsonSchema.GetRawText()))
            .ToArray();

        // 同一次模型调用的所有增量必须共享 MessageId，否则 MAF 会把多个 tool call
        // 拆成多条 assistant 消息，回传给供应商时缺少配对的 tool 消息（DeepSeek 400）。
        var messageId = Guid.NewGuid().ToString("N");
        var calls = new SortedDictionary<int, ToolCallAccumulator>();

        await foreach (var delta in model
            .StreamAsync(requestMessages, tools, cancellationToken)
            .ConfigureAwait(false))
        {
            var finishReason = ToFinishReason(delta.FinishReason);
            var finishReasonEmitted = false;

            if (!string.IsNullOrEmpty(delta.ContentDelta))
            {
                yield return new ChatResponseUpdate(
                    MeaiChatRole.Assistant,
                    [new TextContent(delta.ContentDelta)])
                {
                    MessageId = messageId,
                };
            }

            if (!string.IsNullOrEmpty(delta.ReasoningDelta))
            {
                yield return new ChatResponseUpdate(
                    MeaiChatRole.Assistant,
                    [new TextReasoningContent(delta.ReasoningDelta)])
                {
                    MessageId = messageId,
                };
            }

            if (delta.ToolCallDeltas is not null)
            {
                foreach (var fragment in delta.ToolCallDeltas)
                {
                    if (!calls.TryGetValue(fragment.Index, out var accumulator))
                    {
                        accumulator = new ToolCallAccumulator();
                        calls[fragment.Index] = accumulator;
                    }

                    accumulator.Append(fragment);
                }
            }

            if (delta.Usage is not null)
            {
                yield return new ChatResponseUpdate(
                    MeaiChatRole.Assistant,
                    [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = delta.Usage.PromptTokens,
                        OutputTokenCount = delta.Usage.CompletionTokens,
                    })])
                {
                    MessageId = messageId,
                    FinishReason = finishReason,
                };

                finishReasonEmitted = finishReason is not null;
            }

            if (finishReason is not null && calls.Count > 0)
            {
                // 一轮里的多个 tool call 必须放进同一条 update，MAF 才会聚合为一条
                // assistant 消息，并逐一配对 tool 结果。
                yield return CreateToolCallsUpdate(calls, messageId, finishReason);
                calls.Clear();
                finishReasonEmitted = true;
            }

            if (finishReason is not null && !finishReasonEmitted)
            {
                yield return new ChatResponseUpdate(
                    MeaiChatRole.Assistant,
                    Array.Empty<AIContent>())
                {
                    MessageId = messageId,
                    FinishReason = finishReason,
                };
            }
        }

        if (calls.Count > 0)
        {
            yield return CreateToolCallsUpdate(calls, messageId, finishReason: null);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <summary>
    /// 组装发给供应商的消息：把 <see cref="ChatOptions.Instructions"/> 转成**第一条 system 消息**。
    ///
    /// <para>
    /// 不转的后果是提示词整段消失：MAF 把 system prompt 放在 <c>ChatOptions.Instructions</c> 里，
    /// 而供应商协议要的是消息列表里的一条 system。原生连接器（OpenAI 等）会做这层映射，
    /// 这里的防腐层如果不做，宿主精心维护的规则（权限口径、指标口径、禁止编造）
    /// 在模型侧根本不存在——而且**没有任何报错**，只是回答开始不守规矩。
    /// </para>
    ///
    /// <para>
    /// 已经带了 system 消息时不再补：某些调用方会自己把指令拼进消息列表，
    /// 补一条就会得到两段互相矛盾的指令。
    /// </para>
    /// </summary>
    private static IReadOnlyList<AbstractionsChatMessage> BuildRequestMessages(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options)
    {
        var converted = messages.SelectMany(ToAbstractionsMessages).ToList();

        var instructions = options?.Instructions;
        if (!string.IsNullOrWhiteSpace(instructions)
            && !converted.Any(message => message.Role == AbstractionsChatRole.System))
        {
            converted.Insert(0, new AbstractionsChatMessage(AbstractionsChatRole.System, instructions));
        }

        return converted;
    }

    public void Dispose()
    {
    }

    private static ChatResponseUpdate CreateToolCallsUpdate(
        SortedDictionary<int, ToolCallAccumulator> calls,
        string messageId,
        ChatFinishReason? finishReason)
    {
        var contents = new List<AIContent>();

        foreach (var call in calls.Values)
        {
            if (string.IsNullOrEmpty(call.Name))
            {
                continue;
            }

            contents.Add(new FunctionCallContent(
                call.Id ?? $"call_{Guid.NewGuid():N}",
                call.Name,
                call.ParseArguments()));
        }

        return new ChatResponseUpdate(MeaiChatRole.Assistant, contents)
        {
            MessageId = messageId,
            FinishReason = finishReason,
        };
    }

    private static ChatFinishReason? ToFinishReason(string? value) => value switch
    {
        null => null,
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "tool_calls" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => new ChatFinishReason(value),
    };

    // 一条 MEAI 消息可能包含多个 FunctionResultContent。供应商要求每个
    // tool_call_id 各有一条 role=tool 消息，不能合并成一条。
    private static IEnumerable<AbstractionsChatMessage> ToAbstractionsMessages(MeaiChatMessage message)
    {
        var text = new StringBuilder();
        var toolCalls = new List<CompletedToolCall>();
        var results = new List<(string CallId, string Content)>();

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    text.Append(textContent.Text);
                    break;
                case FunctionCallContent functionCall:
                    toolCalls.Add(new CompletedToolCall(
                        functionCall.CallId,
                        functionCall.Name,
                        JsonSerializer.Serialize(functionCall.Arguments)));
                    break;
                case FunctionResultContent functionResult:
                    results.Add((
                        functionResult.CallId,
                        functionResult.Result?.ToString() ?? string.Empty));
                    break;
            }
        }

        if (results.Count > 0)
        {
            foreach (var result in results)
            {
                yield return new AbstractionsChatMessage(
                    AbstractionsChatRole.Tool,
                    result.Content,
                    null,
                    result.CallId);
            }

            yield break;
        }

        var role = message.Role == MeaiChatRole.System ? AbstractionsChatRole.System
            : message.Role == MeaiChatRole.Assistant ? AbstractionsChatRole.Assistant
            : message.Role == MeaiChatRole.Tool ? AbstractionsChatRole.Tool
            : AbstractionsChatRole.User;

        yield return new AbstractionsChatMessage(
            role,
            text.ToString(),
            toolCalls.Count > 0 ? toolCalls : null,
            null);
    }

    private sealed class ToolCallAccumulator
    {
        private readonly StringBuilder _arguments = new();

        public string? Id { get; private set; }

        public string? Name { get; private set; }

        public void Append(ProposedToolCallFragment fragment)
        {
            Id ??= fragment.Id;
            Name ??= fragment.Name;
            _arguments.Append(fragment.ArgumentsFragment);
        }

        public IDictionary<string, object?> ParseArguments()
        {
            if (_arguments.Length == 0)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            using var document = JsonDocument.Parse(_arguments.ToString());
            return document.RootElement
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => (object?)property.Value.Clone(),
                    StringComparer.Ordinal);
        }
    }
}
