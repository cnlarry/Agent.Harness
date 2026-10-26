using Agent.Harness.Abstractions;

namespace Agent.Harness.Testing;

/// <summary>
/// 内存会话存储：按 <c>(TenantId, UserId, ConversationId)</c> 隔离消息，
/// 并按 <c>(TenantId, ConversationId)</c> 记录归属；跨用户读取/写入会被拒绝。
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string TenantId, string ConversationId), string> _owners = [];
    private readonly Dictionary<(string TenantId, string UserId, string ConversationId), List<AgentMessage>> _messages = [];

    public ValueTask<bool> IsOwnedByAsync(
        string tenantId,
        string userId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var owned = !_owners.TryGetValue((tenantId, conversationId), out var owner) ||
                        owner == userId;
            return ValueTask.FromResult(owned);
        }
    }

    public ValueTask<IReadOnlyList<AgentMessage>> LoadAsync(
        string tenantId,
        string userId,
        string conversationId,
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue((tenantId, conversationId), out var owner) &&
                owner != userId)
            {
                return ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
            }

            if (!_messages.TryGetValue((tenantId, userId, conversationId), out var messages))
            {
                return ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
            }

            var limit = Math.Max(0, maxMessages);
            IReadOnlyList<AgentMessage> result = limit == 0
                ? []
                : messages.TakeLast(limit).ToArray();

            return ValueTask.FromResult(result);
        }
    }

    public ValueTask SaveUserMessageAsync(
        string tenantId,
        string userId,
        string conversationId,
        string content,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        Add(tenantId, userId, conversationId, new AgentMessage(AgentRole.User, content));
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveAssistantMessageAsync(
        string tenantId,
        string userId,
        string conversationId,
        AgentTurnResult result,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        Add(
            tenantId,
            userId,
            conversationId,
            new AgentMessage(AgentRole.Assistant, result.Text));
        return ValueTask.CompletedTask;
    }

    private void Add(
        string tenantId,
        string userId,
        string conversationId,
        AgentMessage message)
    {
        lock (_gate)
        {
            var ownerKey = (tenantId, conversationId);
            if (_owners.TryGetValue(ownerKey, out var owner) && owner != userId)
            {
                throw new InvalidOperationException(
                    $"Conversation {conversationId} 属于其他用户，拒绝写入。");
            }

            _owners[ownerKey] = userId;

            var messageKey = (tenantId, userId, conversationId);
            if (!_messages.TryGetValue(messageKey, out var messages))
            {
                messages = [];
                _messages[messageKey] = messages;
            }

            messages.Add(message);
        }
    }
}
