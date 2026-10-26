using Agent.Harness.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agent.Harness.AspNetCore;

/// <summary>
/// 默认的回合组装器：身份只取 Claims，系统提示/轮次/历史上限只取服务端配置，
/// 历史从 <see cref="IConversationStore"/> 加载（未注册时为空）；注册存储时
/// 先校验会话归属，跨用户访问直接拒绝。
/// </summary>
public sealed class ClaimsAgentTurnFactory : IAgentTurnFactory
{
    private readonly AgentHarnessEndpointOptions _options;
    private readonly IConversationStore? _conversationStore;

    public ClaimsAgentTurnFactory(
        IOptions<AgentHarnessEndpointOptions> options,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        _options = options.Value;
        _conversationStore = services.GetService<IConversationStore>();
    }

    public async ValueTask<AgentTurnRequest> CreateAsync(
        HttpContext context,
        AgentChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var userId = context.User.FindFirst(_options.UserClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException($"缺少用户身份 Claim：{_options.UserClaimType}");
        }

        var tenantId = context.User.FindFirst(_options.TenantClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new UnauthorizedAccessException($"缺少租户 Claim：{_options.TenantClaimType}");
        }

        if (string.IsNullOrWhiteSpace(request.Input))
        {
            throw new ArgumentException("Input 不能为空。", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            throw new ArgumentException("ConversationId 不能为空。", nameof(request));
        }

        IReadOnlyList<AgentMessage> history = Array.Empty<AgentMessage>();

        if (_conversationStore is not null)
        {
            var owned = await _conversationStore
                .IsOwnedByAsync(tenantId, userId, request.ConversationId, cancellationToken)
                .ConfigureAwait(false);

            if (!owned)
            {
                throw new ConversationAccessDeniedException("无权访问该会话。");
            }

            history = await _conversationStore.LoadAsync(
                tenantId,
                userId,
                request.ConversationId,
                _options.HistoryLimit,
                cancellationToken).ConfigureAwait(false);
        }

        return new AgentTurnRequest(
            TenantId: tenantId,
            UserId: userId,
            ConversationId: request.ConversationId,
            Input: request.Input,
            History: history,
            SystemPrompt: _options.SystemPrompt,
            CorrelationId: context.TraceIdentifier,
            PageContext: request.PageContext,
            MaxToolRounds: _options.MaxToolRounds);
    }
}
