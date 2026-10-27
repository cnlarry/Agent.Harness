using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Harness.Maf;

/// <summary>
/// MAF 引擎适配。只替换“模型 + 工具循环”，不接管权限、落库、预算或审计。
/// </summary>
public sealed class MafAgentEngine(
    IChatModel model,
    ToolRegistry tools,
    IToolPolicy policy,
    ILoggerFactory loggerFactory,
    MafAgentEngineOptions? options = null) : IAgentEngine
{
    private readonly MafAgentEngineOptions _options = options ?? new MafAgentEngineOptions();

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IChatModel effectiveModel = _options.MaxEmptyResponseRetries > 0
            ? new EmptyResponseRetryChatModel(model, _options.MaxEmptyResponseRetries)
            : model;

        // 收尾轮说明放在重试装饰器外层：一次重试属于同一次模型调用，不能被算成新一轮。
        if (!string.IsNullOrWhiteSpace(_options.FinalRoundInstruction))
        {
            effectiveModel = new FinalRoundInstructionChatModel(
                effectiveModel,
                request.MaxToolRounds,
                _options.FinalRoundInstruction);
        }

        var invoking = new FunctionInvokingChatClient(
            new ChatModelChatClient(effectiveModel),
            loggerFactory)
        {
            MaximumIterationsPerRequest = request.MaxToolRounds,
            AllowConcurrentInvocation = _options.AllowConcurrentToolInvocation,
            MaximumConsecutiveErrorsPerRequest = 2,
            TerminateOnUnknownCalls = _options.TerminateOnUnknownCalls,
            IncludeDetailedErrors = false,
        };

        var toolResults = new ConcurrentDictionary<string, ToolResult>(StringComparer.Ordinal);

        var agent = new ChatClientAgent(invoking, new ChatClientAgentOptions
        {
            Id = "agent-harness",
            Name = "Agent Harness",
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                Instructions = request.SystemPrompt,
                Tools = BuildFunctions(request, toolResults),
                AllowMultipleToolCalls = true,
            },
        });

        var messages = request.History.Select(ToMeaiMessage).ToList();
        if (!string.IsNullOrWhiteSpace(request.Input))
        {
            messages.Add(new MeaiChatMessage(MeaiChatRole.User, request.Input));
        }

        var text = new StringBuilder();
        var usage = AgentUsage.Empty;
        var toolCalls = new List<ToolCallRecord>();
        var drafts = new List<object>();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingCalls = new Dictionary<string, (string Name, string Arguments)>(StringComparer.Ordinal);
        string? finishReason = null;

        await foreach (var update in agent
            .RunStreamingAsync(messages, session: null, options: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            if (update.FinishReason is not null)
            {
                // 工具轮次会依次出现 tool_calls → stop，最终结果取最后一次。
                finishReason = update.FinishReason.ToString();
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                        text.Append(textContent.Text);
                        yield return new AgentEvent.TextDelta(textContent.Text);
                        break;

                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        yield return new AgentEvent.ReasoningDelta(reasoning.Text);
                        break;

                    case FunctionCallContent call:
                        callNames[call.CallId] = call.Name;
                        pendingCalls[call.CallId] = (
                            call.Name,
                            JsonSerializer.Serialize(call.Arguments));
                        yield return new AgentEvent.ToolStarted(
                            call.CallId,
                            call.Name,
                            tools.RiskOf(call.Name));
                        break;

                    case FunctionResultContent result:
                        var name = callNames.GetValueOrDefault(result.CallId, "unknown");
                        var digest = Digest(result.Result);
                        var ok = result.Exception is null;

                        if (toolResults.TryRemove(result.CallId, out var toolResult))
                        {
                            // ToolResult.Ok=false（策略拒绝/工具失败）不能因为异常为空就当成成功。
                            ok = toolResult.Ok && result.Exception is null;
                            // 宿主给了摘要就用它：正文是给模型的，摘要才是给人看的（含可检索的失败标记）。
                            digest = toolResult.Digest ?? digest;
                            if (toolResult.Draft is not null)
                            {
                                drafts.Add(toolResult.Draft);
                            }
                        }

                        if (pendingCalls.Remove(result.CallId, out var pending))
                        {
                            toolCalls.Add(new ToolCallRecord(pending.Name, pending.Arguments, digest));
                        }

                        yield return new AgentEvent.ToolFinished(
                            result.CallId,
                            name,
                            digest,
                            ok);
                        break;

                    case UsageContent usageContent:
                        usage = usage.Add(new AgentUsage(
                            (int)(usageContent.Details.InputTokenCount ?? 0),
                            (int)(usageContent.Details.OutputTokenCount ?? 0)));
                        break;
                }
            }
        }

        // 达到最大工具轮次时，供应商仍可能返回一个新的 tool call；MAF 会结束循环
        // 但不会执行它。这里补一条 Ok=false 的收尾事件，保证 ToolStarted 都有配对，
        // 并把结束原因标成 max_tool_rounds，宿主不能把空文本当成正常完成。
        var exceededMaxRounds = pendingCalls.Count > 0;
        foreach (var pending in pendingCalls)
        {
            const string digest = "未执行：达到最大工具轮次";
            toolCalls.Add(new ToolCallRecord(pending.Value.Name, pending.Value.Arguments, digest));
            yield return new AgentEvent.ToolFinished(
                pending.Key,
                pending.Value.Name,
                digest,
                Ok: false);
        }

        pendingCalls.Clear();

        if (exceededMaxRounds)
        {
            finishReason = AgentFinishReasons.MaxToolRounds;
        }

        yield return new AgentEvent.Completed(new AgentTurnResult(
            text.ToString(),
            model.ModelName,
            usage,
            toolCalls,
            drafts,
            finishReason));
    }

    private List<AITool> BuildFunctions(
        AgentTurnRequest request,
        ConcurrentDictionary<string, ToolResult> toolResults)
    {
        var functions = new List<AITool>();

        IReadOnlyList<ITool> resolved;
        if (_options.EnabledTools is { Length: 0 })
        {
            // 空 allowlist 表示“一个都不暴露”，不能退回 ToolRegistry 的“空=全部”。
            resolved = [];
        }
        else
        {
            resolved = tools.Resolve(_options.EnabledTools);
        }

        if (_options.ReadOnlyToolsOnly)
        {
            resolved = resolved
                .Where(tool => tool.Descriptor.ReadOnly && tool.Descriptor.Risk == ToolRisk.Read)
                .ToArray();
        }

        foreach (var tool in resolved)
        {
            var descriptor = tool.Descriptor;

            functions.Add(new JsonSchemaAIFunction(descriptor, async (arguments, token) =>
            {
                var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId
                    ?? string.Empty;
                var invocation = new ToolInvocation(
                    request.TenantId,
                    request.UserId,
                    request.ConversationId,
                    callId,
                    arguments,
                    request.PageContext);

                var decision = await policy.EvaluateAsync(descriptor, invocation, token)
                    .ConfigureAwait(false);

                ToolResult result;

                if (decision is ToolDecision.Deny deny)
                {
                    result = ToolResult.Deny(deny.Message);
                }
                // 第一版 fail-closed：需要审批就拒绝并回喂原因；
                // P3 再把 RequireApproval 映射为 ApprovalRequiredAIFunction / 宿主操作卡。
                else if (decision is ToolDecision.RequireApproval approval)
                {
                    result = ToolResult.Deny($"需要用户确认：{approval.CardJson}");
                }
                else if (!tools.TryGet(descriptor.Name, out var implementation))
                {
                    result = ToolResult.Deny($"未知工具：{descriptor.Name}");
                }
                else
                {
                    result = await implementation.InvokeAsync(invocation, token).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(callId))
                {
                    toolResults[callId] = result;
                }

                return result;
            }));
        }

        return functions;
    }

    private static MeaiChatMessage ToMeaiMessage(AgentMessage message)
    {
        var role = message.Role switch
        {
            AgentRole.System => MeaiChatRole.System,
            AgentRole.Assistant => MeaiChatRole.Assistant,
            AgentRole.Tool => MeaiChatRole.Tool,
            _ => MeaiChatRole.User,
        };

        if (message.ToolCallId is not null)
        {
            return new MeaiChatMessage(
                role,
                [new FunctionResultContent(message.ToolCallId, message.Content)]);
        }

        var contents = new List<AIContent> { new TextContent(message.Content) };
        if (message.ToolCalls is not null)
        {
            foreach (var call in message.ToolCalls)
            {
                contents.Add(new FunctionCallContent(
                    call.Id,
                    call.Name,
                    ParseArguments(call.ArgumentsJson)));
            }
        }

        return new MeaiChatMessage(role, contents);
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
    }

    private static string Digest(object? result)
    {
        var text = result?.ToString() ?? string.Empty;
        return text.Length <= 200 ? text : text[..200];
    }
}
