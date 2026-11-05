using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

internal sealed class UnconfiguredPurchaseReleaseSource : IPurchaseReleaseSource
{
    private static Exception Error() => new PurchaseSourceUnavailableException(
        "Configure CatalogApi:PurchaseSourceDatabasePath or CatalogApi:ScannerDatabasePath and restart the API.");
    public ValueTask<PurchaseSourcePage<PurchaseReleaseCandidate>> QueryAsync(PurchaseReleaseQuery query, CancellationToken cancellationToken = default) => throw Error();
    public ValueTask<PurchaseReleaseInspection?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default) => throw Error();
}
