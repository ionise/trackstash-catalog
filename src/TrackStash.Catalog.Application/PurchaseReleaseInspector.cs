using System.Globalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

/// <summary>A source inspection plus up to 100 active label candidates and the full match count; no import authority.</summary>
public sealed record PurchaseReleaseInspectorResponse(PurchaseReleaseInspection Source,
    IReadOnlyList<LabelListItem> LabelCandidates, int LabelCandidateCount);

/// <summary>Import readiness derived from the cached source bundle and current catalog label matches.</summary>
/// <param name="Readiness"><c>ready</c>: complete cached source and exactly one active catalog label;
/// <c>incomplete_source</c>: cached source has blockers; <c>label_review</c>: the source label matches no or several catalog labels.</param>
/// <param name="BlockerCodes">Distinct source blocker codes; empty when the cached source is complete.</param>
public sealed record PurchaseReleaseReadiness(string Readiness, int HydratedTrackCount, int ManifestTrackCount,
    int LabelCandidateCount, IReadOnlyList<string> BlockerCodes);

/// <summary>A purchase candidate plus read-only catalog admission state and import readiness for its Beatport release ID.</summary>
/// <param name="AdmittedReleaseIds">Catalog Releases owning the Beatport release ID; empty when not admitted or status is unavailable.</param>
public sealed record PurchaseReleaseCandidateItem(long ReleaseId, string Title, string SourceKind, bool HasCachedRelease,
    IReadOnlyList<string> AdmittedReleaseIds, PurchaseReleaseReadiness? Readiness);

/// <summary>A candidate page; AdmissionStatusAvailable is false on catalogs older than schema 9.</summary>
public sealed record PurchaseReleaseCandidatePage(IReadOnlyList<PurchaseReleaseCandidateItem> Items, int Total, int Page, int PageSize,
    bool AdmissionStatusAvailable, bool HideAdmitted, bool ReadyOnly);

/// <summary>Admission filtering needs canonical release references from schema 9 or later.</summary>
public sealed class PurchaseAdmissionStatusUnavailableException : Exception;

public interface IPurchaseReleaseInspector
{
    ValueTask<PurchaseReleaseCandidatePage> QueryAsync(PurchaseReleaseQuery query, bool hideAdmitted = false, bool readyOnly = false,
        CancellationToken cancellationToken = default);
    ValueTask<PurchaseReleaseInspectorResponse?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Source-only completeness of one cached release bundle; label matching is resolved per request.</summary>
public sealed record PurchaseSourceSummary(bool SourceComplete, int HydratedTrackCount, int ManifestTrackCount,
    long? LabelProviderId, IReadOnlyList<string> BlockerCodes);

/// <summary>Process-wide cache of per-release source summaries, rebuilt only when the purchase source snapshot stamp changes.</summary>
public sealed class PurchaseReadinessCache(IPurchaseReleaseSource source)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Stamp, Dictionary<long, PurchaseSourceSummary> Items)? _current;

    public async ValueTask<IReadOnlyDictionary<long, PurchaseSourceSummary>> GetAsync(CancellationToken ct)
    {
        var stamp = await source.GetSnapshotStampAsync(ct).ConfigureAwait(false);
        if (_current is { } hit && hit.Stamp == stamp) return hit.Items;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            stamp = await source.GetSnapshotStampAsync(ct).ConfigureAwait(false);
            if (_current is { } again && again.Stamp == stamp) return again.Items;
            var items = (await source.InspectAllAsync(ct).ConfigureAwait(false)).ToDictionary(inspection => inspection.ReleaseId, inspection =>
                new PurchaseSourceSummary(inspection.SourceComplete, inspection.HydratedTrackCount, inspection.ManifestTrackCount, inspection.Label?.ProviderId,
                    inspection.Diagnostics.Where(d => d.Severity == "blocker").Select(d => d.Code).Distinct(StringComparer.Ordinal).ToList()));
            _current = (stamp, items);
            return items;
        }
        finally { _gate.Release(); }
    }
}

public sealed class PurchaseReleaseInspector(IPurchaseReleaseSource source, IStorageProvider storage, PurchaseReadinessCache readiness) : IPurchaseReleaseInspector
{
    public async ValueTask<PurchaseReleaseCandidatePage> QueryAsync(PurchaseReleaseQuery query, bool hideAdmitted = false, bool readyOnly = false,
        CancellationToken cancellationToken = default)
    {
        query.Validate();
        var admitted = await AdmittedAsync(cancellationToken).ConfigureAwait(false);
        if (admitted is null && hideAdmitted) throw new PurchaseAdmissionStatusUnavailableException();
        var summaries = await readiness.GetAsync(cancellationToken).ConfigureAwait(false);
        var labelCounts = await LabelCountsAsync(summaries.Values.Select(s => s.LabelProviderId).OfType<long>().Distinct(), cancellationToken).ConfigureAwait(false);
        PurchaseReleaseReadiness? Readiness(long id) => summaries.TryGetValue(id, out var s) ? new(
            !s.SourceComplete ? "incomplete_source" : s.LabelProviderId is { } label && labelCounts.GetValueOrDefault(label) == 1 ? "ready" : "label_review",
            s.HydratedTrackCount, s.ManifestTrackCount, s.LabelProviderId is { } l ? labelCounts.GetValueOrDefault(l) : 0, s.BlockerCodes) : null;
        var filtered = query with
        {
            ExcludedReleaseIds = hideAdmitted ? admitted!.Keys : null,
            IncludedReleaseIds = readyOnly ? summaries.Keys.Where(id => Readiness(id)?.Readiness == "ready").ToList() : null,
        };
        var page = await source.QueryAsync(filtered, cancellationToken).ConfigureAwait(false);
        return new(page.Items.Select(item => new PurchaseReleaseCandidateItem(item.ReleaseId, item.Title, item.SourceKind, item.HasCachedRelease,
            admitted?.GetValueOrDefault(item.ReleaseId) ?? [], Readiness(item.ReleaseId))).ToList(),
            page.Total, page.Page, page.PageSize, admitted is not null, hideAdmitted, readyOnly);
    }

    private async ValueTask<Dictionary<long, int>> LabelCountsAsync(IEnumerable<long> labelIds, CancellationToken ct)
    {
        var counts = new Dictionary<long, int>();
        await using var uow = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        foreach (var id in labelIds)
            counts[id] = (await uow.Labels.QueryAsync(new LabelQuery
            {
                Source = "beatport", ExternalId = id.ToString(CultureInfo.InvariantCulture), PageSize = 1,
            }, ct).ConfigureAwait(false)).Total;
        return counts;
    }

    private async ValueTask<Dictionary<long, IReadOnlyList<string>>?> AdmittedAsync(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 9) return null;
        await using var uow = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        if (uow.CatalogFoundations is not { } foundations) return null;
        var owners = await foundations.ListReferenceOwnersAsync(CatalogEntityKind.Release, "beatport", ct).ConfigureAwait(false);
        return owners
            .Where(owner => long.TryParse(owner.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            .GroupBy(owner => long.Parse(owner.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(owner => owner.OwnerId).Distinct(StringComparer.Ordinal).ToList());
    }

    public async ValueTask<PurchaseReleaseInspectorResponse?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default)
    {
        var inspection = await source.InspectAsync(releaseId, query, cancellationToken).ConfigureAwait(false);
        if (inspection is null) return null;
        if (inspection.Label is null) return new(inspection, [], 0);
        await using var uow = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await uow.Labels.QueryAsync(new LabelQuery
        {
            Source = "beatport", ExternalId = inspection.Label.ProviderId.ToString(CultureInfo.InvariantCulture), PageSize = 100,
        }, cancellationToken).ConfigureAwait(false);
        return new(inspection, candidates.Items, candidates.Total);
    }
}
