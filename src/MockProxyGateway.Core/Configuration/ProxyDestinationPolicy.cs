using System.Net;
using System.Net.Sockets;

namespace MockProxyGateway.Configuration;

internal static class ProxyDestinationPolicy
{
    internal static bool IsValid(Uri uri, GatewayOptions options)
    {
        if (!uri.IsAbsoluteUri || !uri.IsWellFormedOriginalString() || uri.OriginalString.Contains('\\') ||
            uri.OriginalString.Any(char.IsControl) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !GatewayPaths.IsSafe(uri.AbsolutePath) ||
            !options.AllowedDestinationAuthorities.Contains(uri.Authority, StringComparer.OrdinalIgnoreCase))
            return false;
        // Validate before URI normalization can erase unsafe dot segments.
        var authorityStart = uri.OriginalString.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return false;
        var rawPathStart = uri.OriginalString.IndexOf('/', authorityStart + 3);
        if (rawPathStart >= 0 && (!GatewayPaths.IsSafe(uri.OriginalString[rawPathStart..]) ||
            !GatewayPaths.IsEscapedTargetPart(uri.OriginalString[rawPathStart..], query: false)))
            return false;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !IsApproved(literal, options))
            return false;
        return uri.Scheme == Uri.UriSchemeHttps ||
            (uri.Scheme == Uri.UriSchemeHttp && options.AllowLoopbackForTests &&
                IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }

    internal static bool IsApproved(IPAddress address, GatewayOptions options)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return options.AllowLoopbackForTests;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.Parse("fd00:ec2::254")))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            if (octets[0] is 0 or >= 224 || (octets[0] == 169 && octets[1] == 254) ||
                address.Equals(IPAddress.Parse("100.100.100.200")) || address.Equals(IPAddress.Parse("168.63.129.16")))
                return false;
        }
        return options.AllowedDestinationAddresses.Any(x =>
            IPAddress.TryParse(x, out var approved) &&
            (approved.IsIPv4MappedToIPv6 ? approved.MapToIPv4() : approved).Equals(address));
    }
}
