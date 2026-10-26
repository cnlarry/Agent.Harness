using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Harness.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>注册 Core 的工具注册表与回合管线。引擎、预算、审计由宿主提供。</summary>
    public static IServiceCollection AddAgentHarnessCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ToolRegistry>();
        services.TryAddScoped<TurnPipeline>();
        return services;
    }
}
