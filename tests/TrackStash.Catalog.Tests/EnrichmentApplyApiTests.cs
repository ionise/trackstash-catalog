using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrackStash.Catalog.Api;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private const string ApplyRecordingJson = """
        {"id":7,"title":"Updated tune","isrc":"GBTRK0000001","genre":["House"],"subgenre":["Progressive House"],
         "bpm":124.5,"key_alphanumeric":"11A","artists":[]}
        """;
    private static async Task<(JsonElement Review, string Tag)> EnrichmentReviewAsync(HttpClient client, string resource = "recordings",
        string id = "t-shared", string candidateId = "7", string? editionId = null, string provider = "sonovault")
    {
        var entity = await client.GetFromJsonAsync<JsonElement>($"/api/v1/{resource}/{id}");
        var response = await client.PostAsJsonAsync($"/api/v1/{resource}/{id}/enrichment/{provider}/reviews",
            new { candidateId, editionId, catalogVersion = entity.GetProperty("version").GetString() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"/api/v1/enrichment-reviews/{review.GetProperty("id").GetString()}", response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (review, response.Headers.ETag!.ToString());
    }
    private static Task<HttpResponseMessage> EnrichmentApplyAsync(HttpClient client, JsonElement review, string? tag,
        string[] fields, string? preferredGenre = null, string reason = "Reviewed SonoVault candidate", bool confirmed = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/enrichment-reviews/{review.GetProperty("id").GetString()}/apply")
        {
            Content = JsonContent.Create(new { fields, preferredGenre, reason, confirmed }),
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task EnrichmentApply_SelectiveRecordingFieldsPersistAcrossPlacementsAndReplayWithoutProviderCalls()
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory { ResponseJson = ApplyRecordingJson };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var original = await client.GetStringAsync("/api/v1/recordings/t-shared");
        var (review, tag) = await EnrichmentReviewAsync(client);
        Assert.Equal(original, await client.GetStringAsync("/api/v1/recordings/t-shared"));
        Assert.Equal("/v1/tracks/7", factory.LastUri!.AbsolutePath);
        Assert.Equal(1, factory.Count);
        Assert.Equal(1, await ImportCount("enrichment_review"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        string[] fields = ["Bpm", "MusicalKey", "Genre", "Subgenre", "PreferredGenre"];
        var result = await EnrichmentApplyAsync(client, review, tag, fields, "Progressive House");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var recording = (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!;
        Assert.Equal("Shared tune", recording.Title);
        Assert.Equal("Original Mix", recording.MixName);
        Assert.Empty(recording.ExternalReferences);
        Assert.Equal(124.5m, recording.MusicalMetadata!.Bpm);
        Assert.Equal(("11A", "Camelot", "Progressive House"),
            (recording.MusicalMetadata.KeyValue, recording.MusicalMetadata.KeyNotation, recording.MusicalMetadata.PreferredGenre));
        Assert.Equal(["House"], recording.MusicalMetadata.Genres);
        Assert.Equal(["Progressive House"], recording.MusicalMetadata.Subgenres);
        Assert.Single(recording.Credits);
        Assert.NotEqual(JsonDocument.Parse(original).RootElement.GetProperty("version").GetString(), recording.Version);
        var tracks = (await client.GetFromJsonAsync<TrackListResponse>("/api/v1/tracks?recordingId=t-shared"))!;
        Assert.Equal(2, tracks.Items.Count);
        Assert.All(tracks.Items, track => Assert.Equal(124.5m, track.MusicalMetadata!.Bpm));
        var appearances = (await client.GetFromJsonAsync<ArtistAppearancesResponse>("/api/v1/artists/t-primary/track-appearances"))!;
        Assert.Equal(2, appearances.Items.Count);
        Assert.All(appearances.Items, track => Assert.Equal("Progressive House", track.MusicalMetadata!.PreferredGenre));
        var replay = await EnrichmentApplyAsync(client, review, tag, fields.Reverse().ToArray(), "Progressive House");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await result.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(1, factory.Count);
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/v1/enrichment-reviews/{review.GetProperty("id").GetString()}");
        Assert.Equal(review.GetProperty("candidate").GetRawText(), saved.GetProperty("candidate").GetRawText());
        Assert.Equal(JsonValueKind.Object, saved.GetProperty("outcome").ValueKind);
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, fields, "House"), 409, "enrichment_already_applied");
    }

    [Fact]
    public async Task EnrichmentApply_RejectsInvalidIntentAndRollsBackEarlierScalarWrites()
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory { ResponseJson = ApplyRecordingJson };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var original = await client.GetStringAsync("/api/v1/recordings/t-shared");
        var history = await ImportCount("catalog_foundation_revision");
        var (review, tag) = await EnrichmentReviewAsync(client);
        await ProblemAsync(await EnrichmentApplyAsync(client, review, null, ["Name"]), 428, "precondition_required");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Name"], confirmed: false), 400, "enrichment_invalid_apply");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, []), 400, "enrichment_invalid_apply");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Name", "Name"]), 400, "enrichment_invalid_apply");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Credits"]), 400, "enrichment_field_unavailable");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Name", "PreferredGenre"], "Progressive House"), 400, "enrichment_classification_invalid");
        Assert.Equal(original, await client.GetStringAsync("/api/v1/recordings/t-shared"));
        Assert.Equal(history, await ImportCount("catalog_foundation_revision"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(HttpStatusCode.OK, (await EnrichmentApplyAsync(client, review, tag, ["Name"])).StatusCode);
        Assert.Equal("Updated tune", (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!.Title);
    }

    [Fact]
    public async Task EnrichmentApply_StaleRecordingAndConflictingIdentityDoNotWrite()
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory { ResponseJson = ApplyRecordingJson };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var (review, tag) = await EnrichmentReviewAsync(client);
        await ImportSql("UPDATE recording SET mix_name='Different version' WHERE recording_id='t-shared'");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Bpm"]), 412, "enrichment_state_changed");
        Assert.Equal(0, await ImportCount("recording_musical_metadata"));
        (review, tag) = await EnrichmentReviewAsync(client);
        await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id,is_primary) VALUES('t-second','sonovault','7',0)");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["SonoVaultIdentity"]), 409, "enrichment_identity_conflict");
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(HttpStatusCode.OK, (await EnrichmentApplyAsync(client, review, tag, ["Bpm"])).StatusCode);
    }

    [Theory]
    [InlineData(null, "year", false)]
    [InlineData("12", "year", false)]
    [InlineData("12", "day", true)]
    public async Task EnrichmentApply_ReleaseRequiresSelectedEditionAndPreservesTracklist(string? selected, string precision, bool exactDate)
    {
        await ImportSql(TrackFixture);
        var json = $$"""
            {"id":8,"title":"Reviewed release","release_date":"1999-01-01","edition":{{(selected ?? "null")}},
             "editions":[{"id":12,"source":"discogs","external_id":"99","title":"Edition","release_date":"2025-02-03",
              "date_precision":"{{precision}}","catalog_no":"NEW1","barcode":"123456","track_count":1}],"tracks":[]}
            """;
        var factory = new EnrichmentHttpFactory { ResponseJson = json };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var tracks = await client.GetStringAsync("/api/v1/releases/t-new/tracks");
        var (review, tag) = await EnrichmentReviewAsync(client, "releases", "t-new", "8", selected);
        var fields = review.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()!).ToArray();
        Assert.Equal(exactDate, fields.Contains("ReleaseDate"));
        Assert.Equal(selected is not null, fields.Contains("CatalogNumber"));
        Assert.Equal(HttpStatusCode.OK, (await EnrichmentApplyAsync(client, review, tag, fields)).StatusCode);
        var release = (await client.GetFromJsonAsync<ReleaseResponse>("/api/v1/releases/t-new"))!;
        Assert.Equal("Reviewed release", release.Title);
        Assert.Equal(exactDate ? "2025-02-03" : "2024-06-01", release.PublishDate);
        Assert.Equal(selected is null ? null : "NEW1", release.CatalogNumber);
        Assert.Equal(selected is null ? null : "123456", release.Upc);
        Assert.Equal("8", Assert.Single(release.ExternalReferences).ExternalId);
        Assert.Equal(tracks, await client.GetStringAsync("/api/v1/releases/t-new/tracks"));
        Assert.Single(release.Labels);
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public async Task EnrichmentApply_SchemaGateAuthenticationAndHistoryImmutabilityAreExplicit()
    {
        await ImportSql(TrackFixture);
        using var anonymous = _app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/recordings/t-shared/enrichment/sonovault/reviews",
            new { candidateId = "7", catalogVersion = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/enrichment-reviews/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/enrichment-reviews/missing/apply", new { })).StatusCode);
        await ImportSql("DELETE FROM schema_migration WHERE version>=13");
        await ProblemAsync(await _client.PostAsJsonAsync("/api/v1/recordings/t-shared/enrichment/sonovault/reviews",
            new { candidateId = "7", catalogVersion = "x" }), 409, "enrichment_schema_required");
        Assert.Equal(12, await Provider.Migrations.GetCurrentVersionAsync());
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }

    [Fact]
    public async Task EnrichmentApply_ReviewDetectsChangesDuringProviderFetchAndRejectsClientProposalFields()
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory { ResponseJson = ApplyRecordingJson,
            BeforeResponseAsync = () => ImportSql("UPDATE recording SET title='Concurrent edit' WHERE recording_id='t-shared'") };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var entity = await client.GetFromJsonAsync<JsonElement>("/api/v1/recordings/t-shared");
        var version = entity.GetProperty("version").GetString();
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/recordings/t-shared/enrichment/sonovault/reviews",
            new { candidateId = "7", catalogVersion = version, metadata = new { bpm = 1 } }), 400, "validation_failed");
        Assert.Equal(0, factory.Count);
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/recordings/t-shared/enrichment/sonovault/reviews",
            new { candidateId = "7", catalogVersion = version }), 412, "enrichment_state_changed");
        Assert.Equal(1, factory.Count);
        Assert.Equal(0, await ImportCount("enrichment_review"));
        Assert.Equal("Concurrent edit", (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!.Title);
    }

    private sealed class EnrichmentClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public async Task EnrichmentApply_ExpiryAndOriginalReviewTagAreEnforcedWithoutProviderCalls()
    {
        await ImportSql(TrackFixture);
        var clock = new EnrichmentClock();
        var factory = new EnrichmentHttpFactory { ResponseJson = ApplyRecordingJson };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
            builder.Services.AddSingleton<TimeProvider>(clock);
        });
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var (review, tag) = await EnrichmentReviewAsync(client);
        await ProblemAsync(await EnrichmentApplyAsync(client, review, "\"d3Jvbmc=\"", ["Bpm"]), 412, "enrichment_state_changed");
        clock.Now = clock.Now.AddHours(25);
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Bpm"]), 410, "enrichment_review_expired");
        Assert.Equal(1, factory.Count); Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }

    [Fact]
    public async Task EnrichmentApply_AcceptingIdentityPreservesUnselectedScalarMetadataAndEquivalentProviderIds()
    {
        await ImportSql(TrackFixture);
        await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id,is_primary) VALUES('t-shared','sonovault','0007',0)");
        var factory = new EnrichmentHttpFactory { ResponseJson = ApplyRecordingJson };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var before = await ImportCount("recording_artist_credit");
        var (review, tag) = await EnrichmentReviewAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await EnrichmentApplyAsync(client, review, tag, ["SonoVaultIdentity"])).StatusCode);
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var recording = (await unit.Recordings.GetByIdAsync("t-shared"))!;
        Assert.Equal("Shared tune", recording.Title); Assert.Null(recording.Name); Assert.Null(recording.NormalizedName);
        Assert.Equal("Original Mix", recording.MixName); Assert.Equal("GBTRK0000001", recording.Isrc);
        Assert.Equal("0007", Assert.Single(recording.ExternalReferences).ExternalId);
        Assert.Equal(before, await ImportCount("recording_artist_credit"));
    }
}
