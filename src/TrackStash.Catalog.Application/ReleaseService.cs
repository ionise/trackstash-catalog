using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public sealed record ReleaseLabelDetail(string Id, string? Name, string? Role, bool IsPrimary);
public sealed record ReleaseContributorDetail(string ArtistId, string? ArtistName, string? CreditedName,
    string Role, int? Position, string IdentityStatus);
public sealed record ReleaseReferenceDetail(string Source, string ExternalId, bool IsPrimary);
public sealed record CatalogReleaseDetail(string Id, string? Title, string Version, string? CatalogNumber, string? Upc,
    string? PublishDate, string? NewReleaseDate, IReadOnlyList<ReleaseLabelDetail> Labels,
    IReadOnlyList<ReleaseContributorDetail> Credits, IReadOnlyList<ReleaseReferenceDetail> ExternalReferences);
public sealed record ReleaseTrackDetail(string PlacementId, string RecordingId, string? Title, string? MixName,
    string? Isrc, long? DurationMs, int DisplayOrder, int? SourcePosition, string OrderSource, int? DiscNumber, int? TrackNumber,
    IReadOnlyList<ReleaseContributorDetail> Credits, IReadOnlyList<ReleaseReferenceDetail> ExternalReferences);
public sealed record ReleaseTrackPage(IReadOnlyList<ReleaseTrackDetail> Items, int Total, int Page, int PageSize);
public sealed class ReleaseBrowseUnavailableException : Exception;

public interface IReleaseService
{
    ValueTask<CatalogReleasePage> QueryAsync(ReleaseQuery query, CancellationToken cancellationToken = default);
    ValueTask<CatalogReleaseDetail?> GetAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<ReleaseTrackPage?> TracksAsync(string id, ReleaseQuery query, CancellationToken cancellationToken = default);
}

public sealed class ReleaseService(IStorageProvider storage) : IReleaseService
{
    private async ValueTask Ready(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 9)
            throw new ReleaseBrowseUnavailableException();
    }
    public async ValueTask<CatalogReleasePage> QueryAsync(ReleaseQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); await Ready(cancellationToken).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        return await unit.Releases.QueryAsync(query, cancellationToken).ConfigureAwait(false);
    }
    private static async Task<IReadOnlyList<ReleaseContributorDetail>> Credits(IUnitOfWork unit, IReadOnlyList<CatalogCredit> credits,
        Dictionary<string, Artist> artists, CancellationToken ct)
    {
        var items = new List<ReleaseContributorDetail>();
        foreach (var credit in credits)
        {
            if (!artists.TryGetValue(credit.ArtistId, out var artist))
            {
                artist = await unit.Artists.GetByIdAsync(credit.ArtistId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Catalog credit refers to a missing Artist.");
                artists.Add(artist.Id, artist);
            }
            items.Add(new(artist.Id, artist.Name, credit.CreditedName, credit.Role, credit.Position, artist.IdentityStatus));
        }
        return items;
    }
    public async ValueTask<CatalogReleaseDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await Ready(cancellationToken).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var release = await unit.Releases.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (release is null) return null;
        var labels = new List<ReleaseLabelDetail>();
        foreach (var link in release.LabelLinks)
        {
            var label = await unit.Labels.GetByIdAsync(link.LabelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Catalog release refers to a missing Label.");
            labels.Add(new(label.Id, label.Name, link.Role, link.IsPrimary));
        }
        var credits = await unit.CatalogFoundations!.GetCreditsAsync(CatalogEntityKind.Release, id, cancellationToken).ConfigureAwait(false);
        return new(release.Id, release.Title ?? release.Name, release.Version, release.CatalogNumber, release.Upc,
            release.PublishDate, release.NewReleaseDate, labels,
            await Credits(unit, credits, [], cancellationToken).ConfigureAwait(false),
            release.ExternalReferences.Select(r => new ReleaseReferenceDetail(r.Source, r.ExternalId, r.IsPrimary)).ToArray());
    }
    public async ValueTask<ReleaseTrackPage?> TracksAsync(string id, ReleaseQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); await Ready(cancellationToken).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        if (await unit.Releases.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var placements = await unit.CatalogFoundations!.GetPlacementsAsync(id, cancellationToken).ConfigureAwait(false);
        var items = new List<ReleaseTrackDetail>();
        var artists = new Dictionary<string, Artist>(StringComparer.Ordinal);
        foreach (var placement in placements.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Skip((int)Math.Min(((long)query.Page - 1) * query.PageSize, placements.Count)).Take(query.PageSize))
        {
            var recording = await unit.Recordings.GetByIdAsync(placement.RecordingId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Release placement refers to a missing Recording.");
            items.Add(new(placement.Id, recording.Id, recording.Title ?? recording.Name, recording.MixName, recording.Isrc,
                recording.DurationMs, placement.DisplayOrder, placement.SourcePosition, placement.OrderSource,
                placement.DiscNumber, placement.TrackNumber,
                await Credits(unit, placement.Credits, artists, cancellationToken).ConfigureAwait(false),
                placement.ExternalReferences.Select(r => new ReleaseReferenceDetail(r.Source, r.ExternalId, r.IsPrimary)).ToArray()));
        }
        return new(items, placements.Count, query.Page, query.PageSize);
    }
}
