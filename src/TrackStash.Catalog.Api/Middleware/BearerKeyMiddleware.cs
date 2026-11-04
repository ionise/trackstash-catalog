using System.Security.Cryptography;
using System.Text;

namespace TrackStash.Catalog.Api.Middleware;

internal sealed class BearerKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _keyHash;

    public BearerKeyMiddleware(RequestDelegate next, string key)
    {
        _next = next;
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && request.Path.StartsWithSegments("/admin"))
        {
            await _next(context);
            return;
        }
        var authorization = request.Headers.Authorization;
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
