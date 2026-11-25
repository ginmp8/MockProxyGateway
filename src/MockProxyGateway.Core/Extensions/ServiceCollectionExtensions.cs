using Microsoft.Extensions.DependencyInjection.Extensions;
using MockProxyGateway;
using MockProxyGateway.Configuration;
using MockProxyGateway.Handlers;
using MockProxyGateway.Middleware;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Gateway dependency injection registration.</summary>
public static class MockProxyGatewayServiceCollectionExtensions
{
    /// <summary>Register gateway services without activating routes or requiring authentication.</summary>
    public static IServiceCollection AddMockProxyGateway(this IServiceCollection services, Action<GatewayOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<GatewayOptions>().BindConfiguration("MockProxyGateway");
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RuleStore>();
        services.TryAddSingleton<ProxyTransport>();
        services.TryAddSingleton<GatewayRuntime>();
        services.AddHttpForwarder();
        return services;
    }
}
