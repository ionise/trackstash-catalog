using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private sealed class MediaFixture(string root, WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public string Root => root;
        public string File => Path.Combine(root, "Library", "tune.aiff");
        public string Scanner => Path.Combine(root, "scanner.db");
        public HttpClient Client => client;
        public WebApplication App => app;
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(root, true);
        }
    }
    private sealed class MediaClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-07T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private async Task<MediaFixture> StartMedia(MediaClock? clock = null)
    {
        await ImportSql(TrackFixture);
        await ImportSql("UPDATE recording SET duration_ms=400000 WHERE recording_id='t-shared'");
        var temp = Path.GetTempPath();
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/", StringComparison.Ordinal)) temp = "/private" + temp;
        var root = Path.Combine(temp, "trackstash-media-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        var file = Path.Combine(root, "Library", "tune.aiff");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
        await using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "scanner.db")))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE media_file(media_file_id INTEGER PRIMARY KEY,path TEXT,content_hash TEXT,duration_seconds REAL,
                  size_bytes INTEGER,scanned_utc TEXT,last_modified_utc TEXT);
                CREATE TABLE metadata(media_file_id INTEGER PRIMARY KEY,title TEXT,subtitle TEXT,artist TEXT,album TEXT,isrc TEXT);
                INSERT INTO media_file VALUES(1,@path,@hash,400,4,'2026-11-07T00:00:00Z','2026-11-07T00:00:00Z');
                INSERT INTO metadata VALUES(1,'Shared tune (Original Mix)',NULL,'Zed Primary','New release','GBTRK0000001');
                """;
            command.Parameters.AddWithValue("@path", file);
            command.Parameters.AddWithValue("@hash", hash);
            await command.ExecuteNonQueryAsync();
        }
        var app = Build(builder => {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["CatalogApi:ScannerDatabasePath"] = Path.Combine(root, "scanner.db"),
                ["CatalogApi:MediaLibrarySettingsPath"] = Path.Combine(root, "libraries.json"),
                ["CatalogApi:MediaLibraries:Test"] = Path.Combine(root, "Library")
            });
            if (clock is not null) builder.Services.AddSingleton<TimeProvider>(clock);
        });
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        return new(root, app, client);
    }
    private static async Task ScannerSql(MediaFixture fixture, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + fixture.Scanner);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<(MediaLinkReview Review, string Tag)> PrepareMedia(MediaFixture fixture)
    {
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/media/1/link-reviews", new { recordingId = "t-shared" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var review = (await response.Content.ReadFromJsonAsync<MediaLinkReview>())!;
        Assert.Equal("/api/v1/media-link-reviews/" + review.Id, response.Headers.Location!.OriginalString);
        return (review, response.Headers.ETag!.ToString());
    }
    private static async Task<HttpResponseMessage> AcceptMedia(MediaFixture fixture, MediaLinkReview review, string? tag,
        bool confirmed = true, string reason = "Confirmed the same Recording and version")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media-link-reviews/" + review.Id + "/apply") {
            Content = JsonContent.Create(new { reason, confirmed })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }
    [Fact]
    public async Task Media_MetadataCandidatesVerifiedReviewAndAcceptancePreserveCatalogAndReplay()
    {
        await using var fixture = await StartMedia();
        var before = (await fixture.Client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!;
        var page = await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?library=Test");
        Assert.Equal(1, page!.Total);
        Assert.Equal("tune.aiff", page.Items[0].RelativePath);
        var candidates = await fixture.Client.GetFromJsonAsync<MediaCandidates>("/api/v1/media/1/candidates");
        Assert.Equal("t-shared", candidates!.Candidates[0].RecordingId);
        Assert.Empty(candidates.Candidates[0].Blockers);
        Assert.Contains(candidates.Candidates[0].Evidence, e => e.Contains("ISRC"));
        var (review, tag) = await PrepareMedia(fixture);
        Assert.Equal(0, await ImportCount("media_file"));
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/enrichment-reviews/" + review.Id), 409, "media_review_required");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/enrichment-track-reviews/" + review.Id), 400, "enrichment_batch_required");
        await ProblemAsync(await AcceptMedia(fixture, review, null), 428, "precondition_required");
        await ProblemAsync(await AcceptMedia(fixture, review, tag, confirmed: false), 400, "validation_failed");
        var applied = await AcceptMedia(fixture, review, tag);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(1, await ImportCount("media_file"));
        Assert.Equal(1, await ImportCount("media_file_recording_match"));
        Assert.Equal(1, await ImportCount("enrichment_apply_revision"));
        var after = (await fixture.Client.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(1, after.LinkedCopyCount);
        var links = await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media");
        var link = Assert.Single(links!);
        Assert.Equal(fixture.File, link.Path);
        Assert.Equal(review.VerifiedSha256, link.Sha256);
        File.Delete(fixture.File);
        var replay = await AcceptMedia(fixture, review, tag);
        Assert.Equal(await applied.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        await ProblemAsync(await AcceptMedia(fixture, review, tag, reason: "Changed reason"), 409, "enrichment_already_applied");
        var saved = await fixture.Client.GetFromJsonAsync<MediaLinkReview>("/api/v1/media-link-reviews/" + review.Id);
        Assert.NotNull(saved!.Outcome);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Media_MissingScannerHashIsCalculatedFrozenAndRecheckedWithoutScannerWrites(string? missing)
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, missing is null ? "UPDATE media_file SET content_hash=NULL" : $"UPDATE media_file SET content_hash='{missing}'");
        var scannerBefore = await fixture.Client.GetStringAsync("/api/v1/media");
        var (review, tag) = await PrepareMedia(fixture);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant(), review.VerifiedSha256);
        Assert.Equal(missing, review.Media.ContentHash);
        Assert.Equal(scannerBefore, await fixture.Client.GetStringAsync("/api/v1/media"));
        await File.WriteAllBytesAsync(fixture.File, [4, 3, 2, 1]);
        await ProblemAsync(await AcceptMedia(fixture, review, tag), 409, "media_changed");
        Assert.Equal(0, await ImportCount("media_file"));
        await File.WriteAllBytesAsync(fixture.File, [1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.OK, (await AcceptMedia(fixture, review, tag)).StatusCode);
        Assert.Equal(scannerBefore, await fixture.Client.GetStringAsync("/api/v1/media"));
    }
    [Fact]
    public async Task Media_InvalidNonemptyScannerHashIsNotTreatedAsMissing()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE media_file SET content_hash='invalid-hash'");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/link-reviews", new { recordingId = "t-shared" }),
            409, "media_hash_invalid");
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }
    [Theory]
    [InlineData("bytes")]
    [InlineData("scanner")]
    [InlineData("catalog")]
    [InlineData("missing")]
    public async Task Media_ChangedFileScannerOrRecordingBlocksApplyWithoutPartialWrites(string change)
    {
        await using var fixture = await StartMedia();
        var (review, tag) = await PrepareMedia(fixture);
        switch (change)
        {
            case "bytes": await File.WriteAllBytesAsync(fixture.File, [4, 3, 2, 1]); break;
            case "scanner": await ScannerSql(fixture, "UPDATE metadata SET title='Different title'"); break;
            case "catalog": await ImportSql("UPDATE recording SET mix_name='Different mix' WHERE recording_id='t-shared'"); break;
            case "missing": File.Delete(fixture.File); break;
        }
        var response = await AcceptMedia(fixture, review, tag);
        Assert.Equal(change is "bytes" or "missing" ? HttpStatusCode.Conflict : HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }
    [Theory]
    [InlineData("duration")]
    [InlineData("mix")]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("contradictoryMixInSubtitle")]
    public async Task Media_ContradictoryTagsAreBlockedDespiteIsrcAgreement(string change)
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, change switch {
            "duration" => "UPDATE media_file SET duration_seconds=999",
            "mix" => "UPDATE metadata SET title='Shared tune',subtitle='Radio Edit'",
            "artist" => "UPDATE metadata SET artist='Different Artist'",
            "contradictoryMixInSubtitle" => "UPDATE metadata SET subtitle='Radio Edit'",
            _ => "UPDATE metadata SET title='Wrong tune'",
        });
        var candidates = await fixture.Client.GetFromJsonAsync<MediaCandidates>("/api/v1/media/1/candidates");
        Assert.NotEmpty(candidates!.Candidates[0].Blockers);
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/link-reviews", new { recordingId = "t-shared" }),
            409, "media_identity_conflict");
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }
    [Fact]
    public async Task Media_RejectsSymlinksUnconfiguredLibrariesAndClientPaths()
    {
        await using var fixture = await StartMedia();
        using var anonymous = fixture.App.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/media")).StatusCode);
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/link-reviews",
            new { recordingId = "t-shared", path = "/arbitrary/file" }), 400, "validation_failed");
        var outside = Path.Combine(fixture.Root, "outside.aiff");
        File.Move(fixture.File, outside);
        File.CreateSymbolicLink(fixture.File, outside);
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/link-reviews", new { recordingId = "t-shared" }),
            409, "media_path_unsafe");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?library=Unknown"), 400, "validation_failed");
    }
    private static Task WriteMediaSidecar(MediaFixture fixture, string mix = "Original Mix", bool duplicate = false)
    {
        var localFile = new { mediaFileId = 1, currentRelativePath = "Library/tune.aiff" };
        return File.WriteAllTextAsync(Path.Combine(fixture.Root, "Library", ".trackstash-release.json"), JsonSerializer.Serialize(new {
            schemaVersion = 4, artifactType = "trackstash.release-directory",
            identity = new { provider = "External" },
            tracks = new[] { new {
                title = "Shared tune", mixName = mix, providerTrackId = (string?)null,
                normalized = new { isrc = "GBTRK0000001" },
                localFiles = duplicate ? new[] { localFile, localFile } : new[] { localFile }
            } }
        }));
    }
    [Theory]
    [InlineData("mix")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    public async Task Media_SidecarConflictsAndInvalidIdentitiesCannotBeAccepted(string variant)
    {
        await using var fixture = await StartMedia();
        switch (variant)
        {
            case "mix": await WriteMediaSidecar(fixture, "Radio Edit"); break;
            case "duplicate": await WriteMediaSidecar(fixture, duplicate: true); break;
            case "malformed": await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Library", ".trackstash-release.json"), "{}"); break;
            case "oversized": await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Library", ".trackstash-release.json"), new string(' ', 1024 * 1024 + 1)); break;
        }
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/link-reviews", new { recordingId = "t-shared" }),
            409, variant == "mix" ? "media_identity_conflict" : "media_sidecar_invalid");
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("enrichment_review"));
    }
    [Fact]
    public async Task Media_SidecarChangeAndReviewExpiryRequireFreshReview()
    {
        var clock = new MediaClock();
        await using var fixture = await StartMedia(clock);
        await WriteMediaSidecar(fixture);
        var (review, tag) = await PrepareMedia(fixture);
        Assert.Equal("Shared tune", review.SidecarTitle);
        await WriteMediaSidecar(fixture, "Radio Edit");
        await ProblemAsync(await AcceptMedia(fixture, review, tag), 412, "enrichment_state_changed");
        await WriteMediaSidecar(fixture);
        clock.Now = clock.Now.AddHours(24);
        await ProblemAsync(await AcceptMedia(fixture, review, tag), 410, "enrichment_review_expired");
        Assert.Equal(0, await ImportCount("media_file"));
    }
    [Fact]
    public async Task Media_DuplicateCopiesCanShareRecordingButIdenticalBytesCannotConflict()
    {
        await using var fixture = await StartMedia();
        var (first, firstTag) = await PrepareMedia(fixture);
        Assert.Equal(HttpStatusCode.OK, (await AcceptMedia(fixture, first, firstTag)).StatusCode);
        var copy = Path.Combine(fixture.Root, "Library", "second.aiff");
        File.Copy(fixture.File, copy);
        await ScannerSql(fixture, $"""
            INSERT INTO media_file SELECT 2,'{copy}',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc FROM media_file WHERE media_file_id=1;
            INSERT INTO metadata SELECT 2,title,subtitle,artist,album,isrc FROM metadata WHERE media_file_id=1;
            """);
        var secondResponse = await fixture.Client.PostAsJsonAsync("/api/v1/media/2/link-reviews", new { recordingId = "t-shared" });
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = (await secondResponse.Content.ReadFromJsonAsync<MediaLinkReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await AcceptMedia(fixture, second, secondResponse.Headers.ETag!.ToString())).StatusCode);
        Assert.Equal(2, (await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!.Length);
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/1/link-reviews", new { recordingId = "t-shared" }),
            409, "media_already_linked");
        await ImportSql("INSERT INTO recording(recording_id,title,mix_name,isrc,duration_ms) VALUES('different-id','Shared tune','Original Mix','GBTRK0000099',400000)");
        var thirdPath = Path.Combine(fixture.Root, "Library", "third.aiff");
        File.Copy(fixture.File, thirdPath);
        await ScannerSql(fixture, $"""
            INSERT INTO media_file SELECT 3,'{thirdPath}',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc FROM media_file WHERE media_file_id=1;
            INSERT INTO metadata SELECT 3,title,subtitle,artist,album,'GBTRK0000099' FROM metadata WHERE media_file_id=1;
            """);
        var thirdResponse = await fixture.Client.PostAsJsonAsync("/api/v1/media/3/link-reviews", new { recordingId = "different-id" });
        Assert.Equal(HttpStatusCode.Created, thirdResponse.StatusCode);
        var third = (await thirdResponse.Content.ReadFromJsonAsync<MediaLinkReview>())!;
        await ProblemAsync(await AcceptMedia(fixture, third, thirdResponse.Headers.ETag!.ToString()), 409, "media_identity_conflict");
        Assert.Equal(2, await ImportCount("media_file"));
    }
    [Fact]
    public async Task Media_AuditWriteFailureRollsBackMediaAndAssociation()
    {
        await using var fixture = await StartMedia();
        var (review, tag) = await PrepareMedia(fixture);
        await ImportSql("CREATE TRIGGER fail_media_audit BEFORE INSERT ON enrichment_apply_revision BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END;");
        Assert.Equal(HttpStatusCode.InternalServerError, (await AcceptMedia(fixture, review, tag)).StatusCode);
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }
    [Fact]
    public async Task Media_MissingVersionTagRemainsManualAndBadScannerSchemaIsExplicit()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET title='Shared tune'");
        var candidates = await fixture.Client.GetFromJsonAsync<MediaCandidates>("/api/v1/media/1/candidates");
        Assert.Equal("Needs manual version review", candidates!.Candidates[0].Status);
        Assert.Empty(candidates.Candidates[0].Blockers);
        await ScannerSql(fixture, "DROP TABLE metadata");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media"), 409, "media_inventory_unavailable");
    }
    [Fact]
    public async Task Media_WithoutIsrcStillRequiresFullTitleMixArtistAndDurationCorroboration()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET isrc=NULL");
        var candidates = await fixture.Client.GetFromJsonAsync<MediaCandidates>("/api/v1/media/1/candidates");
        var supported = Assert.Single(candidates!.Candidates, candidate => candidate.RecordingId == "t-shared");
        Assert.Equal("Supported", supported.Status);
        Assert.Empty(supported.Blockers);
        await ScannerSql(fixture, "UPDATE metadata SET title='Shared tune'");
        candidates = await fixture.Client.GetFromJsonAsync<MediaCandidates>("/api/v1/media/1/candidates");
        var blocked = Assert.Single(candidates!.Candidates, candidate => candidate.RecordingId == "t-shared");
        Assert.Equal("Blocked", blocked.Status);
        Assert.NotEmpty(blocked.Blockers);
    }
}
