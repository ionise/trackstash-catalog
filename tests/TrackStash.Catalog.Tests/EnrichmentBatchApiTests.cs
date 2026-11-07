using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private const string BatchSharedTrack = """
        {"id":7,"name":"Shared tune","mix_name":"Original Mix","isrc":"GBTRK0000001","bpm":124,
         "key":{"name":"F# Minor"},"genre":{"name":"House"},"sub_genre":{"name":"Progressive House"},
         "artists":[{"id":70,"name":"Zed Primary"}]}
        """;
    private const string BatchSecondTrack = """
        {"id":8,"name":"Bravo tune","mix_name":"Extended Mix","bpm":126,
         "artists":[{"id":71,"name":"Alpha Other"}]}
        """;
    private static async Task<(JsonElement Review, string Tag)> PrepareTrackBatch(HttpClient client)
    {
        var release = await client.GetFromJsonAsync<JsonElement>("/api/v1/releases/t-new");
        var response = await client.PostAsJsonAsync("/api/v1/releases/t-new/enrichment/beatport/track-reviews",
            new { candidateId = "10", catalogVersion = release.GetProperty("version").GetString() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/api/v1/enrichment-track-reviews/" + review.GetProperty("id").GetString(), response.Headers.Location!.OriginalString);
        return (review, response.Headers.ETag!.ToString());
    }
    private static object[] BatchIntent(JsonElement batch, params string[] fields) =>
        batch.GetProperty("tracks").EnumerateArray().Where(t => t.GetProperty("review").ValueKind == JsonValueKind.Object)
            .Select(t => (object)new { reviewId = t.GetProperty("review").GetProperty("id").GetString(), fields }).ToArray();
    private static async Task<HttpResponseMessage> ApplyTrackBatch(HttpClient client, JsonElement review, string? tag, object[] selections,
        bool confirmed = true, string reason = "Reviewed Release version and track matches")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/enrichment-track-reviews/" + review.GetProperty("id").GetString() + "/apply")
        {
            Content = JsonContent.Create(new { selections, reason, confirmed })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await client.SendAsync(request);
    }
    [Fact]
    public async Task EnrichmentBatch_MatchesFreezesAppliesAtomicallyAndReplaysWithoutProviderCalls()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id) VALUES('t-second','beatport','008')");
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42);
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = $$"""{"count":2,"results":[{{BatchSharedTrack}},{{BatchSecondTrack}}]}""";
        var before = await client.GetStringAsync("/api/v1/releases/t-new");
        var (review, tag) = await PrepareTrackBatch(client);
        var rows = review.GetProperty("tracks").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal("Confident", r.GetProperty("status").GetString()));
        Assert.Equal(before, await client.GetStringAsync("/api/v1/releases/t-new"));
        Assert.Equal(3, await ImportCount("enrichment_review"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(2, broker.Requests.Count);
        var child = rows[0].GetProperty("review");
        var childResponse = await client.GetAsync("/api/v1/enrichment-reviews/" + child.GetProperty("id").GetString());
        await ProblemAsync(await EnrichmentApplyAsync(client, child, childResponse.Headers.ETag!.ToString(), ["Bpm"]),
            409, "enrichment_batch_required");
        var selected = BatchIntent(review, "Bpm");
        await ProblemAsync(await ApplyTrackBatch(client, review, null, selected), 428, "precondition_required");
        await ProblemAsync(await ApplyTrackBatch(client, review, tag, selected, confirmed: false), 400, "enrichment_invalid_apply");
        var applied = await ApplyTrackBatch(client, review, tag, selected);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(3, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(124, (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!.MusicalMetadata!.Bpm);
        Assert.Equal(126, (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-second"))!.MusicalMetadata!.Bpm);
        Assert.Empty((await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!.ExternalReferences);
        Assert.Equal(0, await ImportCount("artwork_asset"));
        var appearances = await client.GetFromJsonAsync<TrackListResponse>("/api/v1/tracks?recordingId=t-shared");
        Assert.Equal(2, appearances!.Items.Count);
        Assert.All(appearances.Items, t => Assert.Equal(124, t.MusicalMetadata!.Bpm));
        var replay = await ApplyTrackBatch(client, review, tag, selected.Reverse().ToArray());
        Assert.Equal(await applied.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(2, broker.Requests.Count);
        await ProblemAsync(await ApplyTrackBatch(client, review, tag, selected, reason: "Different intent"), 409, "enrichment_already_applied");
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/v1/enrichment-track-reviews/" + review.GetProperty("id").GetString());
        Assert.Equal(2, saved.GetProperty("outcome").GetProperty("recordings").GetArrayLength());
        Assert.Equal(review.GetProperty("candidate").GetRawText(), saved.GetProperty("candidate").GetRawText());
    }
    [Theory]
    [InlineData("stale")]
    [InlineData("identity")]
    [InlineData("classification")]
    [InlineData("unavailable-last")]
    public async Task EnrichmentBatch_OneFailureRollsBackEverySelectedRecordingAndAudit(string failure)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id) VALUES('t-second','beatport','8')");
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42);
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = $$"""{"count":2,"results":[{{BatchSharedTrack}},{{BatchSecondTrack}}]}""";
        var (review, tag) = await PrepareTrackBatch(client);
        var selected = BatchIntent(review, "Bpm");
        if (failure == "stale")
            await ImportSql("UPDATE recording SET mix_name='Changed locally' WHERE recording_id='t-second'");
        if (failure == "identity")
        {
            await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id) VALUES('t-loose','beatport','007')");
            selected = BatchIntent(review, "Bpm", "BeatportIdentity");
        }
        if (failure == "classification") selected = BatchIntent(review, "Bpm", "PreferredGenre");
        if (failure == "unavailable-last")
        {
            var children = review.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("review"))
                .OrderBy(r => r.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
            selected = [
                new { reviewId = children[0].GetProperty("id").GetString(), fields = new[] { "Bpm" } },
                new { reviewId = children[1].GetProperty("id").GetString(), fields = new[] { "Credits" } }
            ];
        }
        var response = await ApplyTrackBatch(client, review, tag, selected);
        Assert.Equal(failure == "stale" ? HttpStatusCode.PreconditionFailed : failure == "identity" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!.MusicalMetadata?.Bpm);
        Assert.Null((await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-second"))!.MusicalMetadata?.Bpm);
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(2, broker.Requests.Count);
    }
    [Theory]
    [InlineData("mix")]
    [InlineData("artist")]
    [InlineData("duplicate")]
    [InlineData("isrc")]
    public async Task EnrichmentBatch_ConflictingOrNonUniqueTrackMatchesAreNotWritable(string conflict)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        var track = conflict switch
        {
            "mix" => BatchSharedTrack.Replace("Original Mix", "Dub"),
            "artist" => BatchSharedTrack.Replace("Zed Primary", "Different artist"),
            "isrc" => BatchSharedTrack.Replace("GBTRK0000001", "GBTRK0000002"),
            _ => BatchSharedTrack,
        };
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42);
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = conflict == "duplicate"
            ? $$"""{"count":2,"results":[{{track}},{{track}}]}""" : $$"""{"count":1,"results":[{{track}}]}""";
        var (review, tag) = await PrepareTrackBatch(client);
        Assert.All(review.GetProperty("tracks").EnumerateArray(), t => Assert.Equal(JsonValueKind.Null, t.GetProperty("review").ValueKind));
        var arbitrary = new object[] { new { reviewId = "invented", fields = new[] { "Bpm" } } };
        await ProblemAsync(await ApplyTrackBatch(client, review, tag, arbitrary), 400, "enrichment_field_unavailable");
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }

    [Fact]
    public async Task EnrichmentBatch_ExpiresAt24HoursButSavedReplayRemainsAvailable()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var clock = new EnrichmentClock();
        var (app, client, broker) = await StartWithBrokerAsync(clock: clock);
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42);
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = $$"""{"count":1,"results":[{{BatchSharedTrack}}]}""";
        var (review, tag) = await PrepareTrackBatch(client);
        var (expiring, expiryTag) = await PrepareTrackBatch(client);
        clock.Now = clock.Now.AddHours(24).AddSeconds(-1);
        var selected = BatchIntent(review, "Bpm");
        var applied = await ApplyTrackBatch(client, review, tag, selected);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        clock.Now = clock.Now.AddSeconds(1);
        await ProblemAsync(await ApplyTrackBatch(client, expiring, expiryTag, BatchIntent(expiring, "Bpm")), 410, "enrichment_review_expired");
        var replay = await ApplyTrackBatch(client, review, tag, selected);
        Assert.Equal(await applied.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(4, broker.Requests.Count);
        Assert.Equal(2, await ImportCount("enrichment_apply_revision"));
    }

    [Fact]
    public async Task EnrichmentBatch_RejectsAuthenticationAndStaleReleaseBeforeProviderCalls()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        var release = await client.GetFromJsonAsync<JsonElement>("/api/v1/releases/t-new");
        var body = new { candidateId = "10", catalogVersion = release.GetProperty("version").GetString() };
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/v1/releases/t-new/enrichment/beatport/track-reviews", body)).StatusCode);
        await ImportSql("UPDATE release SET title='Changed locally' WHERE release_id='t-new'");
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/releases/t-new/enrichment/beatport/track-reviews", body),
            412, "enrichment_state_changed");
        Assert.Empty(broker.Requests);
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }

    [Fact]
    public async Task EnrichmentBatch_Accepts100PlacementsButRejects101BeforeFetching()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        await ImportSql("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<98)
            INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,legacy)
            SELECT 'batch-limit-'||i,'t-new','t-second',i+2,0 FROM n;
            """);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42);
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = """{"count":0,"results":[]}""";
        await PrepareTrackBatch(client);
        Assert.Equal(2, broker.Requests.Count);
        await ImportSql("""
            INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,legacy)
            VALUES('batch-limit-101','t-new','t-second',101,0);
            """);
        var release = await client.GetFromJsonAsync<JsonElement>("/api/v1/releases/t-new");
        var response = await client.PostAsJsonAsync("/api/v1/releases/t-new/enrichment/beatport/track-reviews",
            new { candidateId = "10", catalogVersion = release.GetProperty("version").GetString() });
        await ProblemAsync(response, 409, "enrichment_batch_limit");
        Assert.Equal(2, broker.Requests.Count);
        Assert.Equal(1, await ImportCount("enrichment_review"));
    }

    [Theory]
    [InlineData("""{"reviewId":"child","fields":["Bpm"],"metadata":{"bpm":999}}""")]
    [InlineData("""{"reviewId":"child","fields":["Bpm"],"fields":["Name"]}""")]
    [InlineData("""{"reviewId":"child","fields":[123]}""")]
    public async Task EnrichmentBatch_RejectsNestedProposalValuesAndRepeatedOrNonStringFields(string selection)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/enrichment-track-reviews/unused/apply")
        {
            Content = new StringContent($$"""{"selections":[{{selection}}],"reason":"Reviewed","confirmed":true}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"dGVzdA==\"");
        await ProblemAsync(await client.SendAsync(request), 400, "validation_failed");
        Assert.Empty(broker.Requests);
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }
}
