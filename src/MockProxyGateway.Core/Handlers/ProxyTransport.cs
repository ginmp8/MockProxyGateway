using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
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

    public ProxyTransport(IOptions<GatewayOptions> options)
    {
        _options = options.Value;
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = ConnectAsync
        });
    }

    internal async Task<bool> ForwardAsync(HttpContext context, IHttpForwarder forwarder, Destination destination, string path)
    {
        if (!ProxyDestinationPolicy.IsValid(destination.BaseUri, _options))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return false;
        }
        var configuration = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(_options.ProxyTimeoutSeconds) };
        var error = await forwarder.SendAsync(context, destination.BaseUri.AbsoluteUri, _client, configuration,
            new GatewayTransformer(destination, path, _options.CredentialHeaderNames)).ConfigureAwait(false);
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

    public void Dispose() => _client.Dispose();

    private sealed class GatewayTransformer(Destination destination, string path, string[] credentialHeaderNames) : HttpTransformer
    {
        private static readonly string[] CredentialHeaders = ["Authorization", "Cookie", "Proxy-Authorization", "X-Api-Key", "Api-Key", "X-Auth-Token"];

        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);
            proxyRequest.Headers.Host = null;
            foreach (var name in proxyRequest.Headers.Select(x => x.Key).ToArray())
            {
                if (name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Forwarded", StringComparison.OrdinalIgnoreCase) ||
                    ((CredentialHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) || credentialHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase)) &&
                        !destination.ForwardCredentials.Contains(name, StringComparer.OrdinalIgnoreCase)) ||
                    destination.RemoveHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    proxyRequest.Headers.Remove(name);
            }
            foreach (var name in destination.RemoveHeaders)
                proxyRequest.Content?.Headers.Remove(name);
            var query = httpContext.Request.QueryString.Value ?? "";
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
            proxyRequest.RequestUri = new Uri(target,
                new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        }
    }
}
