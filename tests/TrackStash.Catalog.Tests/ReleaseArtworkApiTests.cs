using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TrackStash.Catalog.Api;
using TrackStash.Catalog.Application;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task<string> ArtworkRelease()
    {
        var id = Guid.NewGuid().ToString("N");
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.CatalogFoundations!.SaveEntityAsync(new Release { Id = id, Title = "Artwork release" }, null);
        await unit.CommitAsync(); return id;
    }
    private async Task<HttpResponseMessage> ReleaseUpload(string id, string? etag, string role = "cover", string rights = "Test permission", byte[]? bytes = null, string? replacement = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes ?? ImageBytes()), "file", "cover.png");
        form.Add(new StringContent(role), "role"); form.Add(new StringContent(rights), "usageRights");
        form.Add(new StringContent("true"), "isPreferred");
        if (replacement is not null) form.Add(new StringContent(replacement), "replaceEntryId");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{id}/artwork") { Content = form };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }
    [Fact]
    public async Task ReleaseArtwork_ManagedUploadReplacementDetachVersionHistoryAndBackup()
    {
        var id = await ArtworkRelease();
        var initial = await _client.GetAsync($"/api/v1/releases/{id}/artwork");
        var upload = await ReleaseUpload(id, initial.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.NotEqual(initial.Headers.ETag.ToString(), upload.Headers.ETag!.ToString());
        var first = Assert.Single((await upload.Content.ReadFromJsonAsync<ReleaseArtworkResponse>())!.Items);
        Assert.Equal("cover", first.Role); Assert.True(first.IsPreferred);
        var list = (await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases"))!;
        Assert.Equal(first.AssetId, Assert.Single(list.Items).CoverAssetId);
        Assert.Empty(list.Items[0].PrimaryArtists);
        Assert.Equal(ImageBytes(), await _client.GetByteArrayAsync(upload.Headers.Location!));
        await ProblemAsync(await ReleaseUpload(id, initial.Headers.ETag.ToString()), 412, "stale_version");
        Assert.Equal(1, await ImportCount("artwork_asset"));
        var replacement = await ReleaseUpload(id, upload.Headers.ETag.ToString(), replacement: first.Id);
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        var second = Assert.Single((await replacement.Content.ReadFromJsonAsync<ReleaseArtworkResponse>())!.Items);
        Assert.Equal(first.Id, second.Id); Assert.NotEqual(first.AssetId, second.AssetId);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/releases/{id}/artwork") { Content = JsonContent.Create(new { items = Array.Empty<object>() }) };
        request.Headers.TryAddWithoutValidation("If-Match", replacement.Headers.ETag!.ToString());
        var detached = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
        Assert.Empty((await detached.Content.ReadFromJsonAsync<ReleaseArtworkResponse>())!.Items);
        Assert.Null(Assert.Single((await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases"))!.Items).CoverAssetId);
        Assert.Equal(2, await ImportCount("artwork_asset")); Assert.Equal(3, await ImportCount("release_artwork_revision"));
        Assert.Equal(ImageBytes(), await _client.GetByteArrayAsync(first.OriginalUrl));
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var analysis = await unit.EntityDelete!.AnalyzeDependenciesAsync("release", id, unit);
            Assert.Contains(analysis.Blockers, b => b.TableName == "release_artwork_revision");
        }
        await Assert.ThrowsAsync<SqliteException>(() => ImportSql("DELETE FROM release_artwork_revision"));
        var backup = new CatalogBackupService(new SqliteCatalogSnapshotSource(_path), new ManagedArtworkStore(_path + ".artwork"), _path + ".backups");
        var saved = await backup.CreateAsync(); var archive = Path.Combine(_path + ".backups", saved.Backup.Filename);
        Assert.Equal(11, (await backup.VerifyAsync(archive)).SchemaVersion);
        var root = _path + ".release-restored";
        try
        {
            await backup.RestoreAsync(archive, root);
            await using var unit = await new SqliteStorageProvider(Path.Combine(root, "catalog.db")).BeginUnitOfWorkAsync();
            Assert.Empty((await unit.Artwork.GetReleaseGalleryAsync(id))!.Items);
            Assert.NotNull(await unit.Artwork.GetAssetAsync(first.AssetId));
            Assert.True(File.Exists(Path.Combine(root, "artwork", first.AssetId, "original")));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    [Theory]
    [InlineData("logo", "Rights")]
    [InlineData("cover", "")]
    public async Task ReleaseArtwork_RejectsInvalidRoleOrMissingRightsWithoutAssets(string role, string rights)
    {
        var id = await ArtworkRelease(); var response = await _client.GetAsync($"/api/v1/releases/{id}/artwork");
        await ProblemAsync(await ReleaseUpload(id, response.Headers.ETag!.ToString(), role, rights), 400, "validation_failed");
        Assert.Equal(0, await ImportCount("artwork_asset"));
    }
    [Fact]
    public async Task ReleaseArtwork_FailedRevisionRollsBackAndDeletesUncommittedFiles()
    {
        var id = await ArtworkRelease(); var response = await _client.GetAsync($"/api/v1/releases/{id}/artwork");
        await ImportSql("CREATE TRIGGER fail_release_artwork BEFORE INSERT ON release_artwork_revision BEGIN SELECT RAISE(ABORT,'injected'); END");
        var failed = await ReleaseUpload(id, response.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await ImportCount("artwork_asset")); Assert.Equal(0, await ImportCount("release_artwork_link"));
        Assert.Equal(response.Headers.ETag.ToString(), (await _client.GetAsync($"/api/v1/releases/{id}/artwork")).Headers.ETag!.ToString());
        Assert.Empty(Directory.GetFiles(_path + ".artwork", "original", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task ReleaseArtwork_SchemaTenBrowsingRemainsAvailableButArtworkIsExplicitlyGated()
    {
        var id = await ArtworkRelease(); await ImportSql("DELETE FROM schema_migration WHERE version=11");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/releases/{id}")).StatusCode);
        Assert.Null(Assert.Single((await _client.GetFromJsonAsync<ReleaseListResponse>("/api/v1/releases"))!.Items).CoverAssetId);
        await ProblemAsync(await _client.GetAsync($"/api/v1/releases/{id}/artwork"), 409, "release_artwork_unavailable");
        await ProblemAsync(await _client.GetAsync($"/api/v1/releases/{id}/artwork-candidates"), 409, "release_artwork_unavailable");
        await ProblemAsync(await ReleaseUpload(id, null), 428, "precondition_required");
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync($"/api/v1/releases/{id}/artwork"), 401, "unauthorized");
    }

    private sealed class FakeReleaseArtworkProvider : IReleaseArtworkProvider
    {
        public string Name => "test-provider";
        public string Revision { get; set; } = "revision-1";
        public int Reads { get; private set; }
        public bool InvalidImage { get; set; }
        public bool DownloadFailed { get; set; }
        public ValueTask<IReadOnlyList<ReleaseArtworkCandidate>> FindAsync(ReleaseArtworkSourceQuery query, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ReleaseArtworkCandidate>>([new(Name, query.ReleaseId + "-cover", Revision, "Source cover", "https://example.test/cover", "Test creator")]);
        public ValueTask<ReleaseArtworkCandidateContent> ReadAsync(ReleaseArtworkSourceQuery query, ReleaseArtworkCandidate candidate, CancellationToken ct = default)
        {
            if (DownloadFailed) throw new HttpRequestException("Internal provider failure");
            Reads++; return ValueTask.FromResult(new ReleaseArtworkCandidateContent(InvalidImage ? [1, 2, 3] : ImageBytes(), "provider-cover.png"));
        }
    }
    [Fact]
    public async Task ReleaseArtwork_ProviderCandidatesRequireExplicitRightsScopedIdentityRevisionAndOriginalToken()
    {
        var id = await ArtworkRelease();
        var empty = (await _client.GetFromJsonAsync<ReleaseArtworkCandidatesResponse>($"/api/v1/releases/{id}/artwork-candidates"))!;
        Assert.Empty(empty.Providers); Assert.Empty(empty.Items);
        await ProblemAsync(await _client.GetAsync("/api/v1/releases/missing/artwork-candidates"), 404, "release_not_found");
        var provider = new FakeReleaseArtworkProvider();
        await using var app = Build(b => b.Services.AddSingleton<IReleaseArtworkProvider>(provider));
        await app.StartAsync(); using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        var candidates = (await client.GetFromJsonAsync<ReleaseArtworkCandidatesResponse>($"/api/v1/releases/{id}/artwork-candidates"))!;
        Assert.Single(candidates.Items); Assert.Equal(0, provider.Reads);
        var token = (await client.GetAsync($"/api/v1/releases/{id}/artwork")).Headers.ETag!.ToString();
        async Task<HttpResponseMessage> Attach(string candidateId, string revision, string rights = "Explicit permission", bool confirmed = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{id}/artwork-candidates/attach")
            {
                Content = JsonContent.Create(new { provider = provider.Name, candidateId, revision, usageRights = rights, confirmed }),
            };
            request.Headers.TryAddWithoutValidation("If-Match", token); return await client.SendAsync(request);
        }
        await ProblemAsync(await Attach(id + "-cover", "revision-1", confirmed: false), 400, "validation_failed");
        await ProblemAsync(await Attach(id + "-cover", "revision-1", rights: ""), 400, "validation_failed");
        using (var arbitraryUrl = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{id}/artwork-candidates/attach"))
        {
            arbitraryUrl.Headers.TryAddWithoutValidation("If-Match", token);
            arbitraryUrl.Content = JsonContent.Create(new { provider = provider.Name, candidateId = id + "-cover", revision = "revision-1", usageRights = "Rights", confirmed = true, sourceUrl = "https://example.test/arbitrary" });
            await ProblemAsync(await client.SendAsync(arbitraryUrl), 400, "validation_failed");
        }
        await ProblemAsync(await Attach("another-release-cover", "revision-1"), 409, "release_artwork_unavailable");
        provider.Revision = "revision-2";
        await ProblemAsync(await Attach(id + "-cover", "revision-1"), 409, "release_artwork_unavailable");
        Assert.Equal(0, provider.Reads);
        provider.DownloadFailed = true;
        await ProblemAsync(await Attach(id + "-cover", "revision-2"), 409, "release_artwork_unavailable");
        Assert.Equal(0, await ImportCount("artwork_asset"));
        provider.DownloadFailed = false;
        provider.InvalidImage = true;
        await ProblemAsync(await Attach(id + "-cover", "revision-2"), 400, "validation_failed");
        Assert.Equal(0, await ImportCount("artwork_asset"));
        provider.InvalidImage = false;
        var attached = await Attach(id + "-cover", "revision-2"); Assert.Equal(HttpStatusCode.Created, attached.StatusCode);
        var asset = Assert.Single((await attached.Content.ReadFromJsonAsync<ReleaseArtworkResponse>())!.Items);
        Assert.Equal("https://example.test/cover", asset.SourceUrl); Assert.Equal("Test creator", asset.Attribution);
        Assert.Equal("Explicit permission", asset.UsageRights);
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_json FROM release_artwork_revision";
            var source = JsonSerializer.Deserialize<ArtworkSourceIdentity>((string)(await command.ExecuteScalarAsync())!)!;
            Assert.Equal(new ArtworkSourceIdentity(provider.Name, id + "-cover", "revision-2"), source);
        }
        var reads = provider.Reads;
        await ProblemAsync(await Attach(id + "-cover", "revision-2"), 412, "stale_version"); Assert.Equal(reads, provider.Reads);
    }
}
