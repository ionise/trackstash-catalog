using System.Globalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

/// <summary>A source inspection plus up to 100 active label candidates and the full match count; no import authority.</summary>
public sealed record PurchaseReleaseInspectorResponse(PurchaseReleaseInspection Source,
    IReadOnlyList<LabelListItem> LabelCandidates, int LabelCandidateCount);

public interface IPurchaseReleaseInspector
{
    ValueTask<PurchaseSourcePage<PurchaseReleaseCandidate>> QueryAsync(PurchaseReleaseQuery query, CancellationToken cancellationToken = default);
    ValueTask<PurchaseReleaseInspectorResponse?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default);
}

public sealed class PurchaseReleaseInspector(IPurchaseReleaseSource source, IStorageProvider storage) : IPurchaseReleaseInspector
{
    public ValueTask<PurchaseSourcePage<PurchaseReleaseCandidate>> QueryAsync(PurchaseReleaseQuery query, CancellationToken cancellationToken = default) =>
        source.QueryAsync(query, cancellationToken);

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
