using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MockProxyGateway.Configuration;
using MockProxyGateway.Handlers;
using MockProxyGateway.Matching;
using Yarp.ReverseProxy.Forwarder;

namespace MockProxyGateway.Middleware;

internal sealed partial class GatewayRuntime : IDisposable
{
    private readonly RuleStore _store;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _clock;
    private readonly ProxyTransport _transport;
    private readonly ILogger<GatewayRuntime> _logger;
    private readonly SemaphoreSlim _concurrency;

    public GatewayRuntime(RuleStore store, IOptions<GatewayOptions> options, TimeProvider clock,
        ProxyTransport transport, ILogger<GatewayRuntime> logger)
    {
        _store = store;
        _options = options.Value;
        _clock = clock;
        _transport = transport;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentRequests);
    }

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next, PathString prefix)
    {
        if (!context.Request.Path.StartsWithSegments(prefix, StringComparison.Ordinal, out var remaining))
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        if (!remaining.HasValue || remaining.Value == "/")
        {
            context.Response.StatusCode = 404;
            return;
        }
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var rawPath = raw?.Split('?', 2)[0];
        if (!GatewayPaths.IsSafe(remaining.Value) || (!string.IsNullOrEmpty(rawPath) && !GatewayPaths.IsSafe(rawPath)))
        {
            context.Response.StatusCode = 400;
            return;
        }
        // Tunnels and upgraded protocols are deliberately outside the HTTP v1 contract.
        if (string.Equals(context.Request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers.ContainsKey("Upgrade"))
        {
            context.Response.StatusCode = 400;
            return;
        }
        if (!_concurrency.Wait(0))
        {
            context.Response.StatusCode = 503;
            return;
        }
        var started = Stopwatch.GetTimestamp();
        var snapshot = _store.Current;
        var alias = "unknown";
        var ruleId = "none";
        var action = "none";
        try
        {
            if (!await AuthorizeAsync(context).ConfigureAwait(false))
                return;
            var remainder = remaining.Value!;
            var split = remainder.IndexOf('/', 1);
            var requestedAlias = split < 0 ? remainder[1..] : remainder[1..split];
            var path = split < 0 ? "/" : remainder[split..];
            if (!GatewayPaths.Identifier().IsMatch(requestedAlias) ||
                !snapshot.Services.TryGetValue(requestedAlias, out var service) || !service.Enabled)
            {
                context.Response.StatusCode = 404;
                return;
            }
            alias = requestedAlias;
            var selected = await new RuleResolver(context, _options, _clock.GetUtcNow()).ResolveAsync(service, path).ConfigureAwait(false);
            ruleId = selected.RuleId;
            if (selected.Action is null)
            {
                context.Response.StatusCode = 404;
                return;
            }
            action = selected.Action.Mode;
            if (action == "mock")
                await MockAsync(context, selected.Action.Response!).ConfigureAwait(false);
            else
                await _transport.ForwardAsync(context, context.RequestServices.GetRequiredService<IHttpForwarder>(),
                    snapshot.Destinations[selected.Action.Destination!], selected.Action.PathRewrite ?? path).ConfigureAwait(false);
        }
        catch (RequestFailure failure)
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = failure.StatusCode;
            else
                context.Abort();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or System.Text.Json.JsonException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 500;
            }
            else
                context.Abort();
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Information))
                Completed(_logger, alias, ruleId, action, context.Response.StatusCode, snapshot.Version,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.TraceIdentifier);
            _concurrency.Release();
        }
    }

    private async Task<bool> AuthorizeAsync(HttpContext context)
    {
        if (_options.AuthorizationPolicy is null)
            return true;
        var provider = context.RequestServices.GetService<IAuthorizationPolicyProvider>();
        var evaluator = context.RequestServices.GetService<IPolicyEvaluator>();
        var policy = provider is null ? null : await provider.GetPolicyAsync(_options.AuthorizationPolicy).ConfigureAwait(false);
        if (policy is null || evaluator is null)
        {
            context.Response.StatusCode = 403;
            return false;
        }
        var authentication = await evaluator.AuthenticateAsync(policy, context).ConfigureAwait(false);
        var result = await evaluator.AuthorizeAsync(policy, authentication, context, context).ConfigureAwait(false);
        if (result.Succeeded)
            return true;
        context.Response.StatusCode = result.Challenged ? 401 : 403;
        return false;
    }

    private static async Task MockAsync(HttpContext context, MockResponse response)
    {
        if (response.DelayMs > 0)
            await Task.Delay(response.DelayMs, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value;
        if (response.Body is null)
            return;
        context.Response.ContentType ??= response.ContentType;
        context.Response.ContentLength = response.Body.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    public void Dispose() => _concurrency.Dispose();

    [LoggerMessage(EventId = 200, Level = LogLevel.Information,
        Message = "Gateway service={Service} rule={Rule} action={Action} status={Status} configuration={Version} durationMs={Duration} correlation={Correlation}")]
    private static partial void Completed(ILogger logger, string service, string rule, string action, int status, long version, double duration, string correlation);
}
