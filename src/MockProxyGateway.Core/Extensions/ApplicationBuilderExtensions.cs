using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using MockProxyGateway;
using MockProxyGateway.Configuration;
using MockProxyGateway.Middleware;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Explicit activation of the incoming HTTP gateway.</summary>
public static class MockProxyGatewayApplicationBuilderExtensions
{
    /// <summary>Install the gateway with the configured prefix or an explicit override.</summary>
    public static IApplicationBuilder UseMockProxyGateway(this IApplicationBuilder app, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ApplicationServices.GetRequiredService<GatewayStartupOptions>().Value;
        if (!options.Enabled)
            return app;
        var env = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        if (env.IsProduction() ||
            !(options.AllowedEnvironments ?? ["Development"]).Contains(env.EnvironmentName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gateway activation is not permitted in this environment.");
        var path = prefix ?? options.Prefix;
        if (!GatewayPaths.IsSafe(path) || !GatewayPaths.IsEscapedTargetPart(path, query: false) ||
            path == "/" || path.EndsWith('/') || path.Contains('%'))
            throw new InvalidOperationException("Invalid gateway prefix.");
        if (options.MaxBodySizeBytes is < 1 or > 16_777_216 ||
            options.MaxConcurrentRequests is < 1 or > 1024 || options.ProxyTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Invalid gateway resource limits.");
        if (options.AllowedJsonMediaTypes.Length > 64 ||
            options.AllowedJsonMediaTypes.Any(value =>
                string.IsNullOrEmpty(value) || value.Length > 256 || value.Contains('*') ||
                !value.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
                !MediaTypeHeaderValue.TryParse(value, out var parsed) ||
                !string.Equals(parsed.MediaType.Value, value, StringComparison.Ordinal) ||
                parsed.Parameters.Count != 0))
            throw new InvalidOperationException("Invalid additional JSON media types.");
        var runtime = app.ApplicationServices.GetRequiredService<GatewayRuntime>();
        return app.Use(next => context => runtime.InvokeAsync(context, next, new PathString(path)));
    }
}
