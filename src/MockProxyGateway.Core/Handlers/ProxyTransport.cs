using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.WebUtilities;
using MockProxyGateway.Matching;
using Microsoft.Extensions.Options;
using MockProxyGateway.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MockProxyGateway.Handlers;

internal sealed class ProxyTransport : IDisposable
{
    private readonly GatewayOptions _options;
    private readonly HttpMessageInvoker _client;
    private readonly ServiceProvider _forwarderServices;
    private readonly IHttpForwarder _forwarder;

    public ProxyTransport(IOptions<GatewayOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        // The pinned YARP forwarder logs complete target URLs (including query strings).
        // Isolate only this adapter in a private, gateway-owned provider. Never change host logging,
        // nor reuse a host forwarder with its potentially different diagnostic or transport policy.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(clock);
        services.AddHttpForwarder();
        _forwarderServices = services.BuildServiceProvider();
        _forwarder = _forwarderServices.GetRequiredService<IHttpForwarder>();
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            // Keep incoming trace headers as ordinary application data; never inject
            // the gateway's ambient activity or baggage into an upstream request.
            ActivityHeadersPropagator = null,
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = ConnectAsync
        });
    }

    internal async Task<bool> ForwardAsync(HttpContext context, Destination destination, string path)
    {
        if (!ProxyDestinationPolicy.IsValid(destination.BaseUri, _options))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return false;
        }
        var target = BuildTarget(context, destination, path);
        var configuration = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(_options.ProxyTimeoutSeconds) };
        var error = await _forwarder.SendAsync(context, destination.BaseUri.AbsoluteUri, _client, configuration,
            new GatewayTransformer(target)).ConfigureAwait(false);
        // YARP handles transport failures. Never resolve or execute another action.
        return error == ForwarderError.None;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var literalLoopback = IPAddress.TryParse(context.DnsEndPoint.Host.Trim('[', ']'), out var literal) &&
            IPAddress.IsLoopback(literal);
        if (addresses.Length == 0 || addresses.Any(x => !ProxyDestinationPolicy.IsApproved(x, _options) ||
            (IPAddress.IsLoopback(x.IsIPv4MappedToIPv6 ? x.MapToIPv4() : x) && !literalLoopback)))
            throw new HttpRequestException("Gateway destination address was not approved.");
        var address = addresses[0];
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _forwarderServices.Dispose();
    }

    private static Uri BuildTarget(HttpContext context, Destination destination, string path)
    {
        var query = context.Request.QueryString.Value ?? "";
        if (destination.RemoveQuery.Length > 0 && query.Length > 1)
        {
            var fields = query[1..].Split('&');
            var retained = fields.Where(field => !QueryHelpers.ParseQuery("?" + field).Keys
                .Any(key => destination.RemoveQuery.Contains(key, StringComparer.OrdinalIgnoreCase)));
            var filtered = string.Join('&', retained);
            query = filtered.Length == 0 ? "" : "?" + filtered;
        }
        if (!GatewayPaths.IsSafe(path) || !GatewayPaths.IsEscapedTargetPart(path, query: false) ||
            !GatewayPaths.IsEscapedTargetPart(query, query: true))
            throw new RequestFailure(400);
        // The authority and base path are exclusively from the validated destination, never the request.
        var target = destination.BaseUri.GetLeftPart(UriPartial.Authority) +
            destination.BaseUri.AbsolutePath.TrimEnd('/') + path + query;
        return new Uri(target,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    private sealed class GatewayTransformer(Uri target) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            // Preserve every end-to-end header using YARP's native protocol handling.
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);
            // The outbound Host and URI must refer to the registered destination.
            proxyRequest.Headers.Host = null;
            proxyRequest.RequestUri = target;
        }
    }
}
