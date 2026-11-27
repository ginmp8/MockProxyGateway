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
        // OptionsFactory executes these configure actions in registration order,
        // then host post-configure actions. Only the built-in host section bind
        // is implicit; ordinary C# assignments are explicit even if null or equal.
        services.AddOptions<GatewayOptions>()
            .Configure(options => options.BindingRulesJson = true)
            .BindConfiguration("MockProxyGateway")
            .Configure(options => options.BindingRulesJson = false);
        if (configure is not null)
            services.Configure<GatewayOptions>(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<GatewayStartupOptions>();
        services.TryAddSingleton<RuleStore>();
        services.TryAddSingleton<ProxyTransport>();
        services.TryAddSingleton<GatewayRuntime>();
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
                // A scalar section cannot also contain a child, even when the child's value is null.
                if ((sectionValue is not null && documentValue is not null) ||
                    ruleConfiguration.GetChildren().Any(x => sectionValue is not null ||
                        !string.Equals(x.Key, "RulesJson", StringComparison.OrdinalIgnoreCase)) ||
                    ruleConfiguration.GetSection("RulesJson").GetChildren().Any())
                    throw new InvalidOperationException("Conflicting supplied rule sources.");
                return JsonElement.Parse(RuleStore.EncodeDocument(sectionValue ?? documentValue ??
                    throw new InvalidOperationException("A complete serialized rule document is required.")));
            };
        });
    }
}
