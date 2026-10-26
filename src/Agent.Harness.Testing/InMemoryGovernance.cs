using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

public sealed record BudgetSettlement(BudgetLease Lease, AgentUsage Usage, bool Completed);

/// <summary>测试/示例用的内存预算台账。</summary>
public sealed class InMemoryTurnBudget : ITurnBudget
{
    public List<BudgetLease> Reservations { get; } = [];

    public List<BudgetSettlement> Settlements { get; } = [];

    public ValueTask<BudgetLease> ReserveAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var lease = new BudgetLease(Guid.NewGuid().ToString("N"), 0m);
        Reservations.Add(lease);
        return ValueTask.FromResult(lease);
    }

    public ValueTask SettleAsync(
        BudgetLease lease,
        AgentUsage usage,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        Settlements.Add(new BudgetSettlement(lease, usage, completed));
        return ValueTask.CompletedTask;
    }
}

/// <summary>测试/示例用的内存审计。</summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    public List<AgentAuditEvent> Events { get; } = [];

    public ValueTask WriteAsync(
        AgentAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return ValueTask.CompletedTask;
    }
}

/// <summary>全部放行的策略，仅用于测试与本地示例；生产必须换成真实权限策略。</summary>
public sealed class AllowAllToolPolicy : IToolPolicy
{
    public ValueTask<ToolDecision> EvaluateAsync(
        ToolDescriptor tool,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ToolDecision>(new ToolDecision.Allow());
}
