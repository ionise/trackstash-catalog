using TrackStash.Core.Normalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using TrackStash.Catalog.Api;
using TrackStash.Catalog.Application;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task ImportSql(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<long> ImportCount(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private async Task<ReleaseImportService> ImportService()
    {
        await CreatePurchaseSource();
        await ImportSql("""
            INSERT INTO label(label_id,name) VALUES('source-label','Test label');
            INSERT INTO label_external_ref(label_id,source,external_id) VALUES('source-label','beatport','42');
            """);
        return new(Provider, new SqlitePurchaseReleaseSource(PurchaseSourcePath), TimeProvider.System);
    }
    private static async Task<StoredReleaseImport> Reviewed(ReleaseImportService service)
    {
        var p = await service.PreviewAsync(10, null, null);
        Assert.Contains(p.Plan.Blockers, b => b.Contains("explicitly accept"));
        foreach (var id in new long[] { 1, 2, 3 })
            p = await service.PreviewAsync(10, p.Plan.OperationId, new("artist", id, id == 3 ? "unresolved" : "create", Evidence: "Reviewed structured provider identity"));
        foreach (var id in new long[] { 101, 102 })
            p = await service.PreviewAsync(10, p.Plan.OperationId, new("recording", id, "create", Evidence: "Accepted distinct versioned recording"));
        Assert.Empty(p.Plan.Blockers);
        return p;
    }
    [Fact]
    public async Task ReleaseImport_FullGraphImmutablePreviewReplayAuditDocumentsNoOpAndManualOrder()
    {
        var service = await ImportService();
        var p = await Reviewed(service);
        Assert.Equal(0, await ImportCount("artist")); Assert.Equal(0, await ImportCount("release")); Assert.Equal(0, await ImportCount("recording"));
        Assert.Equal(6, await ImportCount("release_import_preview"));
        Assert.Equal(JsonSerializer.Serialize(p.Plan), JsonSerializer.Serialize((await service.GetAsync(p.Plan.OperationId)).Plan));
        var outcome = await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Reviewed release", true);
        Assert.Equal(3, outcome.CreatedArtists); Assert.Equal(2, outcome.CreatedRecordings); Assert.Equal(2, outcome.CreatedPlacements);
        Assert.Equal(outcome, await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Reviewed release", true));
        var replayError = await Assert.ThrowsAsync<ReleaseImportException>(() => service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Changed intent", true).AsTask());
        Assert.Equal(409, replayError.Status);
        Assert.Equal(1, await ImportCount("release_import_outcome")); Assert.Equal(1, await ImportCount("release_import_binding"));
        Assert.Equal(6, await ImportCount("catalog_embedding_source")); Assert.Equal(6, await ImportCount("catalog_embedding_outbox"));
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var release = (await unit.Releases.GetByIdAsync(outcome.ReleaseId))!;
            Assert.Equal("CAT001", release.CatalogNumber); Assert.Equal(3, release.ArtistCredits.Count);
            var compound = p.Plan.Artists.Single(a => a.After.Name == "GMJ & Matter").After.Id;
            Assert.Equal("unresolved_compound", (await unit.Artists.GetByIdAsync(compound))!.IdentityStatus);
            var links = await unit.CatalogFoundations!.GetPlacementsAsync(outcome.ReleaseId);
            Assert.Equal(2, links.Count); Assert.All(links, link => Assert.Null(link.TrackNumber));
            Assert.Equal(3, links[1].Credits.Count); Assert.Contains(links[1].Credits, c => c.Role == "remixer");
            Assert.Empty((await unit.Recordings.GetByIdAsync(links[0].RecordingId))!.ExternalReferences);
            await unit.CatalogFoundations.ReorderPlacementsAsync(outcome.ReleaseId, release.Version, links.Reverse().Select(l => l.Id).ToArray());
            await unit.CommitAsync();
        }
        var noOp = await service.PreviewAsync(10, null, null);
        Assert.True(noOp.Plan.NoOp); Assert.Empty(noOp.Plan.Blockers);
        Assert.True((await service.ApplyAsync(noOp.Plan.OperationId, noOp.Plan.Version, "No-op reviewed", true)).NoOp);
        Assert.Equal(6, await ImportCount("catalog_embedding_outbox"));
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
            Assert.All(await unit.CatalogFoundations!.GetPlacementsAsync(outcome.ReleaseId), p => Assert.Equal("manual", p.OrderSource));
        await Assert.ThrowsAsync<SqliteException>(() => ImportSql("UPDATE release_import_preview SET version='changed'"));
        await Assert.ThrowsAsync<SqliteException>(() => ImportSql("DELETE FROM release_import_outcome"));
    }
    [Theory]
    [InlineData("artist")]
    [InlineData("release_artist_credit")]
    [InlineData("recording")]
    [InlineData("placement_external_ref")]
    [InlineData("catalog_foundation_revision")]
    [InlineData("release_import_binding")]
    [InlineData("catalog_embedding_source")]
    [InlineData("catalog_embedding_outbox")]
    [InlineData("release_import_outcome")]
    public async Task ReleaseImport_FailureAtEveryStageRollsBackEvenIfCallerCommits(string stage)
    {
        var service = await ImportService(); var p = await Reviewed(service);
        await ImportSql($"CREATE TRIGGER injected_failure BEFORE INSERT ON {stage} BEGIN SELECT RAISE(ABORT,'injected failure'); END");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await Assert.ThrowsAsync<SqliteException>(() => unit.ReleaseImports!.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", DateTimeOffset.UtcNow).AsTask());
            await unit.CommitAsync();
        }
        foreach (var table in new[] { "artist", "release", "recording", "release_recording", "release_import_outcome", "release_import_binding", "catalog_foundation_revision",
            "catalog_embedding_source", "catalog_embedding_outbox" }) Assert.Equal(0, await ImportCount(table));
        await ImportSql("DROP TRIGGER injected_failure");
        Assert.False((await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", true)).NoOp);
    }
    [Theory]
    [InlineData("UPDATE label SET name='Edited' WHERE label_id='source-label'")]
    [InlineData("INSERT INTO artist(artist_id,name,normalized_name) VALUES('candidate','Kasey Taylor','kasey taylor')")]
    [InlineData("INSERT INTO artist(artist_id,name) VALUES('candidate','Different'); INSERT INTO artist_external_ref(artist_id,source,external_id) VALUES('candidate','beatport','1')")]
    public async Task ReleaseImport_CatalogEditsAndNegativeBindingRacesInvalidateReview(string change)
    {
        var service = await ImportService(); var p = await Reviewed(service);
        await ImportSql(change);
        var failure = await Assert.ThrowsAsync<ReleaseImportException>(() => service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", true).AsTask());
        Assert.Equal(412, failure.Status); Assert.Equal(0, await ImportCount("release_import_outcome")); Assert.Equal(0, await ImportCount("release"));
    }
    [Fact]
    public async Task ReleaseImport_SourceChangedAndIncompleteSourcesNeverWriteGraph()
    {
        var service = await ImportService(); var p = await Reviewed(service);
        await SourceSql("UPDATE beatport_release_cache SET fetched_utc='2026-11-06T00:00:00Z'");
        var failure = await Assert.ThrowsAsync<ReleaseImportException>(() => service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", true).AsTask());
        Assert.Equal("import_source_changed", failure.Code);
        await SourceSql("UPDATE beatport_release_cache SET release_json='null'");
        var incomplete = await service.PreviewAsync(10, null, null);
        Assert.NotEmpty(incomplete.Plan.Blockers);
        var blocked = await Assert.ThrowsAsync<ReleaseImportException>(() => service.ApplyAsync(incomplete.Plan.OperationId, incomplete.Plan.Version, "Review", true).AsTask());
        Assert.Equal(409, blocked.Status); Assert.Equal(0, await ImportCount("release"));
    }
    [Fact]
    public async Task ReleaseImport_ExpiredReviewsRequireNewPlanAndReplayIgnoresLaterSourceChanges()
    {
        var service = await ImportService(); var p = await Reviewed(service);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var expired = await Assert.ThrowsAsync<ReleaseImportException>(() => unit.ReleaseImports!.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review",
                p.Plan.ExpiresUtc).AsTask());
            Assert.Equal(410, expired.Status); await unit.CommitAsync();
        }
        var outcome = await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", true);
        await SourceSql("UPDATE beatport_release_cache SET release_json='null'");
        Assert.Equal(outcome, await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", true));
    }
    [Fact]
    public async Task ReleaseImport_ChangedDecisionsSupersedeOldPlansAndRefreshedIdenticalMetadataIsNoOp()
    {
        var service = await ImportService();
        var first = await service.PreviewAsync(10, null, null);
        var next = await service.PreviewAsync(10, first.Plan.OperationId, new("artist", 1, "create", Evidence: "Accepted act"));
        Assert.Equal(next.Plan.OperationId, (await service.GetAsync(first.Plan.OperationId)).SupersededBy);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var old = await Assert.ThrowsAsync<ReleaseImportException>(() => unit.ReleaseImports!.ApplyAsync(first.Plan.OperationId, first.Plan.Version,
                "Old review", DateTimeOffset.UtcNow).AsTask());
            Assert.Equal("import_superseded", old.Code); await unit.CommitAsync();
        }
        var p = await Reviewed(service);
        await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", true);
        await SourceSql("UPDATE beatport_release_cache SET fetched_utc='2026-11-06T00:00:00Z'");
        var refreshed = await service.PreviewAsync(10, null, null);
        Assert.True(refreshed.Plan.NoOp); Assert.Empty(refreshed.Plan.Blockers);
        var before = await ImportCount("catalog_foundation_revision");
        await service.ApplyAsync(refreshed.Plan.OperationId, refreshed.Plan.Version, "Refreshed but unchanged", true);
        Assert.Equal(before, await ImportCount("catalog_foundation_revision")); Assert.Equal(6, await ImportCount("catalog_embedding_outbox"));
    }
    [Fact]
    public async Task ReleaseImport_EvidencedReuseCombinesArtistBindingsAndPreservesCuratedRecordingAndRepeatedRoles()
    {
        var service = await ImportService();
        var artistId = Guid.NewGuid().ToString("N"); var recordingId = Guid.NewGuid().ToString("N");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.CatalogFoundations!.SaveEntityAsync(new Artist { Id = artistId, Name = "Curated performing act",
                Kind = "person", IdentityStatus = "accepted", Aliases = [new() { Value = "Alias" }],
                ExternalReferences = [new() { Source = "beatport", ExternalId = "1" }] }, null);
            await unit.CatalogFoundations.SaveEntityAsync(new Recording { Id = recordingId, Title = "Curated recording", MixName = "Original Mix",
                Isrc = "TEST001", DurationMs = 999, ArtistCredits = [new() { ArtistId = artistId, Role = "composer" }] }, null);
            await unit.CommitAsync();
        }
        var p = await service.PreviewAsync(10, null, null);
        p = await service.PreviewAsync(10, p.Plan.OperationId, new("artist", 2, "reuse", artistId, "Evidence of second provider identity for same act"));
        p = await service.PreviewAsync(10, p.Plan.OperationId, new("artist", 3, "unresolved", Evidence: "Compound act to resolve later"));
        p = await service.PreviewAsync(10, p.Plan.OperationId, new("recording", 101, "reuse", recordingId, "Reviewed exact recording version"));
        p = await service.PreviewAsync(10, p.Plan.OperationId, new("recording", 102, "create", Evidence: "Distinct remix"));
        Assert.Empty(p.Plan.Blockers);
        var outcome = await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Reuse reviewed", true);
        Assert.Equal(1, outcome.CreatedArtists); Assert.Equal(1, outcome.CreatedRecordings);
        await using var check = await Provider.BeginUnitOfWorkAsync();
        var artist = (await check.Artists.GetByIdAsync(artistId))!;
        Assert.Equal("Curated performing act", artist.Name); Assert.Equal("person", artist.Kind);
        Assert.Single(artist.Aliases); Assert.Equal(2, artist.ExternalReferences.Count);
        var recording = (await check.Recordings.GetByIdAsync(recordingId))!;
        Assert.Equal("Curated recording", recording.Title); Assert.Equal(999, recording.DurationMs); Assert.Equal("composer", Assert.Single(recording.ArtistCredits).Role);
        var link = (await check.CatalogFoundations!.GetPlacementsAsync(outcome.ReleaseId)).Single(l => l.RecordingId == recordingId);
        Assert.Equal(2, link.Credits.Count); Assert.All(link.Credits, c => Assert.Equal(artistId, c.ArtistId));
        Assert.Equal(new string?[] { "Kasey Taylor", "Amega" }.AsEnumerable(), link.Credits.Select(c => c.CreditedName));
    }
    [Fact]
    public async Task ReleaseImport_DifferentBeatportIdsAndContradictoryVersionsAreNotReviewCandidates()
    {
        var service = await ImportService();
        static string N(string name) => EntityNameNormalizer.NormalizeStrict(name);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var f = unit.CatalogFoundations!;
            await f.SaveEntityAsync(new Release { Id = "00000001-0000-4000-8000-000000000000", Title = "Test release", Name = "Test release", NormalizedName = N("test release"), ExternalReferences = [new() { Source = "beatport", ExternalId = "999" }] }, null);
            await f.SaveEntityAsync(new Artist { Id = "00000002-0000-4000-8000-000000000000", Name = "Kasey Taylor", NormalizedName = N("kasey taylor"), ExternalReferences = [new() { Source = "beatport", ExternalId = "77" }] }, null);
            await f.SaveEntityAsync(new Artist { Id = "00000003-0000-4000-8000-000000000000", Name = "Amega", NormalizedName = N("amega") }, null);
            await f.SaveEntityAsync(new Recording { Id = "00000004-0000-4000-8000-000000000000", Title = "Track", Name = "Track", NormalizedName = N("track"), MixName = "Original Mix", Isrc = "OTHER",
                ExternalReferences = [new() { Source = "beatport", ExternalId = "555" }] }, null);
            await f.SaveEntityAsync(new Recording { Id = "00000005-0000-4000-8000-000000000000", Title = "Track", Name = "Track", NormalizedName = N("track"), MixName = "Remix", Isrc = "TEST001",
                ExternalReferences = [new() { Source = "beatport", ExternalId = "556" }] }, null);
            await f.SaveEntityAsync(new Recording { Id = "00000006-0000-4000-8000-000000000000", Title = "Track", Name = "Track", NormalizedName = N("track"), MixName = "Extended Mix" }, null);
            await unit.CommitAsync();
        }
        var p = await service.PreviewAsync(10, null, null);
        IReadOnlyList<string> Candidates(string kind, long id) => p.Plan.Identities.Single(i => i.Kind == kind && i.ProviderId == id).Candidates.Select(c => c.Id).ToList();
        Assert.Empty(Candidates("release", 10));
        Assert.DoesNotContain(p.Plan.Blockers, b => b.Contains("same-title Release"));
        Assert.Empty(Candidates("artist", 1));
        Assert.Equal(["00000003-0000-4000-8000-000000000000"], Candidates("artist", 2));
        Assert.Empty(Candidates("recording", 101));
        Assert.Equal(3, await ImportCount("recording WHERE normalized_name='" + N("Track") + "'"));
        Assert.Equal(["00000005-0000-4000-8000-000000000000"], Candidates("recording", 102));
        p = await service.PreviewAsync(10, p.Plan.OperationId, new("artist", 1, "create"));
        Assert.Equal(ReleaseImportService.DefaultEvidence, p.Plan.Decisions.Single(d => d.ProviderId == 1).Evidence);
        Assert.Equal(1, await ImportCount("release WHERE normalized_name='" + N("Test release") + "'"));
        Assert.Equal(1, await ImportCount("release_external_ref WHERE external_id='999'"));
        await ImportSql("DELETE FROM release_external_ref WHERE release_id='00000001-0000-4000-8000-000000000000'");
        Assert.Equal(0, await ImportCount("release_external_ref WHERE external_id='999'"));
        var fresh = await service.PreviewAsync(10, null, null);
        Assert.NotEqual(p.Plan.OperationId, fresh.Plan.OperationId);
        Assert.Contains(fresh.Plan.Blockers, b => b.Contains("same-title Release"));
    }
    [Fact]
    public async Task ReleaseImport_BackupPreservesImmutablePlansOutcomesPendingDocumentsAndReplay()
    {
        var service = await ImportService(); var p = await Reviewed(service);
        var outcome = await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Backup review", true);
        var backups = new CatalogBackupService(new SqliteCatalogSnapshotSource(_path), new ManagedArtworkStore(_path + ".artwork"), _path + ".backups");
        var archive = await backups.CreateAsync();
        var root = _path + ".restored";
        try
        {
            await backups.RestoreAsync(Path.Combine(_path + ".backups", archive.Backup.Filename), root);
            var restored = new SqliteStorageProvider(Path.Combine(root, "catalog.db"));
            await using var unit = await restored.BeginUnitOfWorkAsync();
            Assert.Equal(outcome, (await unit.ReleaseImports!.GetAsync(p.Plan.OperationId))!.Outcome);
            Assert.Equal(outcome, await unit.ReleaseImports.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Backup review", p.Plan.ExpiresUtc.AddDays(1)));
            Assert.Equal(2, (await unit.CatalogFoundations!.GetPlacementsAsync(outcome.ReleaseId)).Count);
            await unit.CommitAsync();
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task ReleaseImport_ArtistAndLabelEditsInvalidateOnlyDependentDocumentsAndManualOrderDoesNotEnqueue()
    {
        var service = await ImportService(); var p = await Reviewed(service);
        var outcome = await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Review", true);
        await ImportSql("DELETE FROM catalog_embedding_outbox; UPDATE catalog_embedding_source SET status='current',generated_hash=desired_hash");
        var artist = p.Plan.Artists.Single(a => a.After.Name == "GMJ & Matter").After.Id;
        await ImportSql($"UPDATE artist SET name='Reviewed compound name' WHERE artist_id='{artist}'");
        Assert.Equal(3, await ImportCount("catalog_embedding_outbox"));
        await ImportSql("DELETE FROM catalog_embedding_outbox; UPDATE catalog_embedding_source SET status='current',generated_hash=desired_hash");
        await ImportSql("UPDATE label SET name='Edited issuing label' WHERE label_id='source-label'");
        Assert.Equal(1, await ImportCount("catalog_embedding_outbox"));
        await ImportSql("DELETE FROM catalog_embedding_outbox; UPDATE catalog_embedding_source SET status='current',generated_hash=desired_hash");
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        var release = (await unit.Releases.GetByIdAsync(outcome.ReleaseId))!;
        var placements = await unit.CatalogFoundations!.GetPlacementsAsync(outcome.ReleaseId);
        await unit.CatalogFoundations.ReorderPlacementsAsync(outcome.ReleaseId, release.Version, placements.Reverse().Select(l => l.Id).ToArray());
        await unit.CommitAsync();
        Assert.Equal(0, await ImportCount("catalog_embedding_outbox"));
    }
    [Fact]
    public async Task ReleaseImport_RepeatedPlacementsCanExplicitlyReuseOneRecordingWithoutDuplicateCreditsOrReferences()
    {
        var service = await ImportService();
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json=json_set(tracks_json,'$[1].mix_name','Original Mix')");
        var recordingId = Guid.NewGuid().ToString("N");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.CatalogFoundations!.SaveEntityAsync(new Recording { Id = recordingId, Title = "Shared master", MixName = "Original Mix", Isrc = "TEST001" }, null);
            await unit.CommitAsync();
        }
        var p = await service.PreviewAsync(10, null, null);
        foreach (var id in new long[] { 1, 2, 3 })
            p = await service.PreviewAsync(10, p.Plan.OperationId, new("artist", id, id == 3 ? "unresolved" : "create", Evidence: "Accepted provider identity"));
        foreach (var id in new long[] { 101, 102 })
            p = await service.PreviewAsync(10, p.Plan.OperationId, new("recording", id, "reuse", recordingId, "Evidence of the same master at repeated source placements"));
        Assert.Empty(p.Plan.Blockers);
        Assert.Contains(p.Plan.Warnings, warning => warning.Contains("not automatically merged"));
        var outcome = await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Repeated appearances reviewed", true);
        Assert.Equal(0, outcome.CreatedRecordings); Assert.Equal(2, outcome.CreatedPlacements);
        await using var check = await Provider.BeginUnitOfWorkAsync();
        var links = await check.CatalogFoundations!.GetPlacementsAsync(outcome.ReleaseId);
        Assert.All(links, link => Assert.Equal(recordingId, link.RecordingId));
        Assert.Equal(2, links.Select(l => l.Id).Distinct().Count()); Assert.Equal(2, links.SelectMany(l => l.ExternalReferences).Count());
        Assert.Empty((await check.Recordings.GetByIdAsync(recordingId))!.ArtistCredits);
    }
    [Fact]
    public async Task ReleaseImport_FullBundleExceedsOneSourcePageAndFrozenPagesHaveOneStableDigest()
    {
        var service = await ImportService();
        var ids = Enumerable.Range(101, 101).ToArray();
        await SourceSql("UPDATE beatport_release_cache SET release_json=@release", ("@release", ReleasePayload(ids.Select(id =>
            $"https://api.beatport.com/v4/catalog/tracks/{id}/").ToArray(), count: ids.Length)));
        await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json=@tracks", ("@tracks", "[" + string.Join(",", ids.Select(id => TrackPayload(id))) + "]"));
        var p = await service.PreviewAsync(10, null, null);
        Assert.Equal(101, p.Plan.Source.Tracks.Items.Count); Assert.Equal(101, p.Plan.Placements.Count);
        Assert.Equal(201, p.Plan.Source.Tracks.Items[^1].ProviderId);
        Assert.Equal(105, p.Plan.Identities.Count);
        Assert.Equal(p.Plan.Source.BundleDigest, (await service.GetAsync(p.Plan.OperationId)).Plan.Source.BundleDigest);
        Assert.Equal(0, await ImportCount("release"));
    }
    [Fact]
    public async Task ReleaseImport_SchemaEightStillAllowsReadOnlyInspectorButImportRequiresTen()
    {
        await ImportService();
        await ImportSql("DELETE FROM schema_migration WHERE version>=9");
        await using var app = Build(b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:PurchaseSourceDatabasePath"] = PurchaseSourcePath }));
        await app.StartAsync(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/purchase-release-candidates/10")).StatusCode);
        var unknown = (await client.GetFromJsonAsync<PurchaseReleaseCandidatePage>("/api/v1/purchase-release-candidates"))!;
        Assert.False(unknown.AdmissionStatusAvailable); Assert.All(unknown.Items, item => Assert.Empty(item.AdmittedReleaseIds));
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates?hideAdmitted=true"), 409, "admission_status_unavailable");
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/release-import-previews", new { releaseId = 10 }), 409, "import_schema_required");
        Assert.Equal(0, await ImportCount("release_import_preview")); Assert.Equal(8, await Provider.Migrations.GetCurrentVersionAsync());
    }
    [Fact]
    public async Task PurchaseCandidates_ReportAdmittedReleasesAndHideThemBeforePagingWithoutWrites()
    {
        var service = await ImportService(); var p = await Reviewed(service);
        var outcome = await service.ApplyAsync(p.Plan.OperationId, p.Plan.Version, "Reviewed release", true);
        await using var app = Build(b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:PurchaseSourceDatabasePath"] = PurchaseSourcePath }));
        await app.StartAsync(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        var catalogHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(_path));
        var all = (await client.GetFromJsonAsync<PurchaseReleaseCandidatePage>("/api/v1/purchase-release-candidates?hideAdmitted=false"))!;
        Assert.True(all.AdmissionStatusAvailable); Assert.False(all.HideAdmitted); Assert.Equal(2, all.Total);
        Assert.Equal(outcome.ReleaseId, Assert.Single(Assert.Single(all.Items, item => item.ReleaseId == 10).AdmittedReleaseIds));
        Assert.Empty(Assert.Single(all.Items, item => item.ReleaseId == 11).AdmittedReleaseIds);
        var hidden = (await client.GetFromJsonAsync<PurchaseReleaseCandidatePage>("/api/v1/purchase-release-candidates?hideAdmitted=true&pageSize=1"))!;
        Assert.True(hidden.HideAdmitted); Assert.Equal(1, hidden.Total);
        Assert.Equal(11, Assert.Single(hidden.Items).ReleaseId);
        var searched = (await client.GetFromJsonAsync<PurchaseReleaseCandidatePage>("/api/v1/purchase-release-candidates?hideAdmitted=true&q=10"))!;
        Assert.Equal(0, searched.Total); Assert.Empty(searched.Items);
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates?hideAdmitted=yes"), 400, "validation_failed");
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates?hideAdmitted=true&hideAdmitted=false"), 400, "validation_failed");
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates/10?hideAdmitted=true"), 400, "validation_failed");
        var ready = Assert.Single(all.Items, item => item.ReleaseId == 10).Readiness!;
        Assert.Equal("ready", ready.Readiness); Assert.Equal(1, ready.LabelCandidateCount); Assert.Empty(ready.BlockerCodes);
        Assert.Equal(ready.ManifestTrackCount, ready.HydratedTrackCount);
        var incomplete = Assert.Single(all.Items, item => item.ReleaseId == 11).Readiness!;
        Assert.Equal("incomplete_source", incomplete.Readiness); Assert.NotEmpty(incomplete.BlockerCodes);
        var readyOnly = (await client.GetFromJsonAsync<PurchaseReleaseCandidatePage>("/api/v1/purchase-release-candidates?readyOnly=true&pageSize=1"))!;
        Assert.True(readyOnly.ReadyOnly); Assert.Equal(1, readyOnly.Total); Assert.Equal(10, Assert.Single(readyOnly.Items).ReleaseId);
        var readyHidden = (await client.GetFromJsonAsync<PurchaseReleaseCandidatePage>("/api/v1/purchase-release-candidates?readyOnly=true&hideAdmitted=true"))!;
        Assert.Equal(0, readyHidden.Total); Assert.Empty(readyHidden.Items);
        Assert.Equal(0, (await client.GetFromJsonAsync<PurchaseReleaseCandidatePage>("/api/v1/purchase-release-candidates?readyOnly=true&q=11"))!.Total);
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates?readyOnly=1"), 400, "validation_failed");
        await ProblemAsync(await client.GetAsync("/api/v1/purchase-release-candidates/10?readyOnly=true"), 400, "validation_failed");
        Assert.Equal(catalogHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(_path)));
    }
    [Fact]
    public async Task ReleaseImport_ApiRequiresAuthOriginalEtagValidBodyConfirmationAndReturnsFrozenPages()
    {
        var service = await ImportService(); var p = await Reviewed(service);
        await using var app = Build(b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:PurchaseSourceDatabasePath"] = PurchaseSourcePath }));
        await app.StartAsync(); using var client = app.GetTestClient();
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/release-import-previews", new { releaseId = 10 }), 401, "unauthorized");
        client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/release-import-previews", new { releaseId = "10" }), 400, "validation_failed");
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/release-import-previews", new { releaseId = 10, unknown = true }), 400, "validation_failed");
        var get = await client.GetAsync($"/api/v1/release-import-previews/{p.Plan.OperationId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode); Assert.Equal($"\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Plan.Version))}\"", get.Headers.ETag!.ToString());
        Assert.True(get.Headers.CacheControl!.NoStore);
        var body = await get.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_", body); Assert.DoesNotContain("CatalogStamp", body);
        var identities = await client.GetAsync($"/api/v1/release-import-previews/{p.Plan.OperationId}/identities?page=2&pageSize=2");
        var page = await identities.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(6, page.GetProperty("total").GetInt32()); Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        var path = $"/api/v1/release-import-previews/{p.Plan.OperationId}/apply";
        await ProblemAsync(await client.PostAsJsonAsync(path, new { reason = "Review", confirmed = true }), 428, "precondition_required");
        async Task<HttpResponseMessage> Apply(string etag, bool confirmed) {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { reason = "Review", confirmed }) };
            request.Headers.TryAddWithoutValidation("If-Match", etag); return await client.SendAsync(request);
        }
        await ProblemAsync(await Apply(get.Headers.ETag.ToString(), false), 400, "confirmation_required");
        await ProblemAsync(await Apply($"\"{Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong"))}\"", true), 412, "import_state_changed");
        var result = await Apply(get.Headers.ETag.ToString(), true); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(await result.Content.ReadAsStringAsync(), await (await Apply(get.Headers.ETag.ToString(), true)).Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task ReleaseImport_ApiAcceptsDecisionsAndApplyWithoutTypedEvidenceOrReason()
    {
        var service = await ImportService();
        await using var app = Build(b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:PurchaseSourceDatabasePath"] = PurchaseSourcePath }));
        await app.StartAsync(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        var p = await service.PreviewAsync(10, null, null);
        foreach (var (kind, id, action) in new[] { ("artist", 1L, "create"), ("artist", 2L, "create"), ("artist", 3L, "unresolved"), ("recording", 101L, "create"), ("recording", 102L, "create") })
        {
            object decision = id == 1 ? new { kind, providerId = id, action, canonicalId = (string?)null, evidence = "  " } : new { kind, providerId = id, action };
            var created = await client.PostAsJsonAsync("/api/v1/release-import-previews", new { releaseId = 10, basePreviewId = p.Plan.OperationId, decision });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            p = await service.GetAsync((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("operationId").GetString()!);
        }
        Assert.Empty(p.Plan.Blockers);
        Assert.All(p.Plan.Decisions, d => Assert.Equal(ReleaseImportService.DefaultEvidence, d.Evidence));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/release-import-previews/{p.Plan.OperationId}/apply") { Content = JsonContent.Create(new { confirmed = true }) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Plan.Version))}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(1, await ImportCount("release"));
    }
}
