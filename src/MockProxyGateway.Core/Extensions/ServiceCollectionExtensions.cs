using System.Text.Json;
using Microsoft.Extensions.Configuration;
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

    /// <summary>Use a host-supplied configuration containing one complete serialized rule document.</summary>
    public static IServiceCollection AddMockProxyGateway(this IServiceCollection services,
        IConfiguration ruleConfiguration, Action<GatewayOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(ruleConfiguration);
        return services.AddMockProxyGateway(options =>
        {
            configure?.Invoke(options);
            if (options.ProgrammaticRules is not null)
                throw new InvalidOperationException("Conflicting explicit rule sources.");
            options.ProgrammaticRules = () =>
            {
                var sectionValue = (ruleConfiguration as IConfigurationSection)?.Value;
                var documentValue = ruleConfiguration["RulesJson"];
                if ((sectionValue is not null && documentValue is not null) ||
                    ruleConfiguration.GetSection("Rules").GetChildren().Any())
                    throw new InvalidOperationException("Conflicting supplied rule sources.");
                return JsonElement.Parse(sectionValue ?? documentValue ??
                    throw new InvalidOperationException("A complete serialized rule document is required."));
            };
        });
    }
}
