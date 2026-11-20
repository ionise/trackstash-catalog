namespace TrackStash.Catalog.Api.Authentication;

internal sealed class TrustedProxyIdentity(bool enabled, string? logoutPath)
{
    internal const string HeaderName = "X-Trackstash-User";
    internal bool Enabled { get; } = enabled;
    internal string? LogoutPath { get; } = logoutPath;

    internal static TrustedProxyIdentity FromConfiguration(IConfiguration configuration, bool proxyEnabled)
    {
        var value = configuration["CatalogApi:TrustedProxyIdentity"];
        if (value is not null && !bool.TryParse(value, out _))
            throw new InvalidOperationException("CatalogApi:TrustedProxyIdentity must be true or false.");
        var enabled = bool.TryParse(value, out var parsed) && parsed;
        var logoutPath = configuration["CatalogApi:TrustedProxyLogoutPath"];
        if (!enabled)
        {
            if (logoutPath is not null)
                throw new InvalidOperationException("CatalogApi:TrustedProxyLogoutPath requires trusted proxy identity.");
            return new(false, null);
        }

        if (!proxyEnabled)
            throw new InvalidOperationException("Trusted proxy identity requires trusted HTTPS proxy mode.");
        if (string.IsNullOrEmpty(logoutPath) || logoutPath == "/" || !logoutPath.StartsWith('/')
            || logoutPath.StartsWith("//", StringComparison.Ordinal)
            || logoutPath.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~'))
            || logoutPath.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidOperationException("CatalogApi:TrustedProxyLogoutPath must be a same-origin absolute path.");
        return new(true, logoutPath);
    }

    internal string? GetUsername(HttpRequest request)
    {
        if (!Enabled || !request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1)
            return null;
        var username = values[0];
        return string.IsNullOrWhiteSpace(username) || username.Length > 256 || username != username.Trim()
            || username.Any(c => char.IsControl(c) || c == ',')
            ? null : username;
    }
}
