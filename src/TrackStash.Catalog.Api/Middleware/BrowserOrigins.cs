namespace TrackStash.Catalog.Api.Middleware;

internal sealed class BrowserOrigins
{
    public string[] Allowed { get; }

    public BrowserOrigins(string[] allowed)
    {
        foreach (var origin in allowed)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || !uri.IsLoopback
                || uri.UserInfo.Length != 0 || origin != uri.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("CatalogApi:AllowedOrigins entries must be exact HTTP(S) loopback origins without paths.");
        Allowed = allowed;
    }

    public bool Permits(string origin, HttpRequest request)
        => origin == $"{request.Scheme}://{request.Host}"
            || Allowed.Contains(origin, StringComparer.Ordinal);
}
