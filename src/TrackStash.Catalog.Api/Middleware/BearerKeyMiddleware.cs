using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using TrackStash.Catalog.Api.Authentication;

namespace TrackStash.Catalog.Api.Middleware;

internal sealed class BearerKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[]? _keyHash;

    public BearerKeyMiddleware(RequestDelegate next, string key)
    {
        _next = next;
        _keyHash = key.Length == 0 ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    public async Task InvokeAsync(HttpContext context, IAdminSessions sessions, TrustedProxyIdentity proxyIdentity, RemoteInventorySettings remoteInventory)
    {
        var request = context.Request;
        if (context.GetEndpoint()?.Metadata.GetMetadata<DeviceInventoryEndpoint>() is not null)
        {
            if (remoteInventory.Authenticate(request))
            {
                var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 1024 * 1024;
                await _next(context);
            }
            else await ApiErrors.WriteAsync(context, 401, "unauthorized", "A valid device credential is required");
            return;
        }
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && request.Path.StartsWithSegments("/admin"))
        {
            await _next(context);
            return;
        }
        var hasAuthorization = request.Headers.ContainsKey("Authorization");
        var safeMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method);
        var sessionEndpoint = context.GetEndpoint()?.Metadata.GetMetadata<SessionEndpoint>() is not null;
        var cookieAuthenticated = !hasAuthorization && !sessionEndpoint && sessions.IsAuthenticated(request);
        var proxyAuthenticated = !hasAuthorization && !sessionEndpoint && proxyIdentity.GetUsername(request) is not null;
        if ((sessionEndpoint || cookieAuthenticated || proxyAuthenticated)
            && !safeMethod && !await RequireSameOriginAsync(context)) return;

        if (hasAuthorization ? HasValidKey(request) : sessionEndpoint || cookieAuthenticated || proxyAuthenticated)
        {
            await _next(context);
            return;
        }

        if (_keyHash is not null) context.Response.Headers.WWWAuthenticate = "Bearer";
        await ApiErrors.WriteAsync(context, 401, "unauthorized",
            sessions.Available ? "Authentication is required" : "A valid bearer API key is required");
    }

    private bool HasValidKey(HttpRequest request)
    {
        var authorization = request.Headers.Authorization;
        var value = authorization.Count == 1 ? authorization[0] : null;
        return _keyHash is not null && value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(_keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(value[7..])));
    }

    private static async Task<bool> RequireSameOriginAsync(HttpContext context)
    {
        var request = context.Request;
        var origin = request.Headers.Origin;
        // The access middleware has already checked the peer, host and scheme. Dev CORS origins
        // are for API-key clients only, not for establishing or writing with ambient credentials.
        if (origin.Count == 1
            && Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var address)
            && origin[0] == address.GetLeftPart(UriPartial.Authority))
            return true;
        await ApiErrors.WriteAsync(context, 403, "origin_not_allowed", "This browser origin is not allowed");
        return false;
    }
}
