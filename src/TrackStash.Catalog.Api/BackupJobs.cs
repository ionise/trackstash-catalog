using System.Threading.Channels;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Backup job progress. Job history is process-local; completed ZIPs survive restarts.</summary>
public sealed record BackupJobResponse(string Id, string Status, string Stage, DateTimeOffset CreatedUtc,
    CatalogBackupInfo? Backup = null, IReadOnlyList<string>? Warnings = null, string? Error = null);

public sealed class BackupJobs(ICatalogBackupService backups, ILogger<BackupJobs> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BackupJobResponse> _jobs = [];
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(1);
    private bool _active;

    public BackupJobResponse Start()
    {
        lock (_gate)
        {
            if (_active) throw new BackupBusyException();
            var job = new BackupJobResponse(Guid.NewGuid().ToString("N"), "queued", "Queued", DateTimeOffset.UtcNow);
            while (_jobs.Count >= 100) _jobs.Remove(_jobs.Values.OrderBy(j => j.CreatedUtc).First().Id);
            _jobs.Add(job.Id, job);
            if (!_queue.Writer.TryWrite(job.Id)) throw new InvalidOperationException("Backup job queue is unavailable.");
            _active = true;
            return job;
        }
    }
    public BackupJobResponse? Get(string id) { lock (_gate) return _jobs.GetValueOrDefault(id); }
    private void Change(string id, Func<BackupJobResponse, BackupJobResponse> update)
    {
        lock (_gate) _jobs[id] = update(_jobs[id]);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                Change(id, j => j with { Status = "running", Stage = "Starting" });
                var result = await backups.CreateAsync(new JobProgress(stage => Change(id, j => j with { Stage = stage })), stoppingToken);
                foreach (var warning in result.Warnings) logger.LogWarning("Backup retention: {Warning}", warning);
                Change(id, j => j with { Status = "completed", Stage = "Verified and saved", Backup = result.Backup, Warnings = result.Warnings });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup job {JobId} failed", id);
                var message = ex is OperationCanceledException ? "Backup interrupted by server shutdown; retry after restart."
                    : ex is BackupBusyException ? ex.Message
                    : "Backup failed. Check server logs for missing/corrupt artwork, storage permissions or available space.";
                Change(id, j => j with { Status = "failed", Stage = "Failed", Error = message });
            }
            finally { lock (_gate) _active = false; }
        }
    }
    private sealed class JobProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
