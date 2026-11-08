using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static async Task AddBrowseMetadata(MediaFixture fixture)
    {
        await ScannerSql(fixture, """
            ALTER TABLE metadata ADD COLUMN label TEXT;
            ALTER TABLE metadata ADD COLUMN release TEXT;
            ALTER TABLE media_file ADD COLUMN fingerprint_raw TEXT;
            UPDATE metadata SET label='Test Label',release='New release';
            UPDATE media_file SET fingerprint_raw='audio-fingerprint';
            """);
    }
    [Fact]
    public async Task Media_BrowseGroupsWholeInventoryBeforePagingAndKeepsReleaseLabelsDistinct()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        for (var i = 2; i <= 28; i++)
            await ScannerSql(fixture, $"""
                INSERT INTO media_file SELECT {i},path||'{i}',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw FROM media_file WHERE media_file_id=1;
                INSERT INTO metadata SELECT {i},title,subtitle,artist,album,isrc,label,release FROM metadata WHERE media_file_id=1;
                """);
        var page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release"))!;
        var group = Assert.Single(page.Groups!);
        Assert.Equal(28, group.FileCount);
        Assert.Equal(1, page.GroupTotal);
        Assert.Empty(page.Items);
        var detail = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>($"/api/v1/media?groupBy=release&groupKey={group.Key}&page=2"))!;
        Assert.Equal(28, detail.Total);
        Assert.Equal(3, detail.Items.Count);
        await ScannerSql(fixture, "UPDATE metadata SET label='Another Label' WHERE media_file_id=28");
        page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release&sort=label"))!;
        Assert.Equal(2, page.GroupTotal);
        Assert.Equal("Another Label", page.Groups![0].Label);
        page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release&sort=label&direction=desc"))!;
        Assert.Equal("Test Label", page.Groups![0].Label);
        page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?label=Another&release=New&artist=Zed&track=Shared"))!;
        Assert.Single(page.Items);
        Assert.Equal(28, page.Items[0].Id);
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?groupBy=invalid"), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?sort=path"), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?label=a&label=b"), 400, "validation_failed");
        await ScannerSql(fixture, "UPDATE metadata SET release='Release '||printf('%03d',media_file_id)");
        page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release&sort=release"))!;
        Assert.Equal(28, page.GroupTotal);
        Assert.Equal(25, page.Groups!.Count);
        Assert.Equal("Release 001", page.Groups[0].Name);
        var second = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release&sort=release&page=2"))!;
        Assert.Equal(3, second.Groups!.Count);
        Assert.Equal("Release 026", second.Groups[0].Name);
        Assert.Empty(page.Groups.Select(g => g.Key).Intersect(second.Groups.Select(g => g.Key)));
    }
    [Fact]
    public async Task Media_FingerprintGroupsRequireExactAlgorithmVersionEvidenceAndMissingFilesStaySeparate()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await ScannerSql(fixture, """
            INSERT INTO media_file SELECT 2,path||'2',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw FROM media_file WHERE media_file_id=1;
            INSERT INTO media_file SELECT 3,path||'3',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,NULL FROM media_file WHERE media_file_id=1;
            INSERT INTO media_file SELECT 4,path||'4',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,NULL FROM media_file WHERE media_file_id=1;
            CREATE TABLE acoustic_fingerprint(media_file_id INTEGER,algorithm TEXT,algorithm_version TEXT,fingerprint_raw TEXT,generation_status TEXT,generated_utc TEXT);
            INSERT INTO acoustic_fingerprint VALUES(1,'chromaprint','1','same-audio','complete','2026-01-01'),
              (2,'chromaprint','1','same-audio','complete','2026-01-01');
            """);
        var page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=fingerprint"))!;
        Assert.Equal(3, page.GroupTotal);
        Assert.Single(page.Groups!, g => g.FileCount == 2);
        await ScannerSql(fixture, "UPDATE acoustic_fingerprint SET algorithm_version='2' WHERE media_file_id=2");
        page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=fingerprint"))!;
        Assert.Equal(4, page.GroupTotal);
        Assert.All(page.Groups!, g => Assert.Equal(1, g.FileCount));
    }
    private static async Task<(MediaReleaseReview Review, string Tag)> PrepareReleaseGroup(MediaFixture fixture)
    {
        var groups = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release"))!;
        var key = Assert.Single(groups.Groups!).Key;
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/media-release-reviews", new { groupKey = key, releaseId = "t-new" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var review = (await response.Content.ReadFromJsonAsync<MediaReleaseReview>())!;
        Assert.Equal("/api/v1/media-release-reviews/" + review.Id, response.Headers.Location!.OriginalString);
        return (review, response.Headers.ETag!.ToString());
    }
    private static async Task<HttpResponseMessage> AcceptReleaseGroup(MediaFixture fixture, MediaReleaseReview review, string? tag,
        string reason = "Reviewed the entire embedded release group", bool confirmed = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/media-release-reviews/{review.Id}/apply") {
            Content = JsonContent.Create(new { reason, confirmed })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }
    [Fact]
    public async Task Media_ReleaseAssociationIsExplicitAuditedReplayableAndDoesNotLinkRecordings()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        var before = await fixture.Client.GetStringAsync("/api/v1/releases/t-new");
        var (review, tag) = await PrepareReleaseGroup(fixture);
        Assert.Single(review.Files);
        await ProblemAsync(await AcceptReleaseGroup(fixture, review, null), 428, "precondition_required");
        await ProblemAsync(await AcceptReleaseGroup(fixture, review, tag, confirmed: false), 400, "validation_failed");
        Assert.Empty((await fixture.Client.GetFromJsonAsync<MediaReleaseAssociation[]>("/api/v1/releases/t-new/media-groups"))!);
        var response = await AcceptReleaseGroup(fixture, review, tag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await fixture.Client.GetStringAsync("/api/v1/releases/t-new"));
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
        Assert.Equal(1, await ImportCount("enrichment_apply_revision"));
        var associations = (await fixture.Client.GetFromJsonAsync<MediaReleaseAssociation[]>("/api/v1/releases/t-new/media-groups"))!;
        Assert.Equal(review.Id, Assert.Single(associations).ReviewId);
        var saved = (await fixture.Client.GetFromJsonAsync<MediaReleaseReview>($"/api/v1/media-release-reviews/{review.Id}"))!;
        Assert.NotNull(saved.Outcome);
        var groups = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release"))!;
        Assert.Equal("t-new", Assert.Single(groups.Groups!).AssociatedReleaseId);
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-release-reviews", new { groupKey = review.GroupKey, releaseId = "t-old" }),
            409, "media_release_already_associated");
        await ScannerSql(fixture, "UPDATE metadata SET release='Renamed'");
        Assert.Equal(await response.Content.ReadAsStringAsync(), await (await AcceptReleaseGroup(fixture, review, tag)).Content.ReadAsStringAsync());
        await ProblemAsync(await AcceptReleaseGroup(fixture, review, tag, reason: "Changed intent"), 409, "enrichment_already_applied");
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/enrichment-reviews/{review.Id}"), 409, "media_review_required");
    }
    [Theory]
    [InlineData("scanner")]
    [InlineData("catalog")]
    [InlineData("membership")]
    [InlineData("expiry")]
    [InlineData("audit")]
    public async Task Media_ReleaseAssociationChecksGroupCatalogExpiryAndRollsBackOnFailure(string change)
    {
        var clock = new MediaClock();
        await using var fixture = await StartMedia(clock);
        await AddBrowseMetadata(fixture);
        var (review, tag) = await PrepareReleaseGroup(fixture);
        switch (change)
        {
            case "scanner": await ScannerSql(fixture, "UPDATE metadata SET title='Changed title'"); break;
            case "catalog": await ImportSql("UPDATE release SET title='Changed title' WHERE release_id='t-new'"); break;
            case "membership":
                await ScannerSql(fixture, """
                    INSERT INTO media_file SELECT 2,path||'2',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw FROM media_file WHERE media_file_id=1;
                    INSERT INTO metadata SELECT 2,title,subtitle,artist,album,isrc,label,release FROM metadata WHERE media_file_id=1;
                    """);
                break;
            case "expiry": clock.Now = clock.Now.AddHours(24); break;
            case "audit": await ImportSql("CREATE TRIGGER fail_group_audit BEFORE INSERT ON enrichment_apply_revision BEGIN SELECT RAISE(ABORT,'test failure'); END;"); break;
        }
        var response = await AcceptReleaseGroup(fixture, review, tag);
        Assert.Equal(change switch {
            "expiry" => HttpStatusCode.Gone, "audit" => HttpStatusCode.InternalServerError,
            _ => HttpStatusCode.PreconditionFailed
        }, response.StatusCode);
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(0, await ImportCount("media_file"));
    }
    [Fact]
    public async Task Media_ReleaseGroupReviewIncludesExactlyHundredFilesAndRejectsHundredAndOne()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        for (var i = 2; i <= 100; i++)
            await ScannerSql(fixture, $"""
                INSERT INTO media_file SELECT {i},path||'{i}',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw FROM media_file WHERE media_file_id=1;
                INSERT INTO metadata SELECT {i},title,subtitle,artist,album,isrc,label,release FROM metadata WHERE media_file_id=1;
                """);
        var (review, _) = await PrepareReleaseGroup(fixture);
        Assert.Equal(100, review.Files.Count);
        await ScannerSql(fixture, """
            INSERT INTO media_file SELECT 101,path||'101',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw FROM media_file WHERE media_file_id=1;
            INSERT INTO metadata SELECT 101,title,subtitle,artist,album,isrc,label,release FROM metadata WHERE media_file_id=1;
            """);
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-release-reviews", new { groupKey = review.GroupKey, releaseId = "t-new" }),
            409, "media_group_limit");
        Assert.Equal(1, await ImportCount("enrichment_review"));
    }
    [Fact]
    public async Task Media_ReleaseReviewRejectsClientMetadataAndRequiresAuthentication()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        using var anonymous = fixture.App.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/media-release-reviews", new { groupKey = "any", releaseId = "t-new" })).StatusCode);
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-release-reviews",
            new { groupKey = "any", releaseId = "t-new", files = new[] { 1 } }), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-release-reviews",
            new { groupKey = "unknown", releaseId = "t-new" }), 404, "media_group_not_found");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media-release-reviews/unknown"), 404, "media_review_not_found");
    }
}
