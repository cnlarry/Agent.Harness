using System.Runtime.CompilerServices;
using Agent.Harness.Abstractions;

namespace Agent.Harness.Core;

/// <summary>
/// 回合管线：预算预留 → 引擎执行 → 结算 + 审计。
/// 权限、提示词、落库由宿主在调用管线前完成；管线不替代它们。
/// 引擎异常会被转换为稳定的 <see cref="AgentEvent.Failed"/>，不会直接打断事件流；
/// 取消仍按取消异常向上传播，由宿主处理。
/// </summary>
public sealed class TurnPipeline(
    IAgentEngine engine,
    ITurnBudget budget,
    IAuditSink audit)
{
    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lease = await budget.ReserveAsync(request, cancellationToken).ConfigureAwait(false);
        var usage = AgentUsage.Empty;
        var completed = false;
        AgentEvent.Failed? failure = null;

        await audit.WriteAsync(new AgentAuditEvent(
            request.TenantId,
            request.UserId,
            request.ConversationId,
            request.CorrelationId,
            "agent.turn.started"), cancellationToken).ConfigureAwait(false);

        try
        {
            await using var enumerator = engine
                .StreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                AgentEvent current;

                // yield 不能出现在带 catch 的 try 里；只把 MoveNextAsync 包进
                // try/catch，事件本身在 catch 外产出。
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // 宿主没取消，但引擎内部超时（例如 HttpClient.Timeout）：
                    // 这是失败，不是客户端断流。
                    failure = new AgentEvent.Failed("timeout", "模型响应超时，请稍后重试。");
                    break;
                }
                catch (Exception exception)
                {
                    failure = MapFailure(exception);
                    break;
                }

                if (current is AgentEvent.Completed completedEvent)
                {
                    usage = completedEvent.Result.Usage;
                    completed = completedEvent.Result.FinishReason != AgentFinishReasons.MaxToolRounds;
                }

                yield return current;
            }
        }
        finally
        {
            await budget.SettleAsync(lease, usage, completed, CancellationToken.None)
                .ConfigureAwait(false);
            await audit.WriteAsync(new AgentAuditEvent(
                request.TenantId,
                request.UserId,
                request.ConversationId,
                request.CorrelationId,
                "agent.turn.finished",
                completed), CancellationToken.None).ConfigureAwait(false);
        }

        if (failure is not null)
        {
            yield return failure;
        }
    }

    private static AgentEvent.Failed MapFailure(Exception exception) => exception switch
    {
        ChatModelException model => new AgentEvent.Failed(model.Code, model.Message),
        TimeoutException => new AgentEvent.Failed("timeout", "模型响应超时，请稍后重试。"),
        _ => new AgentEvent.Failed("agent_error", "Agent 执行失败，请稍后重试。"),
    };
}
