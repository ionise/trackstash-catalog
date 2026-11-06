using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private const string TrackFixture = """
        INSERT INTO artist(artist_id,name) VALUES('t-primary','Zed Primary'),('t-remix','Remixer Person'),('t-other','Alpha Other');
        INSERT INTO label(label_id,name) VALUES('t-label','Track label');
        INSERT INTO release(release_id,title,publish_date,new_release_date) VALUES
          ('t-old','Old release','2020-01-01',NULL),('t-new','New release','2024-06-01',NULL),('t-undated','Undated release',NULL,NULL);
        INSERT INTO release_label_link(release_id,label_id,is_primary) VALUES('t-new','t-label',1);
        INSERT INTO recording(recording_id,title,mix_name,isrc) VALUES
          ('t-shared','Shared tune','Original Mix','GBTRK0000001'),('t-second','Bravo tune','Extended Mix',NULL),
          ('t-loose','Loose tune',NULL,NULL);
        INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,source_position,legacy) VALUES
          ('t-old-1','t-old','t-shared',1,1,0),('t-new-2','t-new','t-shared',2,2,0),('t-new-1','t-new','t-second',1,1,0),
          ('t-undated-1','t-undated','t-second',1,1,0);
        INSERT INTO recording_artist_credit(credit_id,recording_id,artist_id,role,position,credit_name) VALUES
          ('t-c1','t-shared','t-primary','primary',1,'Zed Primary'),('t-c2','t-second','t-other','primary',1,'Alpha Other'),
          ('t-c3','t-loose','t-remix','remixer',1,'Remixer Person');
        INSERT INTO placement_artist_credit(credit_id,placement_id,artist_id,role,position,credit_name) VALUES
          ('t-p1','t-new-2','t-remix','remixer',1,'Remix spelling');
        """;

    private async Task<TrackListResponse> Tracks(string query) =>
        (await _client.GetFromJsonAsync<TrackListResponse>($"/api/v1/tracks?{query}"))!;

    [Fact]
    public async Task TrackListFiltersByReleaseLabelArtistAndRecordingWithoutWrites()
    {
        await ImportSql(TrackFixture);
        var history = await ImportCount("catalog_foundation_revision");
        var response = await _client.GetAsync("/api/v1/tracks?releaseId=t-new");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var release = (await response.Content.ReadFromJsonAsync<TrackListResponse>())!;
        Assert.Equal(["t-new-1", "t-new-2"], release.Items.Select(i => i.PlacementId));
        var shared = release.Items[1];
        Assert.Equal(("Shared tune", "Original Mix", "GBTRK0000001", 2), (shared.Title, shared.MixName, shared.Isrc, shared.ReleaseCount));
        Assert.Equal("t-label", Assert.Single(shared.Labels).Id);
        Assert.Equal("Remix spelling", Assert.Single(shared.Credits).CreditedName);
        Assert.Equal("t-primary", Assert.Single(shared.RecordingCredits).ArtistId);
        Assert.Empty(shared.ArtistCredits);

        Assert.Equal(["t-new-2", "t-new-1"], (await Tracks("labelId=t-label&sort=title&order=desc")).Items.Select(i => i.PlacementId));
        var remixer = await Tracks("artistId=t-remix&role=remixer");
        Assert.Equal(["t-new-2", null], remixer.Items.Select(i => i.PlacementId));
        Assert.Equal(new ArtistAppearanceCreditResponse("remixer", "placement", "Remix spelling"), Assert.Single(remixer.Items[0].ArtistCredits));
        Assert.Equal(new ArtistAppearanceCreditResponse("remixer", "recording", "Remixer Person"), Assert.Single(remixer.Items[1].ArtistCredits));
        Assert.Equal(0, (await Tracks("artistId=t-remix&role=primary")).Total);
        Assert.Equal(["t-new-2", "t-old-1"], (await Tracks("recordingId=t-shared")).Items.Select(i => i.PlacementId));
        Assert.Equal(history, await ImportCount("catalog_foundation_revision"));
    }

    [Fact]
    public async Task TrackListSearchesSortsAndGroupsByRecording()
    {
        await ImportSql(TrackFixture);
        Assert.Equal(["t-new-2", "t-old-1"], (await Tracks("q=shared+original")).Items.Select(i => i.PlacementId));
        Assert.Equal(["t-new-2", "t-old-1"], (await Tracks("q=GBTRK0000001")).Items.Select(i => i.PlacementId));
        Assert.Equal(["t-new-2", "t-new-1"], (await Tracks("q=new+release")).Items.Select(i => i.PlacementId).Reverse());
        Assert.Equal(["t-new-2", "t-old-1"], (await Tracks("q=zed")).Items.Select(i => i.PlacementId));
        Assert.Equal(0, (await Tracks("q=shared+missing")).Total);

        var grouped = await Tracks("q=tune&groupBy=recording");
        Assert.Equal(3, grouped.Total);
        var shared = Assert.Single(grouped.Items, i => i.RecordingId == "t-shared");
        Assert.Equal(("t-new", 2), (shared.ReleaseId, shared.ReleaseCount));
        Assert.Equal(["t-second", "t-shared", "t-loose"], grouped.Items.Select(i => i.RecordingId));
        Assert.Equal(["t-second", "t-shared", "t-loose"], (await Tracks("q=tune&groupBy=recording&sort=date&order=asc")).Items.Select(i => i.RecordingId));
        Assert.Equal(["t-second", "t-loose", "t-shared"], (await Tracks("q=tune&groupBy=recording&sort=title")).Items.Select(i => i.RecordingId));
        Assert.Equal(["t-second", "t-shared", "t-loose"], (await Tracks("q=tune&groupBy=recording&sort=artist")).Items.Select(i => i.RecordingId));
        Assert.Equal(["t-second", "t-shared", "t-loose"], (await Tracks("q=tune&groupBy=recording&sort=release")).Items.Select(i => i.RecordingId));
        var paged = await Tracks("q=tune&groupBy=recording&pageSize=1&page=2");
        Assert.Equal((3, "t-shared"), (paged.Total, Assert.Single(paged.Items).RecordingId));
    }

    [Theory]
    [InlineData("unknown=1")]
    [InlineData("page=1&page=2")]
    [InlineData("page=0")]
    [InlineData("pageSize=201")]
    [InlineData("groupBy=release")]
    [InlineData("order=up")]
    [InlineData("sort=bpm")]
    [InlineData("sort=order")]
    [InlineData("role=primary")]
    [InlineData("artistId=t-remix&role=producer")]
    [InlineData("releaseId=")]
    public async Task TrackListRejectsInvalidQueries(string query)
    {
        await ProblemAsync(await _client.GetAsync($"/api/v1/tracks?{query}"), 400, "validation_failed");
    }

    [Theory]
    [InlineData("releaseId", "release_not_found")]
    [InlineData("labelId", "label_not_found")]
    [InlineData("artistId", "artist_not_found")]
    [InlineData("recordingId", "recording_not_found")]
    public async Task TrackListReportsMissingFilterEntities(string field, string code)
    {
        await ProblemAsync(await _client.GetAsync($"/api/v1/tracks?{field}=missing"), 404, code);
    }

    [Fact]
    public async Task RecordingDetailReturnsCreditsAndReleaseCount()
    {
        await ImportSql(TrackFixture);
        var response = await _client.GetAsync("/api/v1/recordings/t-shared");
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.NotNull(response.Headers.ETag);
        var recording = (await response.Content.ReadFromJsonAsync<RecordingResponse>())!;
        Assert.Equal(("Shared tune", "Original Mix", "GBTRK0000001", 2), (recording.Title, recording.MixName, recording.Isrc, recording.ReleaseCount));
        Assert.Equal("t-primary", Assert.Single(recording.Credits).ArtistId);
        Assert.Equal(0, (await _client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-loose"))!.ReleaseCount);
        await ProblemAsync(await _client.GetAsync("/api/v1/recordings/missing"), 404, "recording_not_found");
    }

    [Fact]
    public async Task TrackEndpointsRequireTheApiKey()
    {
        using var anonymous = _app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tracks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/recordings/x")).StatusCode);
    }
}
