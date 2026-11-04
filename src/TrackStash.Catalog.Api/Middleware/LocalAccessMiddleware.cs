using System.Net;
using Microsoft.Extensions.Primitives;

namespace TrackStash.Catalog.Api.Middleware;

internal sealed class LocalAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BrowserOrigins _origins;

    public LocalAccessMiddleware(RequestDelegate next, BrowserOrigins origins)
    {
        _next = next;
        _origins = origins;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            await ApiErrors.WriteAsync(context, 403, "loopback_required", "Only loopback connections are permitted");
            return;
        }
        if (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out var address)
            || !address.IsLoopback)
        {
            await ApiErrors.WriteAsync(context, 403, "host_not_allowed", "A loopback host name is required");
            return;
        }
        var origin = context.Request.Headers.Origin;
        if (!StringValues.IsNullOrEmpty(origin) && (origin.Count != 1 || !_origins.Permits(origin.ToString(), context.Request)))
        {
            await ApiErrors.WriteAsync(context, 403, "origin_not_allowed", "This browser origin is not allowed");
            return;
        }
        await _next(context);
    }
}
