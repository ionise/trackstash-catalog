using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Api;
using TrackStash.Core.Storage;
using TrackStash.Core.Sqlite;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task<string> ProfileArtist()
    {
        var id = Guid.NewGuid().ToString("N");
        await ImportSql($"INSERT INTO artist(artist_id,name) VALUES('{id}','Profile Artist')");
        return id;
    }
    private async Task<HttpResponseMessage> SaveArtistProfile(string id, string? token, object profile)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/artists/{id}/profile") { Content = JsonContent.Create(profile) };
        if (token is not null) request.Headers.TryAddWithoutValidation("If-Match", token);
        return await _client.SendAsync(request);
    }
    private async Task<HttpResponseMessage> UploadArtistPortrait(string id, string token, string role = "portrait")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(ImageBytes()), "file", "portrait.png"); form.Add(new StringContent(role), "role");
        form.Add(new StringContent("Permission recorded"), "usageRights"); form.Add(new StringContent("true"), "isPreferred");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/artists/{id}/artwork") { Content = form };
        request.Headers.TryAddWithoutValidation("If-Match", token); return await _client.SendAsync(request);
    }
    [Fact]
    public async Task ArtistManualProfileAndPortraitInvalidateImportPreviewsButReimportPreservesBothAndCanonicalText()
    {
        var service = await ImportService(); var reviewed = await Reviewed(service);
        await service.ApplyAsync(reviewed.Plan.OperationId, reviewed.Plan.Version, "Reviewed", true);
        var id = reviewed.Plan.Artists.First().After.Id;
        var pending = await service.PreviewAsync(10, null, null);
        var initial = await _client.GetAsync($"/api/v1/artists/{id}/profile");
        var profile = (await initial.Content.ReadFromJsonAsync<ArtistProfileResponse>())!;
        var saved = await SaveArtistProfile(id, initial.Headers.ETag!.ToString(), profile with { Biography = "Manual biography", Notes = "PRIVATE_SENTINEL" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var failure = await Assert.ThrowsAsync<ReleaseImportException>(() => service.ApplyAsync(pending.Plan.OperationId, pending.Plan.Version, "Reviewed", true).AsTask());
        Assert.Equal(412, failure.Status);
        var afterProfile = await service.PreviewAsync(10, null, null);
        var portrait = await UploadArtistPortrait(id, saved.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Created, portrait.StatusCode);
        failure = await Assert.ThrowsAsync<ReleaseImportException>(() => service.ApplyAsync(afterProfile.Plan.OperationId, afterProfile.Plan.Version, "Reviewed", true).AsTask());
        Assert.Equal(412, failure.Status);
        var fresh = await service.PreviewAsync(10, null, null);
        Assert.True(fresh.Plan.NoOp);
        Assert.True((await service.ApplyAsync(fresh.Plan.OperationId, fresh.Plan.Version, "Preserve manual work", true)).NoOp);
        Assert.Equal(6, await ImportCount("catalog_embedding_outbox"));
        Assert.Equal(6, await ImportCount("catalog_embedding_source"));
        await using (var connection = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM catalog_embedding_source WHERE instr(document_text,'PRIVATE_SENTINEL')>0 OR instr(document_text,'Manual biography')>0";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        Assert.Equal("Manual biography", (await _client.GetFromJsonAsync<ArtistProfileResponse>($"/api/v1/artists/{id}/profile"))!.Biography);
        Assert.Single((await _client.GetFromJsonAsync<ArtistArtworkResponse>($"/api/v1/artists/{id}/artwork"))!.Items);
        Assert.Equal(1, await ImportCount("artist_profile_revision"));
    }
    [Fact]
    public async Task ArtistProfileAndPortraitAreConditionalBackedUpAndDoNotEnqueueTextForPrivateOrImageOnlyChanges()
    {
        var id = await ProfileArtist();
        var initial = await _client.GetAsync($"/api/v1/artists/{id}/profile");
        var profile = (await initial.Content.ReadFromJsonAsync<ArtistProfileResponse>())!;
        Assert.Null(profile.Biography);
        var saved = await SaveArtistProfile(id, initial.Headers.ETag!.ToString(), profile with {
            Biography = "Manually written biography", BiographySourceUrl = "https://example.test/bio", Notes = "PRIVATE_SENTINEL", Favourite = true,
            Links = [new("official", "https://example.test/artist", "Website", null)]
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); Assert.NotEqual(initial.Headers.ETag.ToString(), saved.Headers.ETag!.ToString());
        await ProblemAsync(await SaveArtistProfile(id, initial.Headers.ETag.ToString(), profile), 412, "stale_version");
        await ProblemAsync(await UploadArtistPortrait(id, saved.Headers.ETag.ToString(), "logo"), 400, "validation_failed");
        var portrait = await UploadArtistPortrait(id, saved.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.Created, portrait.StatusCode);
        var image = Assert.Single((await portrait.Content.ReadFromJsonAsync<ArtistArtworkResponse>())!.Items);
        Assert.Equal("portrait", image.Role); Assert.Equal(ImageBytes(), await _client.GetByteArrayAsync(image.OriginalUrl));
        await ProblemAsync(await SaveArtistProfile(id, saved.Headers.ETag.ToString(), profile), 412, "stale_version");
        Assert.Equal(0, await ImportCount("catalog_embedding_outbox"));
        Assert.DoesNotContain("PRIVATE_SENTINEL", await _client.GetStringAsync($"/api/v1/artists/{id}"));
        var backup = new CatalogBackupService(new SqliteCatalogSnapshotSource(_path), new ManagedArtworkStore(_path + ".artwork"), _path + ".backups");
        var archive = Path.Combine(_path + ".backups", (await backup.CreateAsync()).Backup.Filename);
        Assert.Equal(12, (await backup.VerifyAsync(archive)).SchemaVersion);
        var destination = _path + ".artist-restore";
        try
        {
            await backup.RestoreAsync(archive, destination);
            await using var unit = await new SqliteStorageProvider(Path.Combine(destination, "catalog.db")).BeginUnitOfWorkAsync();
            Assert.Equal("Manually written biography", (await unit.Artists.GetProfileAsync(id))!.Profile.Biography);
            Assert.Equal(image.AssetId, Assert.Single((await unit.Artwork.GetArtistGalleryAsync(id))!.Items).AssetId);
            Assert.True(File.Exists(Path.Combine(destination, "artwork", image.AssetId, "original")));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(destination)) Directory.Delete(destination, true); }
    }
    [Fact]
    public async Task ArtistProfileErrorsAreExplicitAndSchemaElevenStillBrowses()
    {
        var id = await ProfileArtist();
        var initial = await _client.GetAsync($"/api/v1/artists/{id}/profile");
        var profile = (await initial.Content.ReadFromJsonAsync<ArtistProfileResponse>())!;
        await ProblemAsync(await SaveArtistProfile(id, null, profile), 428, "precondition_required");
        await ProblemAsync(await SaveArtistProfile(id, initial.Headers.ETag!.ToString(), profile with { BiographySourceUrl = "file:///private" }), 400, "validation_failed");
        await ProblemAsync(await SaveArtistProfile(id, initial.Headers.ETag.ToString(), new { biography = "incomplete" }), 400, "validation_failed");
        await ProblemAsync(await _client.GetAsync("/api/v1/artists/missing/profile"), 404, "artist_not_found");
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync($"/api/v1/artists/{id}/artwork"), 401, "unauthorized");
        await ImportSql("DELETE FROM schema_migration WHERE version=12");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/artists/{id}")).StatusCode);
        await ProblemAsync(await _client.GetAsync($"/api/v1/artists/{id}/profile"), 409, "artist_profile_schema_required");
        await ProblemAsync(await _client.GetAsync($"/api/v1/artists/{id}/artwork"), 409, "artist_profile_schema_required");
    }
    [Fact]
    public async Task ArtistPortraitFailedHistoryRollsBackAssetAndFiles()
    {
        var id = await ProfileArtist(); var initial = await _client.GetAsync($"/api/v1/artists/{id}/artwork");
        await ImportSql("CREATE TRIGGER fail_artist_portrait BEFORE INSERT ON artist_artwork_revision BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.Equal(HttpStatusCode.InternalServerError, (await UploadArtistPortrait(id, initial.Headers.ETag!.ToString())).StatusCode);
        Assert.Equal(0, await ImportCount("artwork_asset")); Assert.Equal(0, await ImportCount("artist_artwork_link"));
        Assert.Empty(Directory.GetFiles(_path + ".artwork", "original", SearchOption.AllDirectories));
        Assert.Equal(initial.Headers.ETag.ToString(), (await _client.GetAsync($"/api/v1/artists/{id}/artwork")).Headers.ETag!.ToString());
    }
}
