using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrackStash.Catalog.Api;
using TrackStash.Catalog.Api.Beatport;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private sealed class LookupBroker : IBeatportBrokerClient
    {
        public bool Configured => true;
        public List<(string Path, Dictionary<string, string> Query)> Requests { get; } = [];
        public Task<JsonElement?> GetCatalogAsync(string path, IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
        {
            Requests.Add((path, query.ToDictionary(p => p.Key, p => p.Value)));
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new {
                count = 1, results = new[] { new { id = 7, name = "Shared tune", mix_name = "Original Mix",
                    isrc = "GBTRK0000001", artists = new[] { new { id = 1, name = "Zed Primary" } } } }
            }));
        }
        public Task<BeatportConnectionResponse> StatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BeatportConnectionResponse> SignInAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BrokerFetchOutcome> FetchAsync(string entityType, long id, bool refresh, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task MediaProviderLookup_UsesScannerIsrcOrExplicitMetadataWithoutPathsOrWrites()
    {
        var broker = new LookupBroker();
        await using var fixture = await StartMedia(broker: broker);
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/media/1/provider-lookup/beatport", new { searchByMetadata = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Isrc", result.GetProperty("candidates")[0].GetProperty("match").GetProperty("basis").GetString());
        var first = Assert.Single(broker.Requests);
        Assert.Equal("GBTRK0000001", first.Query["isrc"]);
        Assert.DoesNotContain(first.Query.Values, value => value.Contains(fixture.Root));
        Assert.Equal(0, await ImportCount("enrichment_review"));
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.PostAsJsonAsync("/api/v1/media/1/provider-lookup/beatport",
            new { searchByMetadata = true })).StatusCode);
        var second = broker.Requests[1].Query;
        Assert.False(second.ContainsKey("isrc"));
        Assert.Equal("Shared tune (Original Mix)", second["name"]);
        Assert.Equal("Zed Primary", second["artists.name"]);
        await ScannerSql(fixture, "UPDATE metadata SET isrc='invalid'");
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.PostAsJsonAsync("/api/v1/media/1/provider-lookup/beatport",
            new { searchByMetadata = false })).StatusCode);
        Assert.False(broker.Requests[2].Query.ContainsKey("isrc"));
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/provider-lookup/beatport",
            new { searchByMetadata = false, path = "/private" }), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/provider-lookup/sonovault",
            new { searchByMetadata = false }), 503, "sonovault_not_configured");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media/provider-release/sonovault/0"), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media/provider-release/sonovault/8?editionId=bad"), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media/provider-release/sonovault/8"), 503, "sonovault_not_configured");
        Assert.Equal(3, broker.Requests.Count);
    }

    [Fact]
    public async Task RemoteProviderLookup_RejectsStaleSnapshotsAndDoesNotSendPrivateFields()
    {
        var db = _path + ".remote-lookup";
        const string deviceKey = "test-device-token-distinct-from-admin-123456";
        var broker = new LookupBroker();
        await using var app = Build(builder => {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
                ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = deviceKey
            });
            builder.Services.AddSingleton<IBeatportBrokerClient>(broker);
        });
        try
        {
            await app.StartAsync();
            using var admin = app.GetTestClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
            var item = new RemoteInventoryRecord { MediaFileId = 1, Path = "/private/secret.flac", Format = "flac",
                SizeBytes = 10, ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
                Title = "Shared tune", Artist = "Zed Primary", Isrc = "GBTRK0000001", FingerprintRaw = "private-fingerprint" };
            Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                new RemoteInventoryBatchRequest { Items = [item] })).StatusCode);
            var snapshot = Assert.Single((await admin.GetFromJsonAsync<RemoteInventoryPage>("/api/v1/remote-inventory"))!.Items);
            var key = new RemoteInventoryKey("owner", "mac", 1);
            var request = new { key, version = snapshot.Version, searchByMetadata = false };
            Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync("/api/v1/remote-inventory/provider-lookup/beatport", request)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/remote-inventory/provider-lookup/beatport", request)).StatusCode);
            Assert.DoesNotContain(broker.Requests[0].Query.Values, value => value.Contains("private"));
            await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", new RemoteInventoryBatchRequest {
                Items = [item with { ScannedUtc = item.ScannedUtc.AddSeconds(1), Title = "Changed" }] });
            await ProblemAsync(await admin.PostAsJsonAsync("/api/v1/remote-inventory/provider-lookup/beatport", request), 412, "enrichment_state_changed");
            Assert.Single(broker.Requests);
        }
        finally { File.Delete(db); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    public async Task MediaProviderRelease_InspectsSelectedSonoVaultEditionWithoutCatalogWrites(string? editionId)
    {
        var json = """
            {"id":8,"title":"Representative","edition":null,"editions":[{"id":12,"source":"discogs","external_id":"99","track_count":0}],"tracks":[]}
            """;
        var factory = new EnrichmentHttpFactory { ResponseJson = editionId is null ? json : json.Replace("\"edition\":null", "\"edition\":12") };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var revisions = await ImportCount("catalog_foundation_revision");
        var response = await client.GetAsync("/api/v1/media/provider-release/sonovault/8" +
            (editionId is null ? "" : "?editionId=12"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("/v1/releases/8" + (editionId is null ? "" : "?edition=12"), factory.LastUri!.PathAndQuery);
        var lookup = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(editionId, lookup.GetProperty("candidates")[0].GetProperty("editionId").GetString());
        var sourceVersion = lookup.GetProperty("sourceVersion").GetString();
        Assert.Equal(64, sourceVersion!.Length);
        var again = await client.GetFromJsonAsync<JsonElement>("/api/v1/media/provider-release/sonovault/8" +
            (editionId is null ? "" : "?editionId=12"));
        Assert.Equal(sourceVersion, again.GetProperty("sourceVersion").GetString());
        Assert.Equal(revisions, await ImportCount("catalog_foundation_revision"));
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/media/provider-release/sonovault/8")).StatusCode);
        Assert.Equal(2, factory.Count);
    }
}
