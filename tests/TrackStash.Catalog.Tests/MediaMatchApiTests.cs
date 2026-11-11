using System.Net;
using System.Net.Http.Json;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static async Task LinkAudioAnchor(MediaFixture fixture)
    {
        var (plan, tag) = await MatchPlan(fixture, 1);
        var prepared = await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "t-shared", null));
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
    }

    [Fact]
    public async Task Media_MatchesOfferExplicitLinkedAudioChoiceDespiteDifferentIsrcAndTitleTagLayout()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await AddReconciliationCopy(fixture, 3);
        await ScannerSql(fixture, """
            UPDATE metadata SET title='Shared tune',subtitle='Original Mix' WHERE media_file_id=1;
            UPDATE metadata SET title='Shared tune (Original Mix)',isrc='USAAA0000002' WHERE media_file_id IN (2,3);
            UPDATE media_file SET duration_seconds=400 WHERE media_file_id IN (2,3);
            """);
        await LinkAudioAnchor(fixture);
        var (plan, tag) = await MatchPlan(fixture, 2, 3);
        Assert.All(plan.Files, row => {
            Assert.Null(row.SuggestedRecordingId);
            var candidate = Assert.Single(row.Candidates, c => c.RecordingId == "t-shared");
            Assert.Equal("Needs manual review: matches linked audio", candidate.Status);
            Assert.Empty(candidate.Blockers);
            Assert.Contains(candidate.Evidence, e => e == "Metadata warning: Tagged ISRC conflicts.");
        });
        var choices = new[] { new MediaMatchSelection(2, "t-shared", null), new MediaMatchSelection(3, "t-shared", null) };
        var prepared = await PrepareMatches(fixture, plan, tag, choices);
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        Assert.Equal(new long[] { 1, 2, 3 }, review.VerifiedMediaIds);
        Assert.Equal(HttpStatusCode.OK, (await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        Assert.Equal(3, await ImportCount("media_file"));
        var links = (await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!;
        Assert.Equal(3, links.Length);
        Assert.Single(links.Select(l => l.FingerprintKey).Distinct());
    }

    [Theory]
    [InlineData("different-fingerprint")]
    [InlineData("changed-snapshot")]
    [InlineData("rejected-match")]
    public async Task Media_MatchesDoNotOfferLinkedAudioOverrideWithoutAcceptedMatchingSnapshot(string failure)
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await LinkAudioAnchor(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET isrc='USAAA0000002' WHERE media_file_id=2");
        if (failure == "different-fingerprint")
            await ScannerSql(fixture, "UPDATE media_file SET fingerprint_raw='different' WHERE media_file_id=2");
        else if (failure == "changed-snapshot")
            await ImportSql("UPDATE media_file SET metadata_json=json_set(metadata_json,'$.FingerprintKey','changed')");
        else
            await ImportSql("UPDATE media_file_recording_match SET override_state=2");
        var (plan, tag) = await MatchPlan(fixture, 2);
        Assert.NotEmpty(Assert.Single(plan.Files[0].Candidates, c => c.RecordingId == "t-shared").Blockers);
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(2, "t-shared", null)), 409, "media_identity_conflict");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Media_MatchesReverifyLinkedAnchorAgainstAcceptedHashBeforePreparationAndApply(bool afterPreparation)
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await LinkAudioAnchor(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET isrc='USAAA0000002' WHERE media_file_id=2");
        var (plan, tag) = await MatchPlan(fixture, 2);
        HttpResponseMessage? prepared = null;
        if (afterPreparation)
            prepared = await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(2, "t-shared", null));
        await File.WriteAllBytesAsync(fixture.File, [9, 8, 7, 6]);
        if (afterPreparation)
        {
            Assert.Equal(HttpStatusCode.Created, prepared!.StatusCode);
            var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
            await ProblemAsync(await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString()), 409, "media_changed");
        }
        else
            await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(2, "t-shared", null)), 409, "media_changed");
        Assert.Equal(1, await ImportCount("media_file"));
    }

    private static async Task<(MediaMatchPlan Plan, string Tag)> MatchPlan(MediaFixture fixture, params long[] ids)
    {
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/media-match-proposals", new { mediaIds = ids });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return ((await response.Content.ReadFromJsonAsync<MediaMatchPlan>())!, response.Headers.ETag!.ToString());
    }

    private static async Task<HttpResponseMessage> PrepareMatches(MediaFixture fixture, MediaMatchPlan plan, string? tag,
        params MediaMatchSelection[] selections)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media-match-reviews") {
            Content = JsonContent.Create(new { mediaIds = plan.Files.Select(f => f.Media.Id), selections })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AcceptMatches(MediaFixture fixture, string id, string? tag, bool confirmed = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/media-match-reviews/{id}/apply") {
            Content = JsonContent.Create(new { confirmed })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }

    [Fact]
    public async Task Media_MatchesAcceptSelectedAcrossGroupsWithoutReasonsOrReleaseAssociationAndReplay()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET release='Another edition',label='Another Label' WHERE media_file_id=2");
        var catalog = await fixture.Client.GetStringAsync("/api/v1/recordings/t-shared");
        var (plan, tag) = await MatchPlan(fixture, 1, 2);
        Assert.All(plan.Files, row => Assert.Equal("t-shared", row.SuggestedRecordingId));
        Assert.Equal(0, await ImportCount("enrichment_review"));
        var choices = new[] { new MediaMatchSelection(1, "t-shared", null), new MediaMatchSelection(2, "t-shared", null) };
        await ProblemAsync(await PrepareMatches(fixture, plan, null, choices), 428, "precondition_required");
        var prepared = await PrepareMatches(fixture, plan, tag, choices);
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        Assert.Equal($"/api/v1/media-match-reviews/{review.Id}", prepared.Headers.Location!.OriginalString);
        Assert.Equal(new long[] { 1, 2 }, review.VerifiedMediaIds);
        var reviewTag = prepared.Headers.ETag!.ToString();
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/enrichment-reviews/{review.Id}"), 409, "media_review_required");
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/media-link-reviews/{review.Id}"), 400, "media_review_invalid");
        await ProblemAsync(await AcceptMatches(fixture, review.Id, null), 428, "precondition_required");
        await ProblemAsync(await AcceptMatches(fixture, review.Id, reviewTag, false), 400, "validation_failed");
        var accepted = await AcceptMatches(fixture, review.Id, reviewTag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, await ImportCount("media_file"));
        Assert.Equal(3, await ImportCount("enrichment_apply_revision"));
        Assert.Empty((await accepted.Content.ReadFromJsonAsync<MediaMatchOutcome>())!.Classifications);
        Assert.Empty((await fixture.Client.GetFromJsonAsync<MediaReleaseAssociation[]>("/api/v1/releases/t-new/media-groups"))!);
        // The only Recording response change is its independently counted media copies.
        Assert.Contains("\"linkedCopyCount\":0", catalog);
        var detail = (await fixture.Client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!;
        Assert.Equal(2, detail.LinkedCopyCount);
        File.Delete(fixture.File);
        File.Delete(fixture.Scanner);
        var replay = await AcceptMatches(fixture, review.Id, reviewTag);
        Assert.Equal(await accepted.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.NotNull((await fixture.Client.GetFromJsonAsync<MediaMatchReview>($"/api/v1/media-match-reviews/{review.Id}"))!.Outcome);
    }

    [Fact]
    public async Task Media_MatchesAggregateComplementaryTagsAndVerifyUnselectedSupportingCopiesAcrossGroups()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await ScannerSql(fixture, """
            UPDATE metadata SET artist=NULL,isrc=NULL WHERE media_file_id=1;
            UPDATE metadata SET subtitle='Original Mix',release='Other release',label='Other Label' WHERE media_file_id=2;
            UPDATE media_file SET content_hash=NULL;
            """);
        var (plan, tag) = await MatchPlan(fixture, 1);
        Assert.Equal("t-shared", Assert.Single(plan.Files).SuggestedRecordingId);
        var candidate = Assert.Single(plan.Files[0].Candidates, c => c.RecordingId == "t-shared");
        Assert.Contains(candidate.Evidence, e => e.Contains("Composite tags"));
        var prepared = await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "t-shared", null));
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        Assert.Equal(new long[] { 1, 2 }, review.VerifiedMediaIds);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "Library", "copy-2.flac"), [9, 8, 7, 6]);
        await ProblemAsync(await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString()), 409, "media_changed");
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }

    [Theory]
    [InlineData("UPDATE metadata SET isrc='USAAA0000002' WHERE media_file_id=2")]
    [InlineData("UPDATE metadata SET subtitle='Radio Edit' WHERE media_file_id=2")]
    [InlineData("UPDATE metadata SET artist='Somebody else' WHERE media_file_id=2")]
    [InlineData("UPDATE media_file SET duration_seconds=10 WHERE media_file_id=2")]
    public async Task Media_MatchesNeverVoteAwayConflictingFingerprintCopyTags(string conflict)
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET isrc=NULL,artist=NULL WHERE media_file_id=1");
        await ScannerSql(fixture, conflict);
        var (plan, tag) = await MatchPlan(fixture, 1);
        Assert.Null(Assert.Single(plan.Files).SuggestedRecordingId);
        Assert.NotEmpty(Assert.Single(plan.Files[0].Candidates, c => c.RecordingId == "t-shared").Blockers);
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "t-shared", null)), 409, "media_identity_conflict");
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }

    [Fact]
    public async Task Media_MatchesClassifyUnmatchedFilesWithoutAudioReadsOrCatalogAdmissionAndCanClearClassification()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET title='My private bootleg',artist='Me',isrc=NULL");
        File.Delete(fixture.File);
        var labels = await fixture.Client.GetStringAsync("/api/v1/labels");
        var recordings = await ImportCount("recording");
        var (plan, tag) = await MatchPlan(fixture, 1);
        Assert.Empty(Assert.Single(plan.Files).Candidates);
        var prepared = await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, null, "unofficial"));
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        Assert.Empty(review.VerifiedMediaIds);
        Assert.Equal(HttpStatusCode.OK, (await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        (plan, tag) = await MatchPlan(fixture, 1);
        Assert.Equal("unofficial", Assert.Single(plan.Files).Classification);
        var filtered = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?classification=unofficial&groupBy=release"))!;
        Assert.Equal(1, filtered.Total);
        Assert.Single(filtered.Groups!);
        Assert.Equal(0, (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?classification=unclassified"))!.Total);
        prepared = await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, null, "unclassified"));
        review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        Assert.Equal("unclassified", Assert.Single((await MatchPlan(fixture, 1)).Plan.Files).Classification);
        Assert.Equal(labels, await fixture.Client.GetStringAsync("/api/v1/labels"));
        Assert.Equal(recordings, await ImportCount("recording"));
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(2, await ImportCount("enrichment_apply_revision"));
    }

    [Fact]
    public async Task Media_MatchesAreAtomicWhenASelectedFileChangesOrAlreadyRegisters()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        var (plan, tag) = await MatchPlan(fixture, 1, 2);
        var choices = new[] { new MediaMatchSelection(1, "t-shared", "unofficial"), new MediaMatchSelection(2, "t-shared", "unofficial") };
        var prepared = await PrepareMatches(fixture, plan, tag, choices);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "Library", "copy-2.flac"), [9, 8, 7, 6]);
        await ProblemAsync(await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString()), 409, "media_changed");
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        Assert.All((await MatchPlan(fixture, 1, 2)).Plan.Files, f => Assert.Equal("unclassified", f.Classification));
        var single = await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "t-shared", null));
        var one = (await single.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await AcceptMatches(fixture, one.Id, single.Headers.ETag!.ToString())).StatusCode);
        (plan, tag) = await MatchPlan(fixture, 1);
        Assert.True(Assert.Single(plan.Files).AlreadyRegistered);
        Assert.Null(plan.Files[0].SuggestedRecordingId);
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "t-shared", null)), 409, "media_already_linked");
    }

    [Fact]
    public async Task Media_MatchesRejectStaleMetadataAndCatalogChangesAndExpiredReview()
    {
        var clock = new MediaClock();
        await using var fixture = await StartMedia(clock);
        var (plan, tag) = await MatchPlan(fixture, 1);
        await ScannerSql(fixture, "UPDATE metadata SET artist='Changed Artist'");
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "t-shared", null)), 412, "enrichment_state_changed");
        await ScannerSql(fixture, "UPDATE metadata SET artist='Zed Primary'");
        var prepared = await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "t-shared", null));
        var review = (await prepared.Content.ReadFromJsonAsync<MediaMatchReview>())!;
        await ImportSql("UPDATE recording SET mix_name='Radio Edit' WHERE recording_id='t-shared'");
        await ProblemAsync(await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString()), 412, "enrichment_state_changed");
        clock.Now = clock.Now.AddHours(24);
        await ProblemAsync(await AcceptMatches(fixture, review.Id, prepared.Headers.ETag!.ToString()), 410, "enrichment_review_expired");
        Assert.Equal(0, await ImportCount("media_file"));
    }

    [Fact]
    public async Task Media_MatchesValidateBoundsFieldsAndSelectionIdentities()
    {
        await using var fixture = await StartMedia();
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-match-proposals", new { mediaIds = Array.Empty<long>() }), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-match-proposals", new { mediaIds = new[] { 1, 1 } }), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-match-proposals", new { mediaIds = Enumerable.Range(1, 101) }), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-match-proposals", new { mediaIds = new[] { 1 }, path = fixture.File }), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media-match-proposals", new { mediaIds = new[] { 404 } }), 404, "media_not_found");
        var (plan, tag) = await MatchPlan(fixture, 1);
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, null, null)), 400, "validation_failed");
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, null, "Not on Label")), 400, "validation_failed");
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(2, "t-shared", null)), 400, "validation_failed");
        await ProblemAsync(await PrepareMatches(fixture, plan, tag, new MediaMatchSelection(1, "not-a-candidate", null)), 409, "media_identity_conflict");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?classification=invalid"), 400, "validation_failed");
    }

    [Fact]
    public async Task Media_MatchesKeepAmbiguousSupportedRecordingsExplicitAndBlockTruncatedDiscovery()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET isrc=NULL");
        await ImportSql("""
            INSERT INTO recording(recording_id,name,title,mix_name,duration_ms)
              VALUES('ambiguous-copy','Shared tune','Shared tune','Original Mix',400000);
            INSERT INTO recording_artist_credit(credit_id,recording_id,artist_id,role,position,credit_name)
              SELECT 'ambiguous-credit', 'ambiguous-copy',artist_id,role,position,credit_name
              FROM recording_artist_credit WHERE recording_id='t-shared' AND role='primary';
            """);
        var (plan, _) = await MatchPlan(fixture, 1);
        Assert.Null(Assert.Single(plan.Files).SuggestedRecordingId);
        Assert.Equal(2, plan.Files[0].Candidates.Count(c => c.Status == "Supported"));
        await ImportSql("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<101)
            INSERT INTO recording(recording_id,name,title,mix_name,duration_ms)
              SELECT 'crowded-'||i,'Shared tune','Shared tune','Original Mix',400000 FROM n;
            """);
        var (crowded, tag) = await MatchPlan(fixture, 1);
        Assert.Null(Assert.Single(crowded.Files).SuggestedRecordingId);
        Assert.All(crowded.Files[0].Candidates, c => Assert.Contains(c.Blockers, b => b.Contains("search is incomplete")));
        await ProblemAsync(await PrepareMatches(fixture, crowded, tag, new MediaMatchSelection(1, "t-shared", null)),
            409, "media_identity_conflict");
    }

    [Fact]
    public async Task Media_MatchesPageSizeIsBoundedAndAppliesBeforeGroupingPagination()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        for (var i = 2; i <= 102; i++)
            await ScannerSql(fixture, $"""
                INSERT INTO media_file SELECT {i},path||'{i}',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw
                  FROM media_file WHERE media_file_id=1;
                INSERT INTO metadata SELECT {i},title,subtitle,artist,album,isrc,label,'Release '||printf('%03d',{i})
                  FROM metadata WHERE media_file_id=1;
                """);
        var page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?pageSize=100"))!;
        Assert.Equal(100, page.PageSize);
        Assert.Equal(102, page.Total);
        Assert.Equal(100, page.Items.Count);
        var next = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?pageSize=100&page=2"))!;
        Assert.Equal(2, next.Items.Count);
        Assert.Empty(page.Items.Select(f => f.Id).Intersect(next.Items.Select(f => f.Id)));
        var groups = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?pageSize=100&groupBy=release"))!;
        Assert.Equal(100, groups.Groups!.Count);
        Assert.Equal(102, groups.GroupTotal);
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?pageSize=101"), 400, "validation_failed");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?pageSize=invalid"), 400, "validation_failed");
    }
}
