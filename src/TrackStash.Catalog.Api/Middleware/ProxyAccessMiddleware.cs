namespace TrackStash.Catalog.Api.Middleware;

internal sealed class ProxyAccessMiddleware(RequestDelegate next, ProxyAccess proxy)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).Equals(proxy.TrustedProxyIp))
        {
            await ApiErrors.WriteAsync(context, 403, "proxy_required", "Only the trusted proxy may connect");
            return;
        }
        if (!string.Equals(context.Request.Host.Value, proxy.Host, StringComparison.Ordinal))
        {
            await ApiErrors.WriteAsync(context, 403, "host_not_allowed", "The configured public host is required");
            return;
        }
        var scheme = context.Request.Headers["X-Forwarded-Proto"];
        if (scheme.Count != 1 || scheme[0] != "https")
        {
            await ApiErrors.WriteAsync(context, 403, "https_required", "The trusted proxy must report HTTPS");
            return;
        }
        var headers = context.Request.Headers;
        var requiresOrigin = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method);
        if ((requiresOrigin || headers.ContainsKey("Origin"))
            && (headers.Origin.Count != 1 || headers.Origin[0] != proxy.PublicOrigin))
        {
            await ApiErrors.WriteAsync(context, 403, "origin_not_allowed", "This browser origin is not allowed");
            return;
        }
        context.Request.Scheme = "https";
        await next(context);
    }
}
