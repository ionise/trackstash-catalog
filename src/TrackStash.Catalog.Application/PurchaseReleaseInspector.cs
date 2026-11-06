using System.Globalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

/// <summary>A source inspection plus up to 100 active label candidates and the full match count; no import authority.</summary>
public sealed record PurchaseReleaseInspectorResponse(PurchaseReleaseInspection Source,
    IReadOnlyList<LabelListItem> LabelCandidates, int LabelCandidateCount);

/// <summary>A purchase candidate plus the read-only catalog admission state for its Beatport release ID.</summary>
/// <param name="AdmittedReleaseIds">Catalog Releases owning the Beatport release ID; empty when not admitted or status is unavailable.</param>
public sealed record PurchaseReleaseCandidateItem(long ReleaseId, string Title, string SourceKind, bool HasCachedRelease,
    IReadOnlyList<string> AdmittedReleaseIds);

/// <summary>A candidate page; AdmissionStatusAvailable is false on catalogs older than schema 9.</summary>
public sealed record PurchaseReleaseCandidatePage(IReadOnlyList<PurchaseReleaseCandidateItem> Items, int Total, int Page, int PageSize,
    bool AdmissionStatusAvailable, bool HideAdmitted);

/// <summary>Admission filtering needs canonical release references from schema 9 or later.</summary>
public sealed class PurchaseAdmissionStatusUnavailableException : Exception;

public interface IPurchaseReleaseInspector
{
    ValueTask<PurchaseReleaseCandidatePage> QueryAsync(PurchaseReleaseQuery query, bool hideAdmitted = false, CancellationToken cancellationToken = default);
    ValueTask<PurchaseReleaseInspectorResponse?> InspectAsync(long releaseId, PurchaseReleaseQuery query, CancellationToken cancellationToken = default);
}

public sealed class PurchaseReleaseInspector(IPurchaseReleaseSource source, IStorageProvider storage) : IPurchaseReleaseInspector
{
    public async ValueTask<PurchaseReleaseCandidatePage> QueryAsync(PurchaseReleaseQuery query, bool hideAdmitted = false, CancellationToken cancellationToken = default)
    {
        query.Validate();
        var admitted = await AdmittedAsync(cancellationToken).ConfigureAwait(false);
        if (admitted is null && hideAdmitted) throw new PurchaseAdmissionStatusUnavailableException();
        var page = await source.QueryAsync(hideAdmitted ? query with { ExcludedReleaseIds = admitted!.Keys } : query, cancellationToken).ConfigureAwait(false);
        return new(page.Items.Select(item => new PurchaseReleaseCandidateItem(item.ReleaseId, item.Title, item.SourceKind, item.HasCachedRelease,
            admitted?.GetValueOrDefault(item.ReleaseId) ?? [])).ToList(), page.Total, page.Page, page.PageSize, admitted is not null, hideAdmitted);
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
