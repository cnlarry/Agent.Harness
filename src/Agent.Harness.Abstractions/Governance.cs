namespace Agent.Harness.Abstractions;

/// <summary>工具执行前的策略判定。</summary>
public abstract record ToolDecision
{
    public sealed record Allow : ToolDecision;

    public sealed record Deny(string Code, string Message) : ToolDecision;

    public sealed record RequireApproval(string CardJson) : ToolDecision;
}

/// <summary>工具策略：负责“这个用户此刻能不能执行这个工具”。</summary>
public interface IToolPolicy
{
    ValueTask<ToolDecision> EvaluateAsync(
        ToolDescriptor tool,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>一次回合的预算预留凭证。</summary>
public sealed record BudgetLease(string LeaseId, decimal ReservedAmount);

/// <summary>成本预留/结算。实现必须保证并发下不超卖。</summary>
public interface ITurnBudget
{
    ValueTask<BudgetLease> ReserveAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken = default);

    ValueTask SettleAsync(
        BudgetLease lease,
        AgentUsage usage,
        bool completed,
        CancellationToken cancellationToken = default);
}

/// <summary>审计事件。具体落点由宿主实现（SQL、文件、SIEM 等）。</summary>
public sealed record AgentAuditEvent(
    string TenantId,
    string UserId,
    string ConversationId,
    string CorrelationId,
    string Action,
    bool Success = true,
    string? Detail = null);

public interface IAuditSink
{
    ValueTask WriteAsync(
        AgentAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}
