using TrackStash.Core.Storage;
using TrackStash.Core.Sqlite;
using TrackStash.Catalog.Api.Beatport;

namespace TrackStash.Catalog.Api;

internal sealed class UnconfiguredPurchaseReleaseSource : IPurchaseReleaseSource
{
    private static Exception Error() => new PurchaseSourceUnavailableException(
        "Configure CatalogApi:PurchaseSourceDatabasePath or CatalogApi:ScannerDatabasePath and restart the API.");
    public ValueTask<PurchaseSourcePage<PurchaseReleaseCandidate>> QueryAsync(PurchaseReleaseQuery query, CancellationToken cancellationToken = default) => throw Error();
    public ValueTask<string> GetSnapshotStampAsync(CancellationToken cancellationToken = default) => throw Error();
    public ValueTask<IReadOnlyList<PurchaseReleaseInspection>> InspectAllAsync(CancellationToken cancellationToken = default) => throw Error();
    public ValueTask<PurchaseReleaseInspection?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default) => throw Error();
}

internal sealed class BrokerCatalogReleaseSource(IPurchaseReleaseSource? purchase, BeatportBrokerClient broker) : IPurchaseReleaseSource
{
    private IPurchaseReleaseSource Purchase => purchase ?? new UnconfiguredPurchaseReleaseSource();

    public ValueTask<PurchaseSourcePage<PurchaseReleaseCandidate>> QueryAsync(PurchaseReleaseQuery query, CancellationToken cancellationToken = default) =>
        Purchase.QueryAsync(query, cancellationToken);

    public ValueTask<string> GetSnapshotStampAsync(CancellationToken cancellationToken = default) =>
        Purchase.GetSnapshotStampAsync(cancellationToken);

    public ValueTask<IReadOnlyList<PurchaseReleaseInspection>> InspectAllAsync(CancellationToken cancellationToken = default) =>
        Purchase.InspectAllAsync(cancellationToken);

    public async ValueTask<PurchaseReleaseInspection?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.AllowCachedRelease) return await Purchase.InspectAsync(releaseId, query, cancellationToken);
        query.Validate();
        if (releaseId <= 0 || query.Search is not null)
            throw new PurchaseSourceValidationException("Use a positive release ID and pagination only for inspection.");
        var existing = purchase is null ? null : await purchase.InspectAsync(releaseId, query, cancellationToken);
        if (existing?.SourceComplete == true || !broker.Configured) return existing;
        var release = await broker.GetCachedEntityAsync("releases", releaseId, cancellationToken);
        if (release is null) return existing;
        return await SqlitePurchaseReleaseSource.InspectCachedAsync(releaseId, query, release,
            (id, ct) => broker.GetCachedEntityAsync("tracks", id, ct), cancellationToken);
    }
}
