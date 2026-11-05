using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    [Fact]
    public async Task ArtistAppearancesUseExistingReleaseDatesAndFullScopedContributorsWithoutWrites()
    {
        await ImportSql("""
            INSERT INTO artist(artist_id,name) VALUES('primary-a','Primary'),('primary-b','Primary'),('remix-a','Remixer'),('release-only','Release only');
            INSERT INTO release(release_id,title,publish_date,new_release_date) VALUES
              ('dated','Dated','2026-11-01','2026-11-02'),('alternate','Alternate',NULL,'2026-10-01');
            INSERT INTO recording(recording_id,title) VALUES('shared','Shared'),('unplaced','Unplaced');
            INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,legacy) VALUES
              ('dated-p','dated','shared',1,0),('alternate-p','alternate','shared',1,0);
            INSERT INTO release_artist_credit(credit_id,release_id,artist_id,role,position) VALUES
              ('release-credit','dated','release-only','primary',1);
            INSERT INTO recording_artist_credit(credit_id,recording_id,artist_id,role,position,credit_name) VALUES
              ('primary-credit','shared','primary-a','primary',1,'Primary spelling'),
              ('recording-remix','shared','remix-a','remixer',1,'Remixer'),
              ('unplaced-remix','unplaced','remix-a','remixer',1,'Remixer');
            INSERT INTO placement_artist_credit(credit_id,placement_id,artist_id,role,position,credit_name) VALUES
              ('listing-primary','dated-p','primary-b','primary',1,'Primary'),
              ('listing-remix','dated-p','remix-a','remixer',1,'Remixer');
            """);
        var history = await ImportCount("catalog_foundation_revision");
        string stamp;
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
            stamp = await unit.ReleaseImports!.GetCatalogStampAsync();
        var tracks = (await _client.GetFromJsonAsync<ArtistAppearancesResponse>("/api/v1/artists/remix-a/track-appearances"))!;
        Assert.Equal(3, tracks.Total);
        var dated = Assert.Single(tracks.Items, i => i.ReleaseId == "dated");
        Assert.Equal("2026-11-01", dated.PublishDate); Assert.Equal("2026-11-02", dated.NewReleaseDate);
        Assert.Contains(dated.Contributors, c => c.ArtistId == "primary-a" && c.CreditedName == "Primary spelling");
        Assert.Contains(dated.Contributors, c => c.ArtistId == "primary-b");
        Assert.Single(dated.Contributors, c => c.ArtistId == "remix-a");
        Assert.DoesNotContain(dated.Contributors, c => c.ArtistId == "release-only");
        Assert.All(dated.Credits, c => Assert.Equal("remixer", c.Role));
        var releaseTracks = (await _client.GetFromJsonAsync<ReleaseTracksResponse>("/api/v1/releases/dated/tracks"))!;
        var expected = releaseTracks.Items[0].Credits.Concat(releaseTracks.Items[0].RecordingCredits)
            .DistinctBy(c => (c.ArtistId, c.Role, c.CreditedName)).ToArray();
        Assert.Equal(expected, dated.Contributors);
        var alternate = Assert.Single(tracks.Items, i => i.ReleaseId == "alternate");
        Assert.Null(alternate.PublishDate); Assert.Equal("2026-10-01", alternate.NewReleaseDate);
        Assert.DoesNotContain(alternate.Contributors, c => c.ArtistId == "primary-b");
        var unplaced = Assert.Single(tracks.Items, i => i.ReleaseId is null);
        Assert.Null(unplaced.PublishDate); Assert.Null(unplaced.NewReleaseDate);
        Assert.Equal("remix-a", Assert.Single(unplaced.Contributors).ArtistId);
        var releases = (await _client.GetFromJsonAsync<ArtistAppearancesResponse>("/api/v1/artists/remix-a/release-appearances"))!;
        var release = Assert.Single(releases.Items, i => i.ReleaseId == "dated");
        Assert.Equal(dated.PublishDate, release.PublishDate);
        Assert.Equal("release-only", Assert.Single(release.Contributors).ArtistId);
        Assert.Empty(Assert.Single(releases.Items, i => i.ReleaseId == "alternate").Contributors);
        var paged = (await _client.GetFromJsonAsync<ArtistAppearancesResponse>("/api/v1/artists/remix-a/track-appearances?pageSize=1&page=2"))!;
        Assert.Equal(3, paged.Total);
        Assert.Equal(tracks.Items[1].PlacementId, Assert.Single(paged.Items).PlacementId);
        Assert.Equal(history, await ImportCount("catalog_foundation_revision"));
        await using var after = await Provider.BeginUnitOfWorkAsync();
        Assert.Equal(stamp, await after.ReleaseImports!.GetCatalogStampAsync());
    }
    [Fact]
    public async Task ArtistGroupedAppearanceApiReturnsOneReleaseWithLabelsManagedCoverAndAllEvidence()
    {
        var service = await ImportService(); var preview = await Reviewed(service);
        var outcome = await service.ApplyAsync(preview.Plan.OperationId, preview.Plan.Version, "Review", true);
        var release = (await _client.GetFromJsonAsync<ReleaseResponse>($"/api/v1/releases/{outcome.ReleaseId}"))!;
        var artist = release.Credits.First(c => c.Role == "primary").ArtistId;
        var gallery = await _client.GetAsync($"/api/v1/releases/{outcome.ReleaseId}/artwork");
        var upload = await ReleaseUpload(outcome.ReleaseId, gallery.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var cover = Assert.Single((await upload.Content.ReadFromJsonAsync<ReleaseArtworkResponse>())!.Items);
        var response = await _client.GetAsync($"/api/v1/artists/{artist}/release-appearances?pageSize=1");
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = (await response.Content.ReadFromJsonAsync<ArtistAppearancesResponse>())!;
        Assert.Equal(1, page.Total); var item = Assert.Single(page.Items);
        Assert.Equal(cover.AssetId, item.CoverAssetId);
        Assert.Equal("source-label", Assert.Single(item.Labels).Id);
        Assert.Contains(item.Credits, c => c.Scope == "release");
        Assert.Contains(item.Credits, c => c.Scope == "placement");
        var tracks = (await _client.GetFromJsonAsync<ArtistAppearancesResponse>($"/api/v1/artists/{artist}/track-appearances?pageSize=1"))!;
        Assert.Equal(2, tracks.Total); Assert.Single(tracks.Items);
        Assert.DoesNotContain(tracks.Items[0].Credits, c => c.Scope == "release");
        var next = (await _client.GetFromJsonAsync<ArtistAppearancesResponse>($"/api/v1/artists/{artist}/track-appearances?pageSize=1&page=2"))!;
        Assert.NotEqual(tracks.Items[0].PlacementId, Assert.Single(next.Items).PlacementId);
        await ProblemAsync(await _client.GetAsync($"/api/v1/artists/{artist}/release-appearances?pageSize=101"), 400, "validation_failed");
        await ProblemAsync(await _client.GetAsync($"/api/v1/artists/{artist}/track-appearances?role=primary"), 400, "validation_failed");
        await ProblemAsync(await _client.GetAsync("/api/v1/artists/missing/release-appearances"), 404, "artist_not_found");
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync($"/api/v1/artists/{artist}/release-appearances"), 401, "unauthorized");
        var profileResponse = await _client.GetAsync($"/api/v1/artists/{artist}/profile");
        var incomplete = await SaveArtistProfile(artist, profileResponse.Headers.ETag!.ToString(), new { biography = "Draft" });
        Assert.Contains("biographySourceUrl, favourite, notes, reviewStatus, links", await incomplete.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task ArtistBrowseListsLabelCreditsAndSafeDetailsAndPagedAppearancesWithoutWrites()
    {
        var service = await ImportService(); var preview = await Reviewed(service);
        var outcome = await service.ApplyAsync(preview.Plan.OperationId, preview.Plan.Version, "Review", true);
        var history = await ImportCount("catalog_foundation_revision");
        var release = (await _client.GetFromJsonAsync<ReleaseResponse>($"/api/v1/releases/{outcome.ReleaseId}"))!;
        var primary = release.Credits.First(c => c.Role == "primary");
        await ImportSql($"INSERT INTO artist_alias(artist_id,value) VALUES('{primary.ArtistId}','Alias 100%'); UPDATE artist SET source_payload_json='PRIVATE_SENTINEL' WHERE artist_id='{primary.ArtistId}'");
        var filtered = (await _client.GetFromJsonAsync<ArtistListResponse>("/api/v1/artists?labelId=source-label"))!;
        Assert.Equal(3, filtered.Total); Assert.Equal(3, filtered.Items.Select(a => a.Id).Distinct().Count());
        var remixers = (await _client.GetFromJsonAsync<ArtistListResponse>("/api/v1/artists?labelId=source-label&role=remixer"))!;
        Assert.Equal("unresolved_compound", Assert.Single(remixers.Items).IdentityStatus);
        var literal = (await _client.GetFromJsonAsync<ArtistListResponse>("/api/v1/artists?q=%25"))!;
        Assert.Equal(primary.ArtistId, Assert.Single(literal.Items).Id);
        var first = (await _client.GetFromJsonAsync<ArtistListResponse>("/api/v1/artists?pageSize=1"))!;
        var second = (await _client.GetFromJsonAsync<ArtistListResponse>("/api/v1/artists?pageSize=1&page=2"))!;
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        var detailResponse = await _client.GetAsync($"/api/v1/artists/{primary.ArtistId}");
        var detail = (await detailResponse.Content.ReadFromJsonAsync<ArtistResponse>())!;
        Assert.Equal(primary.ArtistName, detail.Name); Assert.Contains("Alias 100%", detail.Aliases);
        Assert.NotEmpty(detail.ExternalReferences); Assert.NotNull(detailResponse.Headers.ETag);
        Assert.True(detailResponse.Headers.CacheControl!.NoStore); Assert.DoesNotContain("PRIVATE", await detailResponse.Content.ReadAsStringAsync());
        var credits = (await _client.GetFromJsonAsync<ArtistCreditsResponse>($"/api/v1/artists/{primary.ArtistId}/releases?pageSize=1"))!;
        Assert.Single(credits.Items); Assert.Equal(outcome.ReleaseId, credits.Items[0].ReleaseId);
        var tracks = (await _client.GetFromJsonAsync<ArtistCreditsResponse>($"/api/v1/artists/{primary.ArtistId}/tracks"))!;
        Assert.NotEmpty(tracks.Items); Assert.All(tracks.Items, c => Assert.NotEqual("release", c.Scope));
        Assert.Equal(history, await ImportCount("catalog_foundation_revision"));
        var openapi = await _client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/v1/artists/{id}/tracks", openapi);
    }
    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("role=producer")]
    [InlineData("labelId=")]
    [InlineData("q=x&q=y")]
    [InlineData("unknown=x")]
    public async Task ArtistBrowseRejectsInvalidQueries(string query)
        => await ProblemAsync(await _client.GetAsync("/api/v1/artists?" + query), 400, "validation_failed");
    [Fact]
    public async Task ArtistBrowseRequiresAuthHandlesMissingAndGatesSchemaWithoutBreakingLabels()
    {
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync("/api/v1/artists"), 401, "unauthorized");
        await ProblemAsync(await _client.GetAsync("/api/v1/artists/missing"), 404, "artist_not_found");
        await ProblemAsync(await _client.GetAsync("/api/v1/artists/missing/tracks"), 404, "artist_not_found");
        await ProblemAsync(await _client.GetAsync("/api/v1/artists/missing/releases?role=primary"), 400, "validation_failed");
        await ImportSql("DELETE FROM schema_migration WHERE version>=9");
        await ProblemAsync(await _client.GetAsync("/api/v1/artists"), 409, "artist_schema_required");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/labels")).StatusCode);
        Assert.Equal(8, await Provider.Migrations.GetCurrentVersionAsync());
    }
}
