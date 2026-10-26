using Agent.Harness.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agent.Harness.AspNetCore;

/// <summary>
/// 客户端唯一可以提交的回合输入。租户、用户、历史、系统提示与轮次上限
/// 都必须由宿主从 Claims 或服务端配置组装，不能由请求体提供。
/// </summary>
public sealed record AgentChatRequest(
    string Input,
    string ConversationId,
    AgentPageContext? PageContext = null);

/// <summary>把客户端输入组装成受信任的 <see cref="AgentTurnRequest"/>。</summary>
public interface IAgentTurnFactory
{
    ValueTask<AgentTurnRequest> CreateAsync(
        HttpContext context,
        AgentChatRequest request,
        CancellationToken cancellationToken = default);
}
