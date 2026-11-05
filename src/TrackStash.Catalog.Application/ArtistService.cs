using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public sealed record CatalogArtistDetail(string Id, string? Name, string? SortName, string Version, string Kind,
    string IdentityStatus, IReadOnlyList<string> Aliases, IReadOnlyList<ReleaseReferenceDetail> ExternalReferences);
public sealed class ArtistBrowseUnavailableException : Exception;
public sealed class ArtistProfileUnavailableException : Exception;
public sealed record ArtistAppearanceDetail(ArtistAppearanceListing Appearance, string? PublishDate,
    string? NewReleaseDate, IReadOnlyList<ReleaseContributorDetail> Contributors);
public sealed record ArtistAppearanceDetailPage(IReadOnlyList<ArtistAppearanceDetail> Items, int Total, int Page, int PageSize);

public interface IArtistService
{
    ValueTask<ArtistAppearanceDetailPage?> AppearancesAsync(ArtistCreditQuery query, CancellationToken ct = default);
    ValueTask<ArtistProfileSnapshot?> GetProfileAsync(string id, CancellationToken ct = default);
    ValueTask<ArtistProfileResult> SaveProfileAsync(string id, string version, ArtistProfile profile, CancellationToken ct = default);
    ValueTask<CatalogArtistPage> QueryAsync(ArtistQuery query, CancellationToken ct = default);
    ValueTask<CatalogArtistDetail?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<ArtistCreditPage?> CreditsAsync(ArtistCreditQuery query, CancellationToken ct = default);
}

public sealed class ArtistService(IStorageProvider storage) : IArtistService
{
    public async ValueTask<ArtistAppearanceDetailPage?> AppearancesAsync(ArtistCreditQuery query, CancellationToken ct = default)
    {
        new ReleaseQuery(query.Page, query.PageSize).Validate(); await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        if (await unit.Artists.GetByIdAsync(query.ArtistId, ct).ConfigureAwait(false) is null) return null;
        var page = await unit.Artists.AppearancesAsync(query, ct).ConfigureAwait(false);
        var items = new List<ArtistAppearanceDetail>();
        var releases = new Dictionary<string, Release>(StringComparer.Ordinal);
        var placements = new Dictionary<string, ReleasePlacement>(StringComparer.Ordinal);
        var recordings = new Dictionary<string, IReadOnlyList<CatalogCredit>>(StringComparer.Ordinal);
        var artists = new Dictionary<string, Artist>(StringComparer.Ordinal);
        var foundations = unit.CatalogFoundations!;
        foreach (var item in page.Items)
        {
            Release? release = null;
            if (item.ReleaseId is not null)
            {
                if (!releases.TryGetValue(item.ReleaseId, out release))
                {
                    release = await unit.Releases.GetByIdAsync(item.ReleaseId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Artist appearance refers to a missing Release.");
                    releases.Add(release.Id, release);
                    if (query.Tracks)
                        foreach (var placement in await foundations.GetPlacementsAsync(release.Id, ct).ConfigureAwait(false))
                            placements.Add(placement.Id, placement);
                }
            }
            var credits = new List<CatalogCredit>();
            if (query.Tracks)
            {
                if (item.PlacementId is not null)
                {
                    if (!placements.TryGetValue(item.PlacementId, out var placement))
                        throw new InvalidOperationException("Artist appearance refers to a missing track placement.");
                    credits.AddRange(placement.Credits);
                }
                if (item.RecordingId is not null)
                {
                    if (!recordings.TryGetValue(item.RecordingId, out var recordingCredits))
                    {
                        recordingCredits = await foundations.GetCreditsAsync(CatalogEntityKind.Recording, item.RecordingId, ct).ConfigureAwait(false);
                        recordings.Add(item.RecordingId, recordingCredits);
                    }
                    credits.AddRange(recordingCredits);
                }
            }
            else if (release is not null)
                credits.AddRange(await foundations.GetCreditsAsync(CatalogEntityKind.Release, release.Id, ct).ConfigureAwait(false));
            items.Add(new(item, release?.PublishDate, release?.NewReleaseDate,
                await ReleaseService.Credits(unit, credits.DistinctBy(c => (c.ArtistId, c.Role, c.CreditedName)).ToArray(), artists, ct).ConfigureAwait(false)));
        }
        return new(items, page.Total, page.Page, page.PageSize);
    }
    private async ValueTask ProfileReady(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 12) throw new ArtistProfileUnavailableException();
    }
    public async ValueTask<ArtistProfileSnapshot?> GetProfileAsync(string id, CancellationToken ct = default)
    {
        await ProfileReady(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        return await unit.Artists.GetProfileAsync(id, ct).ConfigureAwait(false);
    }
    public async ValueTask<ArtistProfileResult> SaveProfileAsync(string id, string version, ArtistProfile profile, CancellationToken ct = default)
    {
        profile.Validate(); await ProfileReady(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var result = await unit.Artists.SaveProfileAsync(id, version, profile, ct).ConfigureAwait(false);
        if (result.Status == LabelUpdateStatus.Updated) await unit.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }
    private async ValueTask Ready(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 9)
            throw new ArtistBrowseUnavailableException();
    }
    public async ValueTask<CatalogArtistPage> QueryAsync(ArtistQuery query, CancellationToken ct = default)
    {
        query.Validate(); await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        return await unit.Artists.QueryAsync(query, ct).ConfigureAwait(false);
    }
    public async ValueTask<CatalogArtistDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var artist = await unit.Artists.GetByIdAsync(id, ct).ConfigureAwait(false);
        return artist is null ? null : new(artist.Id, artist.Name, artist.SortName, artist.Version, artist.Kind,
            artist.IdentityStatus, artist.Aliases.Select(a => a.Value).ToArray(),
            artist.ExternalReferences.Select(r => new ReleaseReferenceDetail(r.Source, r.ExternalId, r.IsPrimary)).ToArray());
    }
    public async ValueTask<ArtistCreditPage?> CreditsAsync(ArtistCreditQuery query, CancellationToken ct = default)
    {
        new ReleaseQuery(query.Page, query.PageSize).Validate(); await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        if (await unit.Artists.GetByIdAsync(query.ArtistId, ct).ConfigureAwait(false) is null) return null;
        return await unit.Artists.CreditsAsync(query, ct).ConfigureAwait(false);
    }
}
