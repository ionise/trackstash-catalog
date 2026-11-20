using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace TrackStash.Catalog.Api.Authentication;

internal interface IAdminSessions
{
    bool Available { get; }
    bool IsAuthenticated(HttpRequest request);
    int ReserveLoginAttempt();
    bool VerifyCredentials(string username, string password);
    void SignIn(HttpContext context);
    void SignOut(HttpContext context);
}

internal sealed class AdminSessions(AdminCredentials? credentials, TimeProvider clock, bool viaProxy) : IAdminSessions
{
    internal const string LocalCookieName = "TrackStash.Catalog.Session";
    internal const string ProxyCookieName = "__Host-TrackStash.Catalog.Session";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<long> _attempts = new();

    public bool Available => credentials is not null;
    private string CookieName => viaProxy ? ProxyCookieName : LocalCookieName;

    public bool IsAuthenticated(HttpRequest request)
    {
        if (!Available || GetSessionId(request) is not { } id) return false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var issued)) return false;
            if (clock.GetElapsedTime(issued) < Lifetime) return true;
            _sessions.Remove(id);
            return false;
        }
    }

    public int ReserveLoginAttempt()
    {
        lock (_gate)
        {
            // One administrator and no trusted client-IP header: bound work globally.
            var now = clock.GetTimestamp();
            while (_attempts.TryPeek(out var oldest) && clock.GetElapsedTime(oldest, now) >= AttemptWindow)
                _attempts.Dequeue();
            if (_attempts.Count >= 5)
                return Math.Max(1, (int)Math.Ceiling((AttemptWindow - clock.GetElapsedTime(_attempts.Peek(), now)).TotalSeconds));
            _attempts.Enqueue(now);
            return 0;
        }
    }

    public bool VerifyCredentials(string username, string password) => credentials?.Verify(username, password) == true;

    public void SignIn(HttpContext context)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        var previousId = GetSessionId(context.Request);
        lock (_gate)
        {
            if (previousId is not null) _sessions.Remove(previousId);
            var now = clock.GetTimestamp();
            foreach (var expired in _sessions.Where(s => clock.GetElapsedTime(s.Value, now) >= Lifetime).ToArray())
                _sessions.Remove(expired.Key);
            if (_sessions.Count >= 128)
                _sessions.Remove(_sessions.MinBy(s => s.Value).Key);
            _sessions.Add(HashToken(token), now);
        }
        context.Response.Cookies.Append(CookieName, WebEncoders.Base64UrlEncode(token), CookieOptions(context));
    }

    public void SignOut(HttpContext context)
    {
        if (GetSessionId(context.Request) is { } id)
            lock (_gate) _sessions.Remove(id);
        context.Response.Cookies.Delete(CookieName, CookieOptions(context));
    }

    private string? GetSessionId(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var value) || value.Length != 43) return null;
        Span<byte> token = stackalloc byte[32];
        return Convert.TryFromBase64String(value.Replace('-', '+').Replace('_', '/') + "=", token, out var length)
            && length == token.Length && WebEncoders.Base64UrlEncode(token.ToArray()) == value ? HashToken(token) : null;
    }

    private static string HashToken(ReadOnlySpan<byte> token) => Convert.ToHexString(SHA256.HashData(token));

    private CookieOptions CookieOptions(HttpContext context) => new()
    {
        Path = "/", HttpOnly = true, Secure = viaProxy || context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        IsEssential = true,
    };
}
