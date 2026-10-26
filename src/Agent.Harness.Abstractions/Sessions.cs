namespace Agent.Harness.Abstractions;

/// <summary>
/// 会话持久化契约。数据库是会话的唯一事实源；
/// 不要求实现 Agent 框架自己的 Session Store。
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// 判断会话是否属于当前租户/用户。新会话（尚无记录）应返回 <see langword="true"/>；
    /// 已存在但属于其他用户的会话必须返回 <see langword="false"/>。
    /// </summary>
    ValueTask<bool> IsOwnedByAsync(
        string tenantId,
        string userId,
        string conversationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentMessage>> LoadAsync(
        string tenantId,
        string userId,
        string conversationId,
        int maxMessages,
        CancellationToken cancellationToken = default);

    ValueTask SaveUserMessageAsync(
        string tenantId,
        string userId,
        string conversationId,
        string content,
        string correlationId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAssistantMessageAsync(
        string tenantId,
        string userId,
        string conversationId,
        AgentTurnResult result,
        string correlationId,
        CancellationToken cancellationToken = default);
}
