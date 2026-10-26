using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agent.Harness.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 SSE 端点的默认依赖：Claims 组装器与服务端选项。
    /// 宿主仍需注册引擎、预算、审计、会话存储，并自行决定端点授权策略。
    /// </summary>
    public static IServiceCollection AddAgentHarnessAspNetCore(
        this IServiceCollection services,
        Action<AgentHarnessEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AgentHarnessEndpointOptions>()
            .Validate(
                value => !string.IsNullOrWhiteSpace(value.UserClaimType),
                "UserClaimType 不能为空。")
            .Validate(
                value => !string.IsNullOrWhiteSpace(value.TenantClaimType),
                "TenantClaimType 不能为空。")
            .Validate(
                value => value.MaxToolRounds > 0,
                "MaxToolRounds 必须大于 0。")
            .Validate(
                value => value.HistoryLimit > 0,
                "HistoryLimit 必须大于 0。");

        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddScoped<IAgentTurnFactory, ClaimsAgentTurnFactory>();
        return services;
    }
}
