using System.Security.Claims;

namespace Agent.Harness.AspNetCore;

/// <summary>SSE 端点的服务端配置；不要把这些值交给客户端请求体。</summary>
public sealed class AgentHarnessEndpointOptions
{
    /// <summary>用户标识 Claim；默认 <see cref="ClaimTypes.NameIdentifier"/>。</summary>
    public string UserClaimType { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>租户标识 Claim；默认 <c>tenant_id</c>。</summary>
    public string TenantClaimType { get; set; } = "tenant_id";

    /// <summary>系统提示词，由宿主在启动时配置。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>单回合最大工具轮次。</summary>
    public int MaxToolRounds { get; set; } = 4;

    /// <summary>从会话存储加载的最大历史消息数。</summary>
    public int HistoryLimit { get; set; } = 50;
}
