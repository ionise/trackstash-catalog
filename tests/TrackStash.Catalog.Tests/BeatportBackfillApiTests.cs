using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using TrackStash.Catalog.Api.Beatport;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private const string BrokerKey = "test-broker-key-not-a-real-secret-0123456789";

    /// <summary>Simulates the broker: successful fetches are written to beatport_broker_entity, as the real broker does.</summary>
    private sealed class FakeBroker(string scannerPath) : HttpMessageHandler
    {
        public Dictionary<string, string> Payloads { get; } = [];
        /// <summary>Canned answers for list and detail URLs, keyed by path plus query exactly as the catalog sends them.</summary>
        public Dictionary<string, string> Pages { get; } = [];
        public List<string> Requests { get; } = [];
        public bool SignedIn { get; set; } = true;
        public string? LastSignInBody { get; private set; }

        private static HttpResponseMessage Json(HttpStatusCode status, string body, string type = "application/json") =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer " + BrokerKey, request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}{request.RequestUri.Query}{(request.Headers.Contains("X-Broker-Refresh") ? " refresh" : "")}");
            switch (path)
            {
                case "/broker/status":
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { signedIn = SignedIn, username = SignedIn ? "dj" : null, cachedEntities = Payloads.Count }));
                case "/broker/sign-in":
                    LastSignInBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    if (LastSignInBody.Contains("\"bad\"")) return Json(HttpStatusCode.Unauthorized, """{"code":"beatport_sign_in_failed","detail":"Beatport rejected the username or password."}""", "application/problem+json");
                    SignedIn = true;
                    return Json(HttpStatusCode.OK, "{}");
                case "/broker/sign-out":
                    SignedIn = false;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (Pages.Count > 0)
            {
                if (!SignedIn) return Json(HttpStatusCode.Unauthorized, """{"code":"beatport_sign_in_required"}""", "application/problem+json");
                return Pages.TryGetValue(path + request.RequestUri.Query, out var page) ? Json(HttpStatusCode.OK, page)
                    : Json(HttpStatusCode.NotFound, """{"detail":"Not found."}""");
            }
            var match = Regex.Match(path, "^/v4/catalog/(tracks|releases)/([0-9]+)/$");
            Assert.True(match.Success, path);
            if (!SignedIn) return Json(HttpStatusCode.Unauthorized, """{"code":"beatport_sign_in_required"}""", "application/problem+json");
            var type = match.Groups[1].Value.TrimEnd('s');
            if (!Payloads.TryGetValue($"{type}:{match.Groups[2].Value}", out var payload)) return Json(HttpStatusCode.NotFound, """{"detail":"Not found."}""");
            await using var connection = new SqliteConnection($"Data Source={scannerPath};Pooling=False");
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS beatport_broker_entity (entity_type TEXT NOT NULL, entity_id INTEGER NOT NULL, payload_json TEXT NOT NULL,
                    fetched_utc TEXT NOT NULL, source_request_key TEXT NOT NULL, PRIMARY KEY (entity_type, entity_id));
                INSERT OR REPLACE INTO beatport_broker_entity VALUES (@type, @id, @payload, @at, @key);
                """;
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@id", long.Parse(match.Groups[2].Value));
            command.Parameters.AddWithValue("@payload", payload);
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@key", path);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Json(HttpStatusCode.OK, payload);
        }
    }

    private async Task<(WebApplication App, HttpClient Client, FakeBroker Broker)> StartWithBrokerAsync(bool configured = true, TimeProvider? clock = null)
    {
        var broker = new FakeBroker(PurchaseSourcePath);
        var app = Build(b =>
        {
            if (clock is not null) b.Services.AddSingleton(clock);
            var settings = new Dictionary<string, string?> { ["CatalogApi:PurchaseSourceDatabasePath"] = PurchaseSourcePath };
            if (configured)
            {
                settings["CatalogApi:BeatportBrokerUrl"] = "http://127.0.0.1:5190";
                settings["CatalogApi:BeatportBrokerKey"] = BrokerKey;
            }
            b.Configuration.AddInMemoryCollection(settings);
            b.Services.PostConfigure<HttpClientFactoryOptions>(BeatportBrokerClient.HttpClientName,
                o => o.HttpMessageHandlerBuilderActions.Add(h => h.PrimaryHandler = broker));
        });
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        return (app, client, broker);
    }

    private async Task MakeTrack102MissingAsync() =>
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json = @tracks", ("@tracks", $"[{TrackPayload()}]"));

    [Fact]
    public async Task BeatportBackfill_FetchesOnlyMissingTracksAndMakesTheReleaseReady()
    {
        await CreatePurchaseSource();
        await MakeTrack102MissingAsync();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Payloads["track:102"] = TrackPayload(102);
        await using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO label (label_id, name) VALUES ('source-label', 'Test label');
                INSERT INTO label_external_ref (label_id, source, external_id) VALUES ('source-label', 'beatport', '42');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var catalogHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(_path));

        var ready = await client.GetFromJsonAsync<JsonElement>("/api/v1/purchase-release-candidates?readyOnly=true");
        Assert.Equal(0, ready.GetProperty("total").GetInt32());

        var response = await client.PostAsync("/api/v1/purchase-release-candidates/10/beatport-backfill", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BeatportBackfillResult>())!;
        Assert.False(result.SourceCompleteBefore);
        Assert.True(result.SourceCompleteAfter, string.Join(",", result.RemainingBlockers));
        Assert.Equal((1, 1, 0, false), (result.TracksRequested, result.TracksFetched, result.TracksNotFound, result.ReleaseRefreshed));
        Assert.Equal(["GET /v4/catalog/tracks/102/"], broker.Requests);
        Assert.Equal(catalogHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(_path)));

        var readyAfter = await client.GetFromJsonAsync<JsonElement>("/api/v1/purchase-release-candidates?readyOnly=true");
        Assert.Equal(10, readyAfter.GetProperty("items")[0].GetProperty("releaseId").GetInt64());
    }

    [Fact]
    public async Task BeatportBackfill_RefreshesAMissingReleasePayloadBeforeItsTracks()
    {
        await CreatePurchaseSource();
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json = '[]'");
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Payloads["release:11"] = ReleasePayload(id: 11, tracks: new[] { "https://api.beatport.com/v4/catalog/tracks/111/" }, count: 1);
        broker.Payloads["track:111"] = TrackPayload(111, releaseId: 11);

        var result = (await (await client.PostAsync("/api/v1/purchase-release-candidates/11/beatport-backfill", null))
            .Content.ReadFromJsonAsync<BeatportBackfillResult>())!;
        Assert.True(result.ReleaseRefreshed);
        Assert.True(result.SourceCompleteAfter, string.Join(",", result.RemainingBlockers));
        Assert.Equal(["GET /v4/catalog/releases/11/ refresh", "GET /v4/catalog/tracks/111/"], broker.Requests);
    }

    [Fact]
    public async Task BeatportBackfill_ReportsTracksBeatportNoLongerListsAndSurfacesSessionProblems()
    {
        await CreatePurchaseSource();
        await MakeTrack102MissingAsync();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;

        var result = (await (await client.PostAsync("/api/v1/purchase-release-candidates/10/beatport-backfill", null))
            .Content.ReadFromJsonAsync<BeatportBackfillResult>())!;
        Assert.Equal(1, result.TracksNotFound);
        Assert.Contains("track_payload_missing", result.RemainingBlockers);

        broker.SignedIn = false;
        await ProblemAsync(await client.PostAsync("/api/v1/purchase-release-candidates/10/beatport-backfill", null), 409, "beatport_sign_in_required");
        await ProblemAsync(await client.PostAsync("/api/v1/purchase-release-candidates/12/beatport-backfill", null), 404, "purchase_candidate_not_found");
        await ProblemAsync(await client.PostAsync("/api/v1/purchase-release-candidates/x/beatport-backfill", null), 400, "validation_failed");
    }

    [Fact]
    public async Task BeatportConnection_SignInForwardsCredentialsAndMapsFailures()
    {
        await CreatePurchaseSource();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.SignedIn = false;

        var status = await client.GetFromJsonAsync<BeatportConnectionResponse>("/api/v1/beatport/connection");
        Assert.True(status!.Configured); Assert.True(status.Reachable); Assert.False(status.SignedIn);

        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/beatport/sign-in", new { username = "dj", password = "bad" }), 409, "beatport_sign_in_failed");
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/beatport/sign-in", new { username = "dj" }), 400, "validation_failed");
        var signedIn = await client.PostAsJsonAsync("/api/v1/beatport/sign-in", new { username = "dj", password = "good-password" });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.True((await signedIn.Content.ReadFromJsonAsync<BeatportConnectionResponse>())!.SignedIn);
        Assert.Contains("good-password", broker.LastSignInBody);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/beatport/sign-out", null)).StatusCode);
        Assert.False(broker.SignedIn);

        using var anonymous = app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync("/api/v1/beatport/connection"), 401, "unauthorized");
    }

    [Fact]
    public async Task BeatportBackfill_UnconfiguredBrokerIsReportedNotFatal()
    {
        await CreatePurchaseSource();
        var (app, client, broker) = await StartWithBrokerAsync(configured: false);
        await using var _ = app;
        var status = await client.GetFromJsonAsync<BeatportConnectionResponse>("/api/v1/beatport/connection");
        Assert.False(status!.Configured);
        await ProblemAsync(await client.PostAsync("/api/v1/purchase-release-candidates/10/beatport-backfill", null), 503, "beatport_broker_not_configured");
        await ProblemAsync(await client.PostAsync("/api/v1/beatport-backfill-jobs", null), 503, "beatport_broker_not_configured");
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task BeatportBackfillJob_ProcessesEveryIncompleteReleaseAndReportsProgress()
    {
        await CreatePurchaseSource();
        await MakeTrack102MissingAsync();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Payloads["track:102"] = TrackPayload(102);
        broker.Payloads["release:11"] = ReleasePayload(id: 11, tracks: new[] { "https://api.beatport.com/v4/catalog/tracks/111/" }, count: 1);
        broker.Payloads["track:111"] = TrackPayload(111, releaseId: 11);

        var start = await client.PostAsync("/api/v1/beatport-backfill-jobs", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        BeatportBackfillJobResponse job;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do
        {
            await Task.Delay(50);
            job = (await client.GetFromJsonAsync<BeatportBackfillJobResponse>(location))!;
        } while (job.Status is "queued" or "running" && DateTime.UtcNow < deadline);

        Assert.Equal("completed", job.Status);
        Assert.Equal((2, 2, 2, 2), (job.TotalReleases, job.ProcessedReleases, job.CompletedReleases, job.TracksFetched));
        Assert.Empty(job.StillIncomplete!);
        await ProblemAsync(await client.GetAsync("/api/v1/beatport-backfill-jobs/unknown"), 404, "beatport_backfill_job_not_found");
    }
}
