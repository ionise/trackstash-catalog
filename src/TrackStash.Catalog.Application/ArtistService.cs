using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public sealed record CatalogArtistDetail(string Id, string? Name, string? SortName, string Version, string Kind,
    string IdentityStatus, IReadOnlyList<string> Aliases, IReadOnlyList<ReleaseReferenceDetail> ExternalReferences);
public sealed class ArtistBrowseUnavailableException : Exception;

public interface IArtistService
{
    ValueTask<CatalogArtistPage> QueryAsync(ArtistQuery query, CancellationToken ct = default);
    ValueTask<CatalogArtistDetail?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<ArtistCreditPage?> CreditsAsync(ArtistCreditQuery query, CancellationToken ct = default);
}

public sealed class ArtistService(IStorageProvider storage) : IArtistService
{
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
