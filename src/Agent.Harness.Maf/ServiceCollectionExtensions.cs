using Agent.Harness.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Harness.Maf;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 把 MAF 引擎注册为默认 <see cref="IAgentEngine"/>。宿主仍需注册模型、工具、策略与审计。
    /// 通过 DI 注册时默认只暴露只读工具；写路径阶段用 <paramref name="configure"/> 显式关闭。
    /// </summary>
    public static IServiceCollection AddAgentHarnessMaf(
        this IServiceCollection services,
        Action<MafAgentEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MafAgentEngineOptions
        {
            ReadOnlyToolsOnly = true,
        };

        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddScoped<IAgentEngine, MafAgentEngine>();
        return services;
    }

    /// <summary>
    /// 从配置节绑定 <see cref="MafAgentEngineOptions"/> 并注册引擎；
    /// 默认只读，配置中的值可以覆盖，用于部署级灰度/回滚。
    /// </summary>
    public static IServiceCollection AddAgentHarnessMaf(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddAgentHarnessMaf(options => configuration.Bind(options));
    }
}
