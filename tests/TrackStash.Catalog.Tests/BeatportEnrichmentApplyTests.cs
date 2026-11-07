using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private const string BeatportReviewTrack = """
        {"id":7,"name":"Updated tune","mix_name":"Dub","isrc":"GBTRK0000001","bpm":124.5,"length_ms":445000,
         "key":{"name":"F# Minor"},"genre":{"name":"House"},"sub_genre":{"name":"Progressive House"},
         "artists":[{"id":70,"name":"Artist"}],"remixers":[{"id":71,"name":"Remixer"}]}
        """;

    [Fact]
    public async Task BeatportEnrichmentApply_RecordingFieldsAreSelectiveAuditedAndReplayWithoutBrokerCalls()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id) VALUES('t-shared','sonovault','7')");
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/tracks/7/"] = BeatportReviewTrack;
        var original = await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared");
        var (review, tag) = await EnrichmentReviewAsync(client, provider: "beatport");
        Assert.Equal(original!.Version, (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!.Version);
        Assert.Single(broker.Requests);
        var fields = review.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()!).ToArray();
        Assert.Contains("BeatportIdentity", fields);
        Assert.DoesNotContain("SonoVaultIdentity", fields);
        Assert.DoesNotContain("DurationMs", fields);
        Assert.DoesNotContain("Credits", fields);
        Assert.Equal(445000, review.GetProperty("candidate").GetProperty("metadata").GetProperty("durationMs").GetInt64());
        Assert.All(review.GetProperty("candidate").GetProperty("evidence").EnumerateArray(),
            e => Assert.Equal("beatport", e.GetProperty("source").GetString()));
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["SonoVaultIdentity"]), 400, "enrichment_field_unavailable");
        string[] selected = ["Bpm", "MusicalKey", "Genre", "Subgenre", "PreferredGenre"];
        var applied = await EnrichmentApplyAsync(client, review, tag, selected, "Progressive House", "Reviewed Beatport recording");
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var recording = (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!;
        Assert.Equal(original.Title, recording.Title);
        Assert.Equal(original.MixName, recording.MixName);
        Assert.Equal(original.DurationMs, recording.DurationMs);
        Assert.Equal(original.Credits.Count, recording.Credits.Count);
        Assert.Single(recording.ExternalReferences);
        Assert.Equal("sonovault", recording.ExternalReferences[0].Source);
        Assert.Equal(124.5m, recording.MusicalMetadata!.Bpm);
        Assert.Equal(("F# Minor", "Musical", "Progressive House"),
            (recording.MusicalMetadata.KeyValue, recording.MusicalMetadata.KeyNotation, recording.MusicalMetadata.PreferredGenre));
        var replay = await EnrichmentApplyAsync(client, review, tag, selected.Reverse().ToArray(), "Progressive House", "Reviewed Beatport recording");
        Assert.Equal(await applied.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Single(broker.Requests);
        Assert.Equal(1, await ImportCount("enrichment_apply_revision"));
        var (identity, identityTag) = await EnrichmentReviewAsync(client, provider: "beatport");
        Assert.Equal(HttpStatusCode.OK, (await EnrichmentApplyAsync(client, identity, identityTag, ["BeatportIdentity"])).StatusCode);
        recording = (await client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!;
        Assert.Equal(2, recording.ExternalReferences.Count);
        Assert.Contains(recording.ExternalReferences, r => r.Source == "beatport" && r.ExternalId == "7" && !r.IsPrimary);
    }

    [Fact]
    public async Task BeatportEnrichmentApply_ReleaseDateCatalogAndIdentityPreserveRelationshipsAndArtwork()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "Reviewed release", 42, "https://geo-media.beatport.com/cover.jpg");
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = """{"count":0,"results":[]}""";
        var original = await client.GetFromJsonAsync<JsonElement>("/api/v1/releases/t-new");
        var originalTracks = await client.GetStringAsync("/api/v1/releases/t-new/tracks");
        var (review, tag) = await EnrichmentReviewAsync(client, "releases", "t-new", "10", provider: "beatport");
        var fields = review.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()!).ToArray();
        Assert.Equal(["Name", "CatalogNumber", "ReleaseDate", "BeatportIdentity"], fields);
        Assert.Equal(2, broker.Requests.Count);
        var applied = await EnrichmentApplyAsync(client, review, tag, ["Name", "CatalogNumber", "ReleaseDate", "BeatportIdentity"]);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/releases/t-new");
        Assert.Equal("Reviewed release", after.GetProperty("title").GetString());
        Assert.Equal("CAT10", after.GetProperty("catalogNumber").GetString());
        Assert.Equal("2026-10-01", after.GetProperty("publishDate").GetString());
        foreach (var field in new[] { "credits", "labels", "upc" })
            Assert.Equal(original.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
        Assert.Equal(originalTracks, await client.GetStringAsync("/api/v1/releases/t-new/tracks"));
        Assert.Equal(0, await ImportCount("artwork_asset"));
        Assert.Equal(1, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(2, broker.Requests.Count);
        var candidates = await client.GetFromJsonAsync<ReleaseArtworkCandidatesResponse>("/api/v1/releases/t-new/artwork-candidates");
        Assert.Contains(candidates!.Items, c => c.Provider == "beatport");
        Assert.Equal(0, await ImportCount("artwork_asset"));
    }

    [Fact]
    public async Task BeatportEnrichmentApply_IdentityOwnershipAndStaleSnapshotBlockAllWrites()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/tracks/7/"] = BeatportReviewTrack;
        var (review, tag) = await EnrichmentReviewAsync(client, provider: "beatport");
        var original = await client.GetStringAsync("/api/v1/recordings/t-shared");
        await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id) VALUES('t-second','beatport','007')");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Name", "BeatportIdentity"]), 409, "enrichment_identity_conflict");
        Assert.Equal(original, await client.GetStringAsync("/api/v1/recordings/t-shared"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        await ImportSql("UPDATE recording SET title='Changed locally' WHERE recording_id='t-shared'");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["Name"]), 412, "enrichment_state_changed");
        Assert.Single(broker.Requests);
    }

    [Fact]
    public async Task BeatportEnrichmentApply_InvalidDateAndEditionCannotBeApplied()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42).Replace("2026-10-01", "2026");
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = """{"count":0,"results":[]}""";
        var (review, tag) = await EnrichmentReviewAsync(client, "releases", "t-new", "10", provider: "beatport");
        Assert.DoesNotContain(review.GetProperty("fields").EnumerateArray(), f => f.GetProperty("field").GetString() == "ReleaseDate");
        await ProblemAsync(await EnrichmentApplyAsync(client, review, tag, ["ReleaseDate"]), 400, "enrichment_field_unavailable");
        var entity = await client.GetFromJsonAsync<JsonElement>("/api/v1/releases/t-new");
        var invalid = await client.PostAsJsonAsync("/api/v1/releases/t-new/enrichment/beatport/reviews",
            new { candidateId = "10", editionId = "12", catalogVersion = entity.GetProperty("version").GetString() });
        await ProblemAsync(invalid, 400, "enrichment_invalid_review");
        Assert.Equal(2, broker.Requests.Count);
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }

    [Fact]
    public async Task BeatportEnrichmentApply_AuthenticationAndSchemaAreRequiredBeforeProviderRequests()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        var entity = await client.GetFromJsonAsync<JsonElement>("/api/v1/recordings/t-shared");
        var body = new { candidateId = "7", catalogVersion = entity.GetProperty("version").GetString() };
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/recordings/t-shared/enrichment/beatport/reviews", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/recordings/t-shared/enrichment/beatport", null)).StatusCode);
        await ImportSql("DELETE FROM schema_migration WHERE version=13");
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/recordings/t-shared/enrichment/beatport/reviews", body), 409, "enrichment_schema_required");
        Assert.Empty(broker.Requests);
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }

    [Theory]
    [InlineData("""{"id":10}""")]
    [InlineData("""{"id":10,"name":" "}""")]
    [InlineData("""{"id":11,"name":"Wrong release"}""")]
    [InlineData("""{"id":10,"name":"Title","catalog_number":10}""")]
    [InlineData("""{"id":10,"name":"Title","publish_date":2026}""")]
    public async Task BeatportEnrichmentApply_ReleaseDisplayFallbacksNeverBecomeCanonicalProposals(string payload)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = payload;
        var entity = await client.GetFromJsonAsync<JsonElement>("/api/v1/releases/t-new");
        var response = await client.PostAsJsonAsync("/api/v1/releases/t-new/enrichment/beatport/reviews",
            new { candidateId = "10", catalogVersion = entity.GetProperty("version").GetString() });
        await ProblemAsync(response, 502, "beatport_invalid_response");
        Assert.Single(broker.Requests);
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }
}
