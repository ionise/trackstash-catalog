using System.Net;
using System.Net.Sockets;

namespace TrackStash.Catalog.Api.Middleware;

internal sealed record ProxyAccess(string PublicOrigin, string Host, IPAddress TrustedProxyIp, IPAddress ListenAddress)
{
    public static ProxyAccess? FromConfiguration(IConfiguration configuration)
    {
        var origin = configuration["CatalogApi:PublicOrigin"];
        var proxy = configuration["CatalogApi:TrustedProxyIp"];
        var listen = configuration["CatalogApi:ListenAddress"];
        if (origin is null && proxy is null && listen is null) return null;

        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || origin != uri.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("CatalogApi:PublicOrigin must be an exact canonical HTTPS origin without a path, query, fragment or user info.");
        if (!TryAddress(proxy, allowAny: false, out var proxyIp))
            throw new InvalidOperationException("CatalogApi:TrustedProxyIp must be a single unicast IP address.");
        if (!TryAddress(listen, allowAny: true, out var listenIp))
            throw new InvalidOperationException("CatalogApi:ListenAddress must be an IP address (or an unspecified address for all interfaces).");
        if (configuration.GetSection("CatalogApi:AllowedOrigins").GetChildren().Any())
            throw new InvalidOperationException("CatalogApi:AllowedOrigins cannot be used with proxy mode.");

        return new ProxyAccess(origin, uri.Authority, proxyIp!, listenIp!);
    }

    private static bool TryAddress(string? value, bool allowAny, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrEmpty(value) || value != value.Trim()
            || !IPAddress.TryParse(value, out var parsed) || parsed.IsIPv4MappedToIPv6
            || parsed.ToString() != value
            || (parsed.AddressFamily == AddressFamily.InterNetworkV6 && parsed.ScopeId != 0))
            return false;
        var bytes = parsed.GetAddressBytes();
        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
        {
            if (!allowAny) return false;
        }
        else if (parsed.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] >= 224 || bytes[0] == 0
            : parsed.IsIPv6Multicast)
            return false;
        address = parsed;
        return true;
    }
}
