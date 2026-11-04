using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TrackStash.Catalog.Api;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task<BackupJobResponse> WaitBackupAsync(string id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = (await _client.GetFromJsonAsync<BackupJobResponse>($"/api/v1/backup-jobs/{id}"))!;
            if (job.Status is "failed" or "completed") return job;
            await Task.Delay(20);
        }
        throw new TimeoutException("Backup job did not finish.");
    }

    [Fact]
    public async Task BackupApi_FirstVisitReturnsEmptyListWithoutCreatingAnArchive()
    {
        Assert.False(Directory.Exists(_path + ".backups"));
        var response = await _client.GetAsync("/api/v1/backups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<BackupListResponse>())!;
        Assert.Empty(list.Items);
        Assert.Equal(10, list.RetainNewest);
        Assert.Empty(Directory.GetFiles(_path + ".backups", "*.zip"));
    }
    [Fact]
    public async Task BackupApi_CreatesVerifiedArchiveDownloadsSameBytesAndListsAfterRestart()
    {
        var (label, etag) = await CreateAsync("Backup test label");
        var upload = await UploadAsync(label.Id, etag);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var response = await _client.PostAsync("/api/v1/backup-jobs", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<BackupJobResponse>())!;
        Assert.Equal($"/api/v1/backup-jobs/{queued.Id}", response.Headers.Location!.OriginalString);
        var job = await WaitBackupAsync(queued.Id);
        Assert.Equal("completed", job.Status);
        Assert.NotNull(job.Backup);
        var info = Assert.Single((await _client.GetFromJsonAsync<BackupListResponse>("/api/v1/backups"))!.Items);
        var download = await _client.GetAsync($"/api/v1/backups/{info.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains(info.Filename, download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("no-store", download.Headers.CacheControl!.ToString());
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_path + ".backups", info.Filename)), bytes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "catalog.db");
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("/original", StringComparison.Ordinal));
        Assert.DoesNotContain(Key, await new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEndAsync());
        await _app.StopAsync();
        await _app.DisposeAsync();
        _client.Dispose();
        _app = Build();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        Assert.Equal(info, Assert.Single((await _client.GetFromJsonAsync<BackupListResponse>("/api/v1/backups"))!.Items));
        await ProblemAsync(await _client.GetAsync($"/api/v1/backup-jobs/{queued.Id}"), 404, "backup_job_not_found");
    }
    [Fact]
    public async Task BackupApi_RejectsUnauthorizedOriginsPathsAndConcurrentDownloads()
    {
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.PostAsync("/api/v1/backup-jobs", null), 401, "unauthorized");
        await ProblemAsync(await anonymous.GetAsync("/api/v1/backups"), 401, "unauthorized");
        await ProblemAsync(await anonymous.GetAsync("/api/v1/backups/anything/download"), 401, "unauthorized");
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backup-jobs");
        foreign.Headers.Add("Origin", "https://example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(foreign)).StatusCode);
        await ProblemAsync(await _client.PostAsJsonAsync("/api/v1/backup-jobs", new { destination = "/tmp" }), 400, "invalid_request");
        await ProblemAsync(await _client.GetAsync("/api/v1/backups/not-found/download"), 404, "backup_not_found");
        var service = _app.Services.GetRequiredService<ICatalogBackupService>();
        var backup = (await service.CreateAsync()).Backup;
        await using var held = await service.OpenDownloadAsync(backup.Id);
        await ProblemAsync(await _client.GetAsync($"/api/v1/backups/{backup.Id}/download"), 409, "backup_busy");
        var response = await _client.PostAsync("/api/v1/backup-jobs", null);
        var job = await WaitBackupAsync((await response.Content.ReadFromJsonAsync<BackupJobResponse>())!.Id);
        Assert.Equal("failed", job.Status);
        Assert.Contains("Another backup", job.Error);
    }
    [Fact]
    public async Task BackupApi_MissingOriginalFailsExplicitlyAndDoesNotAdvertiseArchive()
    {
        var (label, etag) = await CreateAsync();
        var gallery = (await (await UploadAsync(label.Id, etag)).Content.ReadFromJsonAsync<LabelArtworkResponse>())!;
        File.Delete(Path.Combine(_path + ".artwork", gallery.Items[0].AssetId, "original"));
        var response = await _client.PostAsync("/api/v1/backup-jobs", null);
        var job = await WaitBackupAsync((await response.Content.ReadFromJsonAsync<BackupJobResponse>())!.Id);
        Assert.Equal("failed", job.Status);
        Assert.NotNull(job.Error);
        Assert.Null(job.Backup);
        Assert.Empty((await _client.GetFromJsonAsync<BackupListResponse>("/api/v1/backups"))!.Items);
        using var openApi = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));
        Assert.True(openApi.RootElement.GetProperty("paths").TryGetProperty("/api/v1/backup-jobs", out _));
        Assert.True(openApi.RootElement.GetProperty("paths").TryGetProperty("/api/v1/backups/{id}/download", out _));
    }
}
