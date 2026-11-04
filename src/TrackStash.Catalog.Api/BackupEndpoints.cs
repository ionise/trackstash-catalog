using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Completed filesystem backups for this configured catalog.</summary>
public sealed record BackupListResponse(IReadOnlyList<CatalogBackupInfo> Items, int RetainNewest);

public static class BackupEndpoints
{
    public static void MapBackups(this WebApplication app)
    {
        app.MapGet("/api/v1/backups", async (ICatalogBackupService service, CancellationToken ct) =>
            TypedResults.Ok(new BackupListResponse(await service.ListAsync(ct), 10)))
            .WithName("ListBackups").WithSummary("List saved catalog ZIP backups")
            .WithDescription("Archives are scoped to this catalog's source path. Download re-verifies checksums. No credentials or raw storage paths are returned.");
        app.MapPost("/api/v1/backup-jobs", async (HttpContext context, BackupJobs jobs, CancellationToken ct) =>
        {
            if (await context.Request.Body.ReadAsync(new byte[1], ct) != 0)
                throw new ApiRequestException(400, "invalid_request", "This operation does not accept a request body or destination path.");
            ct.ThrowIfCancellationRequested();
            var job = jobs.Start();
            return TypedResults.Accepted($"/api/v1/backup-jobs/{job.Id}", job);
        }).WithName("CreateBackupJob").WithSummary("Start a verified filesystem ZIP backup")
            .WithDescription("Returns 202 and a job status Location. Continues after the initiating request disconnects. Uses the server-configured destination, retains newest 10 verified backups, and rejects concurrent jobs.")
            .Produces<Microsoft.AspNetCore.Mvc.ProblemDetails>(409);
        app.MapGet("/api/v1/backup-jobs/{id}", (string id, BackupJobs jobs, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return jobs.Get(id) is { } job ? TypedResults.Ok(job)
                : throw new ApiRequestException(404, "backup_job_not_found", "Backup job not found. Job history resets when the server restarts; check saved backups.");
        }).WithName("GetBackupJob").WithSummary("Get backup job status");
        app.MapGet("/api/v1/backups/{id}/download", async (string id, HttpContext context, ICatalogBackupService service, CancellationToken ct) =>
        {
            var info = (await service.ListAsync(ct)).SingleOrDefault(b => b.Id == id)
                ?? throw new ApiRequestException(404, "backup_not_found", "Backup not found.");
            Stream stream;
            try { stream = await service.OpenDownloadAsync(id, ct); }
            catch (FileNotFoundException) { throw new ApiRequestException(404, "backup_not_found", "Backup not found."); }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.Stream(stream, "application/zip", info.Filename, enableRangeProcessing: false);
        }).WithName("DownloadBackup").WithSummary("Download the same saved ZIP after verification")
            .WithDescription("Bearer-authenticated streamed response. Holds a cross-process directory lease until download finishes, preventing pruning or simultaneous backups.")
            .Produces(200, contentType: "application/zip").Produces<Microsoft.AspNetCore.Mvc.ProblemDetails>(404);
    }
}
