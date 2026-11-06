using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api.Beatport;

/// <summary>Beatport connection state as reported by the local broker.</summary>
public sealed record BeatportConnectionResponse(bool Configured, bool Reachable, bool SignedIn, string? Username,
    DateTimeOffset? SignedInUtc, long CachedResponses, long CachedEntities, long UpstreamRequests, long Throttled);

/// <summary>Outcome of fetching one Beatport entity through the broker.</summary>
public enum BrokerFetchOutcome { Fetched, NotFound }

public interface IBeatportBrokerClient
{
    bool Configured { get; }
    Task<BeatportConnectionResponse> StatusAsync(CancellationToken ct);
    Task<BeatportConnectionResponse> SignInAsync(string username, string password, CancellationToken ct);
    Task SignOutAsync(CancellationToken ct);
    Task<BrokerFetchOutcome> FetchAsync(string entityType, long id, bool refresh, CancellationToken ct);
}

/// <summary>Talks to the loopback Beatport broker; the broker owns Beatport credentials, caching and throttling.</summary>
public sealed class BeatportBrokerClient : IBeatportBrokerClient
{
    public const string HttpClientName = "beatport-broker";
    private readonly HttpClient? _http;

    public BeatportBrokerClient(IHttpClientFactory factory, IConfiguration configuration)
    {
        var url = configuration["CatalogApi:BeatportBrokerUrl"];
        var key = configuration["CatalogApi:BeatportBrokerKey"];
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(key)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttp || !baseUri.IsLoopback)
            throw new InvalidOperationException("CatalogApi:BeatportBrokerUrl must be an http loopback URL such as http://127.0.0.1:5190.");
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("CatalogApi:BeatportBrokerKey must be set (at least 32 characters) when a broker URL is configured.");
        _http = factory.CreateClient(HttpClientName);
        _http.BaseAddress = baseUri;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public bool Configured => _http is not null;

    private HttpClient Http => _http ?? throw new ApiRequestException(503, "beatport_broker_not_configured",
        "The Beatport broker is not configured. Start TrackStash with start-prod.sh or set CatalogApi:BeatportBrokerUrl and CatalogApi:BeatportBrokerKey.");

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await Http.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            throw new ApiRequestException(503, "beatport_broker_unavailable", "The Beatport broker is not running or not reachable.", inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ApiRequestException(504, "beatport_broker_timeout", "The Beatport broker did not answer in time.", inner: ex);
        }
    }

    private static async Task<Exception> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null, detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (problem.ValueKind == JsonValueKind.Object)
            {
                code = problem.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                detail = problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return (response.StatusCode, code) switch
        {
            // A 401 from the catalog means "bad catalog key", so Beatport session problems are surfaced as 409 instead.
            (_, "beatport_sign_in_required") => new ApiRequestException(409, "beatport_sign_in_required", "Sign in to Beatport first."),
            (_, "beatport_sign_in_failed") => new ApiRequestException(409, "beatport_sign_in_failed", detail ?? "Beatport rejected the sign-in."),
            (HttpStatusCode.Unauthorized, _) or (HttpStatusCode.Forbidden, _) =>
                new ApiRequestException(503, "beatport_broker_rejected", "The Beatport broker rejected the catalog's key; restart TrackStash with start-prod.sh."),
            (HttpStatusCode.TooManyRequests, _) => new ApiRequestException(429, "beatport_rate_limited", "Beatport is rate limiting requests; try again later."),
            _ => new ApiRequestException(502, code ?? "beatport_upstream_error", detail ?? $"Beatport request failed ({(int)response.StatusCode})."),
        };
    }

    public async Task<BeatportConnectionResponse> StatusAsync(CancellationToken ct)
    {
        if (!Configured) return new(false, false, false, null, null, 0, 0, 0, 0);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/broker/status");
            using var response = await SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct);
            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return new(true, true, status.TryGetProperty("signedIn", out var s) && s.ValueKind == JsonValueKind.True,
                status.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                status.TryGetProperty("signedInUtc", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var when) ? when : null,
                Number(status, "cachedResponses"), Number(status, "cachedEntities"), Number(status, "upstreamRequests"), Number(status, "throttled"));
        }
        catch (ApiRequestException ex) when (ex.Code == "beatport_broker_unavailable")
        {
            return new(true, false, false, null, null, 0, 0, 0, 0);
        }
    }

    public async Task<BeatportConnectionResponse> SignInAsync(string username, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/broker/sign-in") { Content = JsonContent.Create(new { username, password }) };
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct);
        return await StatusAsync(ct);
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/broker/sign-out");
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct);
    }

    public async Task<BrokerFetchOutcome> FetchAsync(string entityType, long id, bool refresh, CancellationToken ct)
    {
        if (entityType is not ("tracks" or "releases") || id <= 0) throw new ArgumentOutOfRangeException(nameof(entityType));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v4/catalog/{entityType}/{id}/");
        if (refresh) request.Headers.Add("X-Broker-Refresh", "1");
        using var response = await SendAsync(request, ct);
        // Beatport's own 404 is passed through as JSON; broker refusals are problem+json and are errors.
        if (response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType != "application/problem+json")
            return BrokerFetchOutcome.NotFound;
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct);
        return BrokerFetchOutcome.Fetched;
    }
}
