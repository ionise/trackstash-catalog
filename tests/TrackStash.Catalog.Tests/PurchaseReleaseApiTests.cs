using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using TrackStash.Catalog.Application;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private string PurchaseSourcePath => _path + ".scanner";
    private static readonly JsonSerializerOptions SourceJson = new(JsonSerializerDefaults.Web);

    private static string ReleasePayload(object? tracks = null, long id = 10, int count = 2) =>
        JsonSerializer.Serialize(new
        {
            id, name = "Test release", label = new { id = 42, name = "Test label" }, catalog_number = "CAT001",
            upc = "012345", publish_date = "2020-01-01", new_release_date = "2020-01-02", track_count = count,
            artists = new[] { new { id = 1, name = "Kasey Taylor" }, new { id = 2, name = "Amega" } },
            remixers = new[] { new { id = 3, name = "GMJ & Matter" } },
            tracks = tracks ?? new[] { "https://api.beatport.com/v4/catalog/tracks/101/", "https://api.beatport.com/v4/catalog/tracks/102/" },
            price = "PRIVATE_PRICE_SENTINEL",
        }, SourceJson);
    private static string TrackPayload(long id = 101, long releaseId = 10, long labelId = 42) =>
        JsonSerializer.Serialize(new
        {
            id, name = "Track", mix_name = id == 101 ? "Original Mix" : "Remix", isrc = "TEST001", length_ms = 123456,
            release = new { id = releaseId, label = new { id = labelId, name = "Test label" } },
            artists = new[] { new { id = 1, name = "Kasey Taylor" }, new { id = 2, name = "Amega" } },
            remixers = id == 101 ? Array.Empty<object>() : [new { id = 3, name = "GMJ & Matter" }],
            price = "PRIVATE_PRICE_SENTINEL", invoice = "PRIVATE_INVOICE_SENTINEL",
        }, SourceJson);

    private async Task SourceSql(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new SqliteConnection($"Data Source={PurchaseSourcePath};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreatePurchaseSource()
    {
        await SourceSql("""
            CREATE TABLE bpc_release (beatport_release_id INTEGER PRIMARY KEY, title TEXT NOT NULL, in_purchase_corpus INTEGER NOT NULL);
            CREATE TABLE beatport_release_cache (beatport_release_id INTEGER PRIMARY KEY, release_json TEXT NOT NULL, fetched_utc TEXT NOT NULL);
            CREATE TABLE beatport_isrc_lookup (normalized_isrc TEXT PRIMARY KEY, lookup_status TEXT NOT NULL, tracks_json TEXT NOT NULL, searched_utc TEXT NOT NULL);
            INSERT INTO bpc_release VALUES (10, 'Test release', 1), (11, 'Other release', 1), (12, 'Not purchased', 0);
            INSERT INTO beatport_release_cache VALUES (10, @release, '2026-11-05T00:00:00Z');
            INSERT INTO beatport_isrc_lookup VALUES ('TEST001', 'Success', @tracks, '2026-11-05T00:00:00Z');
            """, ("@release", ReleasePayload()), ("@tracks", $"[{TrackPayload()},{TrackPayload(102)}]"));
    }

    [Fact]
    public async Task PurchaseInspector_ReadsFullBundlePagesCreditsLabelCandidatesWithoutWritesOrPrivateFields()
    {
        await CreatePurchaseSource();
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO label (label_id, name) VALUES ('source-label', 'Test label');
                INSERT INTO label_external_ref (label_id, source, external_id) VALUES ('source-label', 'beatport', '42');
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var app = Build(b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:PurchaseSourceDatabasePath"] = PurchaseSourcePath }));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(PurchaseSourcePath));
        var catalogHash = SHA256.HashData(await File.ReadAllBytesAsync(_path));
        var queueResponse = await client.GetAsync("/api/v1/purchase-release-candidates?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
        Assert.True(queueResponse.Headers.CacheControl!.NoStore);
        var queue = (await queueResponse.Content.ReadFromJsonAsync<PurchaseSourcePage<PurchaseReleaseCandidate>>())!;
        Assert.Equal(2, queue.Total); Assert.Single(queue.Items);
        var first = (await client.GetFromJsonAsync<PurchaseReleaseInspectorResponse>("/api/v1/purchase-release-candidates/10?pageSize=1"))!;
        var second = (await client.GetFromJsonAsync<PurchaseReleaseInspectorResponse>("/api/v1/purchase-release-candidates/10?pageSize=1&page=2"))!;
        Assert.True(first.Source.SourceComplete);
        Assert.Equal(2, first.Source.HydratedTrackCount);
        Assert.Equal(2, first.Source.Tracks.Total);
        Assert.Equal(101, Assert.Single(first.Source.Tracks.Items).ProviderId);
        Assert.Equal(102, Assert.Single(second.Source.Tracks.Items).ProviderId);
        Assert.Equal(first.Source.BundleDigest, second.Source.BundleDigest);
        Assert.Equal(new[] { "primary", "primary", "remixer" }, second.Source.Tracks.Items[0].Credits.Select(c => c.Role));
        Assert.Equal(3, first.Source.Credits.Count);
        Assert.Equal("GMJ & Matter", first.Source.Credits[2].Name);
        Assert.Equal("source-label", Assert.Single(first.LabelCandidates).Id);
        Assert.Contains(first.Source.Diagnostics, d => d.Code == "compound_name_review");
        var raw = await client.GetStringAsync("/api/v1/purchase-release-candidates/10");
        Assert.DoesNotContain("PRIVATE_", raw); Assert.DoesNotContain(PurchaseSourcePath, raw);
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(PurchaseSourcePath)));
        Assert.Equal(catalogHash, SHA256.HashData(await File.ReadAllBytesAsync(_path)));
        var openapi = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(openapi.GetProperty("paths").TryGetProperty("/api/v1/purchase-release-candidates/{releaseId}", out _));
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/purchase-release-candidates")).StatusCode);
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates/12"), 404, "purchase_candidate_not_found");
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates/0"), 400, "validation_failed");
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates/10?q=ignored"), 400, "validation_failed");
    }

    private const string BrokerEntitySchema = """
        CREATE TABLE beatport_broker_entity (entity_type TEXT NOT NULL, entity_id INTEGER NOT NULL, payload_json TEXT NOT NULL,
            fetched_utc TEXT NOT NULL, source_request_key TEXT NOT NULL, PRIMARY KEY (entity_type, entity_id));
        """;

    [Fact]
    public async Task PurchaseInspector_BrokerFetchedTracksFillMissingAndSupersedeConflictingPayloads()
    {
        await CreatePurchaseSource();
        // Track 102 is missing from ISRC lookups and track 101 has two disagreeing observations.
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json = @tracks", ("@tracks", $"[{TrackPayload()},{TrackPayload().Replace("Original Mix", "Other")}]"));
        var source = new SqlitePurchaseReleaseSource(PurchaseSourcePath);
        var before = (await source.InspectAsync(10, new()))!;
        Assert.Contains(before.Diagnostics, d => d.Code == "track_payload_missing");
        Assert.Contains(before.Diagnostics, d => d.Code == "conflicting_track_payloads");

        await SourceSql(BrokerEntitySchema + """
            INSERT INTO beatport_broker_entity VALUES ('track', 101, @t101, '2026-11-06T00:00:00.0000000Z', '/v4/catalog/tracks/101/');
            INSERT INTO beatport_broker_entity VALUES ('track', 102, @t102, '2026-11-06T00:00:00.0000000Z', '/v4/catalog/tracks/102/');
            """, ("@t101", TrackPayload()), ("@t102", TrackPayload(102)));
        var after = (await source.InspectAsync(10, new()))!;
        Assert.True(after.SourceComplete, string.Join(", ", after.Diagnostics.Where(d => d.Severity == "blocker").Select(d => d.Code)));
        Assert.Equal(2, after.HydratedTrackCount);
        Assert.NotEqual(before.BundleDigest, after.BundleDigest);
        var batch = Assert.Single(await source.InspectAllAsync(), r => r.ReleaseId == 10);
        Assert.Equal(after.BundleDigest, batch.BundleDigest);
    }

    [Fact]
    public async Task PurchaseInspector_UsesTheNewerOfCachedAndBrokerReleasePayloads()
    {
        await CreatePurchaseSource();
        await SourceSql(BrokerEntitySchema + """
            INSERT INTO beatport_broker_entity VALUES ('release', 10, @old, '2026-01-01T00:00:00.0000000Z', '/v4/catalog/releases/10/');
            INSERT INTO beatport_broker_entity VALUES ('release', 11, @release11, '2026-11-06T00:00:00.0000000Z', '/v4/catalog/releases/11/');
            """, ("@old", ReleasePayload().Replace("Test release", "Old title")), ("@release11", ReleasePayload(id: 11).Replace("Test release", "Broker title")));
        var source = new SqlitePurchaseReleaseSource(PurchaseSourcePath);
        Assert.Equal("Test release", (await source.InspectAsync(10, new()))!.Title);
        Assert.Equal("Broker title", (await source.InspectAsync(11, new()))!.Title);
        var all = await source.InspectAllAsync();
        Assert.Equal("Test release", all.Single(r => r.ReleaseId == 10).Title);
        Assert.Equal("Broker title", all.Single(r => r.ReleaseId == 11).Title);
        var queue = await source.QueryAsync(new());
        Assert.True(queue.Items.Single(r => r.ReleaseId == 11).HasCachedRelease);

        await SourceSql("UPDATE beatport_broker_entity SET fetched_utc = '2026-11-07T00:00:00.0000000Z' WHERE entity_id = 10");
        Assert.Equal("Old title", (await source.InspectAsync(10, new()))!.Title);
    }

    [Theory]
    [InlineData("null", "release_payload_invalid")]
    [InlineData("{broken", "release_payload_invalid")]
    [InlineData("[]", "release_payload_invalid")]
    public async Task PurchaseInspector_NullMalformedNonObjectReleaseIsExplicitIncomplete(string payload, string code)
    {
        await CreatePurchaseSource();
        await SourceSql("UPDATE beatport_release_cache SET release_json=@payload", ("@payload", payload));
        var result = (await new SqlitePurchaseReleaseSource(PurchaseSourcePath).InspectAsync(10, new()))!;
        Assert.False(result.SourceComplete); Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == "blocker");
    }

    [Theory]
    [InlineData("missing", "track_payload_missing")]
    [InlineData("release", "track_release_mismatch")]
    [InlineData("label", "track_label_mismatch")]
    [InlineData("conflict", "conflicting_track_payloads")]
    [InlineData("malformed", "track_payload_missing")]
    public async Task PurchaseInspector_WholeBundleDiagnosticsIncludeOffPageMissingContradictoryTracks(string mode, string code)
    {
        await CreatePurchaseSource();
        var tracks = mode switch
        {
            "missing" => $"[{TrackPayload()}]",
            "release" => $"[{TrackPayload()},{TrackPayload(102, 999)}]",
            "label" => $"[{TrackPayload()},{TrackPayload(102, labelId: 999)}]",
            "malformed" => "{broken",
            _ => $"[{TrackPayload()},{TrackPayload(102)}]",
        };
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json=@tracks", ("@tracks", tracks));
        if (mode == "conflict")
            await SourceSql("INSERT INTO beatport_isrc_lookup VALUES ('OTHER', 'Success', @tracks, '2026-11-05T00:00:00Z')", ("@tracks", $"[{TrackPayload(102, 999)}]"));
        var result = (await new SqlitePurchaseReleaseSource(PurchaseSourcePath).InspectAsync(10, new(PageSize: 1)))!;
        Assert.Single(result.Tracks.Items); Assert.False(result.SourceComplete);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
        var batch = Assert.Single(await new SqlitePurchaseReleaseSource(PurchaseSourcePath).InspectAllAsync(), r => r.ReleaseId == 10);
        Assert.Equal(result.BundleDigest, batch.BundleDigest); Assert.Equal(result.HydratedTrackCount, batch.HydratedTrackCount);
        Assert.Equal(result.Diagnostics, batch.Diagnostics);
    }

    [Fact]
    public async Task PurchaseReadiness_BatchMatchesSingleInspectionAndRebuildsWhenTheSourceChanges()
    {
        await CreatePurchaseSource();
        var source = new SqlitePurchaseReleaseSource(PurchaseSourcePath);
        var all = await source.InspectAllAsync();
        Assert.Equal(new long[] { 10, 11 }, all.Select(r => r.ReleaseId));
        foreach (var batch in all)
        {
            var single = (await source.InspectAsync(batch.ReleaseId, new(PageSize: 1)))!;
            Assert.Equal(single.BundleDigest, batch.BundleDigest); Assert.Equal(single.SourceComplete, batch.SourceComplete);
            Assert.Equal(single.ManifestTrackCount, batch.ManifestTrackCount); Assert.Equal(single.Diagnostics, batch.Diagnostics);
        }
        var cache = new PurchaseReadinessCache(source);
        var before = await cache.GetAsync(default);
        Assert.Same(before, await cache.GetAsync(default));
        Assert.True(before[10].SourceComplete); Assert.Equal(42, before[10].LabelProviderId);
        Assert.Contains("release_payload_unavailable", before[11].BlockerCodes);
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json=@tracks", ("@tracks", $"[{TrackPayload()}]"));
        File.SetLastWriteTimeUtc(PurchaseSourcePath, DateTime.UtcNow.AddMinutes(1));
        var after = await cache.GetAsync(default);
        Assert.NotSame(before, after);
        Assert.False(after[10].SourceComplete); Assert.Equal(1, after[10].HydratedTrackCount); Assert.Equal(["track_payload_missing"], after[10].BlockerCodes);
    }

    [Theory]
    [InlineData("https://evil.test/v4/catalog/tracks/101/", "invalid_track_reference")]
    [InlineData("https://api.beatport.com/v4/catalog/tracks/101/?secret=1", "invalid_track_reference")]
    [InlineData("https://api.beatport.com/v4/catalog/tracks/102/", "duplicate_track_reference")]
    public async Task PurchaseInspector_ValidatesManifestReferencesWithoutFollowingUrls(string reference, string code)
    {
        await CreatePurchaseSource();
        await SourceSql("UPDATE beatport_release_cache SET release_json=@release",
            ("@release", ReleasePayload(new[] { reference, "https://api.beatport.com/v4/catalog/tracks/102/" })));
        var result = (await new SqlitePurchaseReleaseSource(PurchaseSourcePath).InspectAsync(10, new()))!;
        Assert.False(result.SourceComplete); Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public async Task PurchaseInspector_SourceRefreshChangesDigestAndLiteralSearchHasStablePaging()
    {
        await CreatePurchaseSource();
        var source = new SqlitePurchaseReleaseSource(PurchaseSourcePath);
        Assert.Empty((await source.QueryAsync(new(Search: "%"))).Items);
        Assert.Equal(10, Assert.Single((await source.QueryAsync(new(Search: "10"))).Items).ReleaseId);
        Assert.Empty((await source.QueryAsync(new(Page: int.MaxValue))).Items);
        Assert.Null(await source.InspectAsync(12, new()));
        var before = (await source.InspectAsync(10, new()))!;
        await SourceSql("UPDATE beatport_release_cache SET fetched_utc='2026-11-06T00:00:00Z'");
        Assert.NotEqual(before.BundleDigest, (await source.InspectAsync(10, new()))!.BundleDigest);
        var missing = (await source.InspectAsync(11, new()))!;
        Assert.False(missing.SourceComplete); Assert.Empty(missing.Tracks.Items);
    }

    [Theory]
    [InlineData("count", "track_count_mismatch")]
    [InlineData("identity", "release_identity_mismatch")]
    [InlineData("large_manifest", "manifest_too_large")]
    [InlineData("large_payload", "release_payload_unavailable")]
    public async Task PurchaseInspector_RejectsCountIdentityAndSizeViolations(string mode, string code)
    {
        await CreatePurchaseSource();
        var payload = mode switch
        {
            "count" => ReleasePayload(count: 3),
            "identity" => ReleasePayload(id: 999),
            "large_manifest" => ReleasePayload(Enumerable.Range(1, 2001).Select(id => $"https://api.beatport.com/v4/catalog/tracks/{id}/").ToArray(), count: 2001),
            _ => new string(' ', 4 * 1024 * 1024 + 1),
        };
        await SourceSql("UPDATE beatport_release_cache SET release_json=@payload", ("@payload", payload));
        var inspection = (await new SqlitePurchaseReleaseSource(PurchaseSourcePath).InspectAsync(10, new()))!;
        Assert.False(inspection.SourceComplete); Assert.Contains(inspection.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public async Task PurchaseInspector_NonObjectCacheEntriesNeverCrashAndMissingCreditArraysRemainDistinctFromEmpty()
    {
        await CreatePurchaseSource();
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json=@tracks",
            ("@tracks", $"[\"not a track\",null,17,{{\"id\":102,\"name\":\"Incomplete track\",\"release\":{{\"id\":10,\"label\":{{\"id\":42,\"name\":\"Test label\"}}}}}},{TrackPayload()}]"));
        var inspection = (await new SqlitePurchaseReleaseSource(PurchaseSourcePath).InspectAsync(10, new()))!;
        Assert.False(inspection.SourceComplete);
        Assert.Equal("cached", inspection.Tracks.Items[0].Status);
        Assert.Equal("invalid", inspection.Tracks.Items[1].Status);
        Assert.Contains(inspection.Diagnostics, d => d.Code == "credit_array_missing" && d.SourcePosition == 2);
    }

    [Fact]
    public async Task PurchaseInspector_CancelledReadsDoNotCreateSourceFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new SqlitePurchaseReleaseSource(PurchaseSourcePath).QueryAsync(new(), cancellation.Token));
        Assert.False(File.Exists(PurchaseSourcePath));
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?q=a&q=b")]
    [InlineData("?path=/somewhere")]
    public async Task PurchaseInspector_InvalidQueriesAreRejectedBeforeReadingSource(string query)
    {
        await ProblemAsync(await _client.GetAsync("/api/v1/purchase-release-candidates" + query), 400, "validation_failed");
    }

    [Fact]
    public async Task PurchaseInspector_UnconfiguredMissingAndUnsupportedSourcesSurfaceSafeErrors()
    {
        await ProblemAsync(await _client.GetAsync("/api/v1/purchase-release-candidates"), 503, "purchase_source_unavailable");
        var missing = new SqlitePurchaseReleaseSource(PurchaseSourcePath);
        await Assert.ThrowsAsync<PurchaseSourceUnavailableException>(async () => await missing.QueryAsync(new()));
        Assert.False(File.Exists(PurchaseSourcePath));
        await SourceSql("CREATE TABLE unrelated (id INTEGER)");
        await Assert.ThrowsAsync<PurchaseSourceUnavailableException>(async () => await missing.QueryAsync(new()));
    }
}
