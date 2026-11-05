using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using TrackStash.Catalog.Api;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    [Fact]
    public async Task ReleaseBrowse_ListSearchLabelFilterStablePagingAndReadOnlyPublicProjection()
    {
        var service = await ImportService(); var preview = await Reviewed(service);
        var outcome = await service.ApplyAsync(preview.Plan.OperationId, preview.Plan.Version, "Review", true);
        await ImportSql("""
            INSERT INTO label(label_id,name) VALUES('other-label','Other label');
            INSERT INTO release(release_id,title,catalog_number,source_payload_json) VALUES
              ('other-release','Another release','OTHER01','{"private":"PRIVATE_SENTINEL"}'),
              ('same-title','Test release','CAT002',NULL);
            INSERT INTO release_label_link(release_id,label_id,is_primary) VALUES('other-release','other-label',1),('same-title','source-label',1);
            """);
        var first = await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases?page=1&pageSize=1");
        var second = await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases?page=2&pageSize=1");
        Assert.Equal(3, first!.Total); Assert.Equal("other-release", Assert.Single(first.Items).Id);
        Assert.NotEqual(first.Items[0].Id, Assert.Single(second!.Items).Id);
        var filtered = await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases?labelId=source-label");
        Assert.Equal(2, filtered!.Total); Assert.All(filtered.Items, r => Assert.Equal("source-label", r.LabelId));
        Assert.Equal(2, filtered.Items.Single(r => r.Id == outcome.ReleaseId).PlacementCount);
        var catalogNumber = await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases?q=cat001");
        Assert.Equal(outcome.ReleaseId, Assert.Single(catalogNumber!.Items).Id);
        var literal = await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases?q=%25");
        Assert.Empty(literal!.Items);
        var empty = await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases?labelId=missing");
        Assert.Empty(empty!.Items);
        var response = await _client.GetAsync("/api/v1/releases");
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await ImportCount("release_import_outcome"));
    }
    [Fact]
    public async Task ReleaseBrowse_DetailTracksExposeScopesMetadataAndPreserveManualOrderWithoutWrites()
    {
        var service = await ImportService(); var preview = await Reviewed(service);
        var outcome = await service.ApplyAsync(preview.Plan.OperationId, preview.Plan.Version, "Review", true);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var release = (await unit.Releases.GetByIdAsync(outcome.ReleaseId))!;
            var placements = await unit.CatalogFoundations!.GetPlacementsAsync(outcome.ReleaseId);
            await unit.CatalogFoundations.ReorderPlacementsAsync(outcome.ReleaseId, release.Version, placements.Reverse().Select(p => p.Id).ToArray());
            await unit.CommitAsync();
        }
        var history = await ImportCount("catalog_foundation_revision");
        var detailResponse = await _client.GetAsync($"/api/v1/releases/{outcome.ReleaseId}");
        var detail = (await detailResponse.Content.ReadFromJsonAsync<ReleaseResponse>())!;
        Assert.NotNull(detailResponse.Headers.ETag); Assert.True(detailResponse.Headers.CacheControl!.NoStore);
        Assert.Equal("Test release", detail.Title); Assert.Equal("CAT001", detail.CatalogNumber); Assert.Equal("012345", detail.Upc);
        Assert.Equal("2020-01-01", detail.PublishDate); Assert.Equal("2020-01-02", detail.NewReleaseDate);
        Assert.Equal("source-label", Assert.Single(detail.Labels).Id);
        Assert.Equal(3, detail.Credits.Count); Assert.Contains(detail.Credits, c => c.Role == "remixer" && c.IdentityStatus == "unresolved_compound");
        var listing = (await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases?labelId=source-label"))!;
        var summary = Assert.Single(listing.Items);
        Assert.Equal(detail.Credits.Where(c => c.Role == "primary").ToArray(), summary.PrimaryArtists);
        Assert.NotEmpty(summary.PrimaryArtists); Assert.Null(summary.CoverAssetId);
        var trackResponse = await _client.GetAsync($"/api/v1/releases/{outcome.ReleaseId}/tracks?page=1&pageSize=1");
        var tracks = (await trackResponse.Content.ReadFromJsonAsync<ReleaseTracksResponse>())!;
        var track = Assert.Single(tracks.Items);
        Assert.Equal(2, tracks.Total); Assert.Equal(2, track.SourcePosition); Assert.Equal(1, track.DisplayOrder);
        Assert.Equal("manual", track.OrderSource); Assert.Null(track.TrackNumber);
        Assert.Equal(3, track.Credits.Count); Assert.Contains(track.Credits, c => c.Role == "remixer");
        Assert.Equal("102", Assert.Single(track.ExternalReferences).ExternalId);
        Assert.DoesNotContain("PRIVATE", await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal(history, await ImportCount("catalog_foundation_revision"));
        var openapi = await _client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/v1/releases/{id}/tracks", openapi); Assert.Contains("ReleaseResponse", openapi);
    }
    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=1&page=2")]
    [InlineData("unknown=x")]
    [InlineData("labelId=")]
    public async Task ReleaseBrowse_InvalidQueriesFailExplicitly(string query)
        => await ProblemAsync(await _client.GetAsync("/api/v1/releases?" + query), 400, "validation_failed");

    [Fact]
    public async Task ReleaseBrowse_RequiresAuthMissingIs404AndSchemaEightStillRunsLabelRoutes()
    {
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync("/api/v1/releases"), 401, "unauthorized");
        await ProblemAsync(await _client.GetAsync("/api/v1/releases/missing"), 404, "release_not_found");
        await ProblemAsync(await _client.GetAsync("/api/v1/releases/missing/tracks"), 404, "release_not_found");
        await ProblemAsync(await _client.GetAsync("/api/v1/releases/missing/tracks?q=no"), 400, "validation_failed");
        await ImportSql("DELETE FROM schema_migration WHERE version>=9");
        await ProblemAsync(await _client.GetAsync("/api/v1/releases"), 409, "release_schema_required");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/labels")).StatusCode);
        Assert.Equal(8, await Provider.Migrations.GetCurrentVersionAsync());
    }
}
