using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace TrackStash.Catalog.Api.Middleware;

internal sealed class LocalAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _keyHash;

    public LocalAccessMiddleware(RequestDelegate next, string key)
    {
        _next = next;
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
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
        if (!StringValues.IsNullOrEmpty(context.Request.Headers.Origin))
        {
            await ApiErrors.WriteAsync(context, 403, "origin_not_allowed", "Browser origins are not enabled");
            return;
        }
        var authorization = context.Request.Headers.Authorization;
        var value = authorization.Count == 1 ? authorization[0] : null;
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(_keyHash,
                SHA256.HashData(Encoding.UTF8.GetBytes(value[7..]))))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ApiErrors.WriteAsync(context, 401, "unauthorized", "A valid bearer API key is required");
            return;
        }
        await _next(context);
    }
}
