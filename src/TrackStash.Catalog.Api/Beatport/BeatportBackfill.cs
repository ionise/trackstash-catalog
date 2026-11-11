using System.Threading.Channels;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api.Beatport;

/// <summary>Result of filling in one purchase release's missing Beatport data.</summary>
public sealed record BeatportBackfillResult(long ReleaseId, bool SourceCompleteBefore, bool SourceCompleteAfter,
    bool ReleaseRefreshed, int TracksRequested, int TracksFetched, int TracksNotFound, IReadOnlyList<string> RemainingBlockers);

/// <summary>Progress of a bulk backfill over every incomplete purchase release.</summary>
public sealed record BeatportBackfillJobResponse(string Id, string Status, DateTimeOffset CreatedUtc, int TotalReleases,
    int ProcessedReleases, int CompletedReleases, int TracksFetched, int TracksNotFound, long? CurrentReleaseId,
    DateTimeOffset? FinishedUtc = null, string? Error = null, IReadOnlyList<long>? StillIncomplete = null);

public interface IBeatportBackfillService
{
    Task<BeatportBackfillResult> BackfillReleaseAsync(long releaseId, CancellationToken ct);
    Task<BeatportBackfillResult> PrepareCatalogReleaseAsync(long releaseId, CancellationToken ct);
    Task<IReadOnlyList<long>> IncompleteReleaseIdsAsync(CancellationToken ct);
}

/// <summary>Fetches missing release/track payloads by ID through the broker, which stores them where the purchase source reads them.</summary>
public sealed class BeatportBackfillService(IPurchaseReleaseSource source, IBeatportBrokerClient broker) : IBeatportBackfillService
{
    private const int PageSize = 100;

    public async Task<IReadOnlyList<long>> IncompleteReleaseIdsAsync(CancellationToken ct) =>
        (await source.InspectAllAsync(ct)).Where(r => !r.SourceComplete).Select(r => r.ReleaseId).ToArray();

    private async Task<(PurchaseReleaseInspection Inspection, List<SourceReleaseTrack> Tracks)> InspectAsync(long releaseId, CancellationToken ct,
        bool allowCachedRelease = false)
    {
        var first = await source.InspectAsync(releaseId, new PurchaseReleaseQuery(1, PageSize) { AllowCachedRelease = allowCachedRelease }, ct)
            ?? throw new ApiRequestException(404, "purchase_candidate_not_found", "This release is not in the configured purchase corpus.");
        var tracks = new List<SourceReleaseTrack>(first.Tracks.Items);
        for (var page = 2; tracks.Count < first.Tracks.Total; page++)
        {
            var next = await source.InspectAsync(releaseId, new PurchaseReleaseQuery(page, PageSize) { AllowCachedRelease = allowCachedRelease }, ct);
            if (next is null || next.Tracks.Items.Count == 0) break;
            tracks.AddRange(next.Tracks.Items);
        }
        return (first, tracks);
    }

    public Task<BeatportBackfillResult> BackfillReleaseAsync(long releaseId, CancellationToken ct) =>
        BackfillAsync(releaseId, false, ct);

    public Task<BeatportBackfillResult> PrepareCatalogReleaseAsync(long releaseId, CancellationToken ct) =>
        BackfillAsync(releaseId, true, ct);

    private async Task<BeatportBackfillResult> BackfillAsync(long releaseId, bool allowCachedRelease, CancellationToken ct)
    {
        if (releaseId <= 0) throw new ApiRequestException(400, "validation_failed", "Release ID must be a positive Beatport ID.");
        if (!broker.Configured)
            throw new ApiRequestException(503, "beatport_broker_not_configured", "The Beatport broker is not configured. Start TrackStash with start-prod.sh.");
        var refreshed = false;
        var sourceWasMissing = false;
        if (allowCachedRelease && await source.InspectAsync(releaseId, new() { AllowCachedRelease = true }, ct) is null)
        {
            sourceWasMissing = true;
            if (await broker.FetchAsync("releases", releaseId, refresh: false, ct) == BrokerFetchOutcome.NotFound)
                throw new ApiRequestException(404, "beatport_release_not_found", "Beatport no longer lists this release.");
            refreshed = true;
        }
        var (before, tracks) = await InspectAsync(releaseId, ct, allowCachedRelease);
        // Release-level blockers (no track position) mean the release payload itself is missing, stale or invalid.
        if (before.Diagnostics.Any(d => d.Severity == "blocker" && d.SourcePosition is null))
        {
            if (await broker.FetchAsync("releases", releaseId, refresh: true, ct) == BrokerFetchOutcome.NotFound)
                throw new ApiRequestException(404, "beatport_release_not_found", "Beatport no longer lists this release.");
            refreshed = true;
            (_, tracks) = await InspectAsync(releaseId, ct, allowCachedRelease);
        }
        int requested = 0, fetched = 0, notFound = 0;
        foreach (var track in tracks.Where(t => t.Status != "cached" && t.ProviderId is > 0))
        {
            ct.ThrowIfCancellationRequested();
            requested++;
            // An invalid payload may itself be a stale broker copy, so bypass the broker cache for those.
            var outcome = await broker.FetchAsync("tracks", track.ProviderId!.Value, refresh: track.Status == "invalid", ct);
            if (outcome == BrokerFetchOutcome.Fetched) fetched++; else notFound++;
        }
        var (after, _) = requested > 0 || refreshed ? await InspectAsync(releaseId, ct, allowCachedRelease) : (before, tracks);
        return new(releaseId, !sourceWasMissing && before.SourceComplete, after.SourceComplete, refreshed, requested, fetched, notFound,
            after.Diagnostics.Where(d => d.Severity == "blocker").Select(d => d.Code).Distinct().Order(StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Runs one bulk backfill at a time in the background; history is process-local.</summary>
public sealed class BeatportBackfillJobs(IServiceScopeFactory scopes, ILogger<BeatportBackfillJobs> logger, TimeProvider time) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BeatportBackfillJobResponse> _jobs = [];
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(1);
    private CancellationTokenSource? _current;
    private bool _active;

    public BeatportBackfillJobResponse Start()
    {
        lock (_gate)
        {
            if (_active) throw new ApiRequestException(409, "beatport_backfill_busy", "A Beatport backfill is already running.");
            var job = new BeatportBackfillJobResponse(Guid.NewGuid().ToString("N"), "queued", time.GetUtcNow(), 0, 0, 0, 0, 0, null);
            while (_jobs.Count >= 20) _jobs.Remove(_jobs.Values.OrderBy(j => j.CreatedUtc).First().Id);
            _jobs.Add(job.Id, job);
            if (!_queue.Writer.TryWrite(job.Id)) throw new InvalidOperationException("Backfill job queue is unavailable.");
            _active = true;
            return job;
        }
    }

    public BeatportBackfillJobResponse? Get(string id) { lock (_gate) return _jobs.GetValueOrDefault(id); }

    public BeatportBackfillJobResponse Cancel(string id)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(id) ?? throw new ApiRequestException(404, "beatport_backfill_job_not_found", "Backfill job not found.");
            if (job.Status is "queued" or "running") _current?.Cancel();
            return job;
        }
    }

    private void Change(string id, Func<BeatportBackfillJobResponse, BeatportBackfillJobResponse> update) { lock (_gate) _jobs[id] = update(_jobs[id]); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_gate) _current = cancel;
            var still = new List<long>();
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IBeatportBackfillService>();
                Change(id, j => j with { Status = "running" });
                var releases = await service.IncompleteReleaseIdsAsync(cancel.Token);
                Change(id, j => j with { TotalReleases = releases.Count });
                foreach (var releaseId in releases)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    Change(id, j => j with { CurrentReleaseId = releaseId });
                    try
                    {
                        var result = await service.BackfillReleaseAsync(releaseId, cancel.Token);
                        if (!result.SourceCompleteAfter) still.Add(releaseId);
                        Change(id, j => j with
                        {
                            ProcessedReleases = j.ProcessedReleases + 1, TracksFetched = j.TracksFetched + result.TracksFetched,
                            TracksNotFound = j.TracksNotFound + result.TracksNotFound,
                            CompletedReleases = j.CompletedReleases + (result.SourceCompleteAfter ? 1 : 0),
                        });
                    }
                    catch (ApiRequestException ex) when (ex.Code is "beatport_release_not_found" or "beatport_upstream_error")
                    {
                        // One bad release should not stop the run; session, broker and rate-limit failures do.
                        logger.LogWarning("Beatport backfill skipped release {ReleaseId}: {Code}", releaseId, ex.Code);
                        still.Add(releaseId);
                        Change(id, j => j with { ProcessedReleases = j.ProcessedReleases + 1 });
                    }
                }
                Change(id, j => j with { Status = "completed", CurrentReleaseId = null, FinishedUtc = time.GetUtcNow(), StillIncomplete = still });
            }
            catch (OperationCanceledException)
            {
                Change(id, j => j with { Status = "cancelled", FinishedUtc = time.GetUtcNow(), StillIncomplete = still,
                    Error = stoppingToken.IsCancellationRequested ? "Interrupted by server shutdown; start again to continue." : "Cancelled." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Beatport backfill job {JobId} failed", id);
                var message = ex is ApiRequestException request ? request.Message : "Backfill failed. Check the catalog and broker logs.";
                Change(id, j => j with { Status = "failed", FinishedUtc = time.GetUtcNow(), StillIncomplete = still, Error = message });
            }
            finally
            {
                lock (_gate) { _active = false; _current = null; }
            }
        }
    }
}
