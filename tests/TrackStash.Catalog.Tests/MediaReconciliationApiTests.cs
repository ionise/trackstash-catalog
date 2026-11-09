using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static async Task AddReconciliationCopy(MediaFixture fixture, long id = 2, bool identicalBytes = false)
    {
        var path = Path.Combine(fixture.Root, "Library", $"copy-{id}.flac");
        byte[] bytes = identicalBytes ? [1, 2, 3, 4] : [5, 6, 7, (byte)id];
        await File.WriteAllBytesAsync(path, bytes);
        await using var connection = new SqliteConnection("Data Source=" + fixture.Scanner);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_file(media_file_id,path,content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw)
              VALUES(@id,@path,@hash,NULL,4,'2026-11-07T00:00:00Z','2026-11-07T00:00:00Z','audio-fingerprint');
            INSERT INTO metadata(media_file_id,title,subtitle,artist,album,isrc,label,release)
              VALUES(@id,'Shared tune',NULL,'Zed Primary','New release',NULL,'Test Label','New release');
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@hash", Convert.ToHexString(SHA256.HashData(bytes)));
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<(MediaReconciliationPlan Plan, string Tag)> ReconciliationPlan(MediaFixture fixture)
    {
        var page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?groupBy=release"))!;
        var key = Assert.Single(page.Groups!).Key;
        var response = await fixture.Client.GetAsync($"/api/v1/media-reconciliation?groupKey={key}&releaseId=t-new");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return ((await response.Content.ReadFromJsonAsync<MediaReconciliationPlan>())!, response.Headers.ETag!.ToString());
    }
    private static async Task<HttpResponseMessage> PrepareReconciliation(MediaFixture fixture, MediaReconciliationPlan plan, string? tag,
        params MediaTrackSelection[] selections)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media-reconciliation-reviews") {
            Content = JsonContent.Create(new { plan.GroupKey, plan.ReleaseId, selections })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }
    private static async Task<HttpResponseMessage> ApplyReconciliation(MediaFixture fixture, string id, string? tag,
        bool confirmed = true, string reason = "Reviewed Release and selected Recording versions")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/media-reconciliation-reviews/{id}/apply") {
            Content = JsonContent.Create(new { reason, confirmed })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }
    [Fact]
    public async Task Media_ReconciliationProposesAudioCopiesAndAtomicallyLinksSelectedFilesWithReleaseAndAudit()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET title='Shared tune Original Mix' WHERE media_file_id=1");
        var before = await fixture.Client.GetStringAsync("/api/v1/releases/t-new");
        var (plan, tag) = await ReconciliationPlan(fixture);
        Assert.Equal(2, plan.Files.Count);
        Assert.All(plan.Files, f => Assert.Equal("t-shared", f.SuggestedRecordingId));
        Assert.Contains(plan.Files[1].Candidates.Single(c => c.RecordingId == "t-shared").Evidence, e => e.Contains("fingerprint"));
        Assert.Equal(0, await ImportCount("enrichment_review"));
        var selected = new[] { new MediaTrackSelection(1, "t-shared"), new MediaTrackSelection(2, "t-shared") };
        await ProblemAsync(await PrepareReconciliation(fixture, plan, null, selected), 428, "precondition_required");
        var prepared = await PrepareReconciliation(fixture, plan, tag, selected);
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        Assert.Equal($"/api/v1/media-reconciliation-reviews/{review.Id}", prepared.Headers.Location!.OriginalString);
        Assert.Equal(new long[] { 1, 2 }, review.VerifiedMediaIds);
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(1, await ImportCount("enrichment_review"));
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/enrichment-reviews/{review.Id}"), 409, "media_review_required");
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/media-link-reviews/{review.Id}"), 400, "media_review_invalid");
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/media-release-reviews/{review.Id}"), 400, "media_review_invalid");
        await ProblemAsync(await ApplyReconciliation(fixture, review.Id, null), 428, "precondition_required");
        await ProblemAsync(await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString(), false), 400, "validation_failed");
        var applied = await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(2, await ImportCount("media_file"));
        Assert.Equal(2, await ImportCount("media_file_recording_match"));
        Assert.Equal(4, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(before, await fixture.Client.GetStringAsync("/api/v1/releases/t-new"));
        Assert.Equal(2, (await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!.Length);
        Assert.Single((await fixture.Client.GetFromJsonAsync<MediaReleaseAssociation[]>("/api/v1/releases/t-new/media-groups"))!);
        File.Delete(fixture.File);
        var replay = await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString());
        Assert.Equal(await applied.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        await ProblemAsync(await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString(), reason: "Different"),
            409, "enrichment_already_applied");
        Assert.NotNull((await fixture.Client.GetFromJsonAsync<MediaReconciliationReview>($"/api/v1/media-reconciliation-reviews/{review.Id}"))!.Outcome);
    }
    [Fact]
    public async Task Media_ReconciliationSupportsPartialSelectionAndResumingAnAssociatedGroupWithoutReplacingIt()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        var (plan, tag) = await ReconciliationPlan(fixture);
        var prepared = await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(2, "t-shared"));
        var review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        Assert.Equal(new long[] { 1, 2 }, review.VerifiedMediaIds);
        Assert.Equal(HttpStatusCode.OK, (await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        Assert.Equal(1, await ImportCount("media_file"));
        (plan, tag) = await ReconciliationPlan(fixture);
        Assert.True(plan.ReleaseAlreadyAssociated);
        Assert.Equal("t-shared", plan.Files[1].LinkedRecordingId);
        Assert.Null(plan.Files[1].SuggestedRecordingId);
        prepared = await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(1, "t-shared"));
        review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        Assert.Equal(2, await ImportCount("media_file"));
        Assert.Single((await fixture.Client.GetFromJsonAsync<MediaReleaseAssociation[]>("/api/v1/releases/t-new/media-groups"))!);
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/media-reconciliation?groupKey={plan.GroupKey}&releaseId=t-old"),
            409, "media_release_already_associated");
    }
    [Theory]
    [InlineData("bytes", 409, "media_changed")]
    [InlineData("scanner", 412, "enrichment_state_changed")]
    [InlineData("membership", 412, "enrichment_state_changed")]
    [InlineData("recording", 412, "enrichment_state_changed")]
    [InlineData("placement", 412, "enrichment_state_changed")]
    [InlineData("sidecar", 409, "media_sidecar_invalid")]
    [InlineData("expired", 410, "enrichment_review_expired")]
    public async Task Media_ReconciliationRejectsChangedDependenciesWithoutAnyBatchLinks(string change, int status, string code)
    {
        var clock = new MediaClock();
        await using var fixture = await StartMedia(clock);
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        var (plan, tag) = await ReconciliationPlan(fixture);
        var prepared = await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(2, "t-shared"));
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        switch (change)
        {
            case "bytes": await File.WriteAllBytesAsync(fixture.File, [8, 9, 10, 11]); break;
            case "scanner": await ScannerSql(fixture, "UPDATE metadata SET artist='Changed Artist' WHERE media_file_id=1"); break;
            case "membership": await AddReconciliationCopy(fixture, 3); break;
            case "recording": await ImportSql("UPDATE recording SET mix_name='Radio Edit' WHERE recording_id='t-shared'"); break;
            case "placement": await ImportSql("UPDATE release_recording SET display_order=99 WHERE placement_id='t-new-2'"); break;
            case "sidecar": await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Library", ".trackstash-release.json"), "{}"); break;
            case "expired": clock.Now = clock.Now.AddHours(24); break;
        }
        await ProblemAsync(await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString()), status, code);
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }
    [Fact]
    public async Task Media_ReconciliationConflictingBytesInTwoRecordingSelectionsRollBackTheEntireTransaction()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture, identicalBytes: true);
        await ScannerSql(fixture, """
            UPDATE media_file SET fingerprint_raw='different-audio',duration_seconds=400 WHERE media_file_id=2;
            UPDATE metadata SET title='Bravo tune',subtitle='Extended Mix',artist='Alpha Other' WHERE media_file_id=2;
            """);
        await ImportSql("UPDATE recording SET duration_ms=400000 WHERE recording_id='t-second'");
        var (plan, tag) = await ReconciliationPlan(fixture);
        var prepared = await PrepareReconciliation(fixture, plan, tag, new(1, "t-shared"), new(2, "t-second"));
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        await ProblemAsync(await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString()), 409, "media_identity_conflict");
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        Assert.Equal(1, await ImportCount("enrichment_review"));
    }
    [Fact]
    public async Task Media_ReconciliationDoesNotPropagateFingerprintAcrossContradictoryVersionsOrChooseAmbiguousTracks()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET subtitle='Radio Edit' WHERE media_file_id=2");
        var (plan, tag) = await ReconciliationPlan(fixture);
        Assert.Null(plan.Files[1].SuggestedRecordingId);
        await ProblemAsync(await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(2, "t-shared")), 409, "media_identity_conflict");
        await ScannerSql(fixture, "UPDATE metadata SET subtitle=NULL WHERE media_file_id=2");
        await ImportSql("""
            INSERT INTO recording(recording_id,title,mix_name,duration_ms) VALUES('ambiguous','Shared tune','Original Mix',400000);
            INSERT INTO recording_artist_credit(credit_id,recording_id,artist_id,role,position,credit_name)
              VALUES('ambiguous-credit','ambiguous','t-primary','primary',1,'Zed Primary');
            INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,legacy)
              VALUES('ambiguous-placement','t-new','ambiguous',3,0);
            """);
        (plan, tag) = await ReconciliationPlan(fixture);
        Assert.All(plan.Files, f => Assert.Null(f.SuggestedRecordingId));
        await ProblemAsync(await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(1, "t-loose")), 409, "media_identity_conflict");
        await ProblemAsync(await PrepareReconciliation(fixture, plan, tag, new(1, "t-shared"), new(1, "t-shared")), 400, "validation_failed");
        await ProblemAsync(await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(999, "t-shared")), 400, "media_candidate_unavailable");
    }
    [Fact]
    public async Task Media_ReconciliationRejectsStaleProposalAndNeverReadsUnavailableAudioDuringPreview()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        var (plan, tag) = await ReconciliationPlan(fixture);
        File.Delete(fixture.File);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync($"/api/v1/media-reconciliation?groupKey={plan.GroupKey}&releaseId=t-new")).StatusCode);
        await ScannerSql(fixture, "UPDATE metadata SET title='Changed' WHERE media_file_id=1");
        await ProblemAsync(await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(1, "t-shared")), 412, "enrichment_state_changed");
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/media-reconciliation?groupKey={plan.GroupKey}&releaseId=t-new&releaseId=t-old"),
            400, "validation_failed");
    }
    [Theory]
    [InlineData("fingerprint")]
    [InlineData("version")]
    [InlineData("isrc")]
    [InlineData("duration")]
    public async Task Media_ReconciliationRequiresCompatibleFingerprintAndNoncontradictoryCopyEvidence(string change)
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        switch (change)
        {
            case "fingerprint": await ScannerSql(fixture, "UPDATE media_file SET fingerprint_raw=NULL WHERE media_file_id=2"); break;
            case "isrc": await ScannerSql(fixture, "UPDATE metadata SET isrc='GBTRK0000099' WHERE media_file_id=2"); break;
            case "duration": await ScannerSql(fixture, "UPDATE media_file SET duration_seconds=100 WHERE media_file_id=2"); break;
            case "version": await ScannerSql(fixture, """
                CREATE TABLE acoustic_fingerprint(media_file_id INTEGER,algorithm TEXT,algorithm_version TEXT,fingerprint_raw TEXT,generation_status TEXT,generated_utc TEXT);
                INSERT INTO acoustic_fingerprint VALUES(1,'chromaprint','1','same-audio','complete','2026-11-08'),
                  (2,'chromaprint','2','same-audio','complete','2026-11-08');
                """); break;
        }
        var (plan, tag) = await ReconciliationPlan(fixture);
        Assert.Null(plan.Files[1].SuggestedRecordingId);
        await ProblemAsync(await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(2, "t-shared")), 409, "media_identity_conflict");
    }
    [Fact]
    public async Task Media_ReconciliationDoesNotUseAnAnchorAlreadyLinkedToAnotherRecording()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        var (link, linkTag) = await PrepareMedia(fixture);
        Assert.Equal(HttpStatusCode.OK, (await AcceptMedia(fixture, link, linkTag)).StatusCode);
        await ImportSql("UPDATE media_file_recording_match SET recording_id='t-second'");
        var (plan, tag) = await ReconciliationPlan(fixture);
        Assert.Null(plan.Files[1].SuggestedRecordingId);
        await ProblemAsync(await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(2, "t-shared")), 409, "media_identity_conflict");
    }
    [Fact]
    public async Task Media_ReconciliationRenamePreservesFrozenReviewAndRejectsUnexpectedRequestValues()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        var (plan, tag) = await ReconciliationPlan(fixture);
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/media-reconciliation-reviews", new {
            plan.GroupKey, plan.ReleaseId, selections = new[] { new { mediaId = 1, recordingId = "t-shared", path = "/not-allowed" } }
        });
        await ProblemAsync(response, 428, "precondition_required");
        using var unsafeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media-reconciliation-reviews") {
            Content = JsonContent.Create(new { plan.GroupKey, plan.ReleaseId, selections = new[] { new { mediaId = 1, recordingId = "t-shared", path = "/not-allowed" } } })
        };
        unsafeRequest.Headers.TryAddWithoutValidation("If-Match", tag);
        await ProblemAsync(await fixture.Client.SendAsync(unsafeRequest), 400, "validation_failed");
        var prepared = await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(1, "t-shared"));
        var review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        var settings = await fixture.Client.GetAsync("/api/v1/media-libraries");
        using var rename = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/media-libraries") {
            Content = JsonContent.Create(new { libraries = new[] { new { id = "Test", name = "My renamed collection", root = Path.Combine(fixture.Root, "Library"), enabled = true } }, confirmed = true })
        };
        rename.Headers.TryAddWithoutValidation("If-Match", settings.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.SendAsync(rename)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
    }
    [Fact]
    public async Task Media_ReconciliationEnforcesGroupAndTrackBoundsBeforeAudioReads()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        var (plan, _) = await ReconciliationPlan(fixture);
        await ImportSql(string.Join("\n", Enumerable.Range(1, 101).Select(i => $"""
            INSERT INTO recording(recording_id,title) VALUES('bound-{i}','Bound track');
            INSERT INTO release_recording(placement_id,release_id,recording_id,display_order,legacy)
              VALUES('bound-p-{i}','t-new','bound-{i}',{i + 2},0);
            """)));
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/media-reconciliation?groupKey={plan.GroupKey}&releaseId=t-new"), 409, "media_track_limit");
        await ScannerSql(fixture, """
            WITH RECURSIVE ids(n) AS (SELECT 2 UNION ALL SELECT n+1 FROM ids WHERE n<101)
            INSERT INTO media_file SELECT n,path||n,content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc,fingerprint_raw FROM ids,media_file WHERE media_file_id=1;
            INSERT INTO metadata SELECT media_file_id,'Shared tune',NULL,'Zed Primary','New release',NULL,'Test Label','New release' FROM media_file WHERE media_file_id>1;
            """);
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/media-reconciliation?groupKey={plan.GroupKey}&releaseId=t-new"), 409, "media_group_limit");
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Media_ReconciliationMissingHashesFreezeSelectedAndSupportingCopiesIndependently(int changedId)
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await ScannerSql(fixture, "UPDATE media_file SET content_hash=NULL");
        var scannerBefore = await fixture.Client.GetStringAsync("/api/v1/media");
        var (plan, tag) = await ReconciliationPlan(fixture);
        Assert.All(plan.Files, f => Assert.Equal("t-shared", f.SuggestedRecordingId));
        var prepared = await PrepareReconciliation(fixture, plan, tag, new MediaTrackSelection(2, "t-shared"));
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        Assert.Equal(new long[] { 1, 2 }, review.VerifiedMediaIds);
        var path = changedId == 1 ? fixture.File : Path.Combine(fixture.Root, "Library", "copy-2.flac");
        var original = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, [8, 9, 10, 11]);
        await ProblemAsync(await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString()), 409, "media_changed");
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        await File.WriteAllBytesAsync(path, original);
        Assert.Equal(HttpStatusCode.OK, (await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        Assert.Equal(scannerBefore, await fixture.Client.GetStringAsync("/api/v1/media"));
        var linked = Assert.Single((await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 5, 6, 7, 2 })).ToLowerInvariant(), linked.Sha256);
    }
    [Fact]
    public async Task Media_LinkedCopiesDisplayCurrentLibraryNamesFormatsAndFrozenAudioEvidenceWithoutScannerReads()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        var (plan, tag) = await ReconciliationPlan(fixture);
        var prepared = await PrepareReconciliation(fixture, plan, tag, new(1, "t-shared"), new(2, "t-shared"));
        var review = (await prepared.Content.ReadFromJsonAsync<MediaReconciliationReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await ApplyReconciliation(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        var settings = await fixture.Client.GetAsync("/api/v1/media-libraries");
        using var rename = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/media-libraries") {
            Content = JsonContent.Create(new { libraries = new[] { new { id = "Test", name = "Archive collection", root = Path.Combine(fixture.Root, "Relocated"), enabled = false } }, confirmed = true })
        };
        rename.Headers.TryAddWithoutValidation("If-Match", settings.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.SendAsync(rename)).StatusCode);
        File.Delete(fixture.Scanner);
        File.Delete(fixture.File);
        var copies = (await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!;
        Assert.Equal(2, copies.Length);
        Assert.All(copies, f => { Assert.Equal("Test", f.Library); Assert.Equal("Archive collection", f.LibraryName); Assert.NotNull(f.FingerprintKey); });
        Assert.Single(copies.Select(f => f.FingerprintKey).Distinct());
        Assert.Equal(2, copies.Select(f => f.Sha256).Distinct().Count());
        Assert.Equal(new[] { "AIFF", "FLAC" }, copies.Select(f => f.Format).Order().ToArray());
        var recording = (await fixture.Client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!;
        Assert.Equal(2, recording.LinkedCopyCount);
        foreach (var query in new[] { "releaseId=t-new", "labelId=t-label", "artistId=t-primary", "recordingId=t-shared" })
        {
            var tracks = (await fixture.Client.GetFromJsonAsync<TrackListResponse>("/api/v1/tracks?" + query))!;
            Assert.Contains(tracks.Items, t => t.RecordingId == "t-shared");
            Assert.All(tracks.Items.Where(t => t.RecordingId == "t-shared"), t => Assert.Equal(2, t.LinkedCopyCount));
        }
    }
}
