namespace TrackStash.Catalog.Application;

/// <summary>Loads public Artist, Label or Release identity snapshots for read-only enrichment.</summary>
public interface INamedEntityEnrichmentService
{
    ValueTask<EnrichmentLookup> FindAsync(EnrichmentEntityKind kind, string id, string providerId, CancellationToken ct = default,
        string? candidateId = null, string? editionId = null);
}

public sealed class NamedEntityEnrichmentService(IArtistService artists, ILabelService labels, IReleaseService releases,
    IEnumerable<IEnrichmentProvider> providers) : INamedEntityEnrichmentService
{
    public async ValueTask<EnrichmentLookup> FindAsync(EnrichmentEntityKind kind, string id, string providerId, CancellationToken ct = default,
        string? candidateId = null, string? editionId = null)
    {
        EnrichmentQuery query;
        if (kind == EnrichmentEntityKind.Artist)
        {
            var artist = await artists.GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new EnrichmentRequestException("artist_not_found", "Catalog Artist was not found.");
            query = new(kind, artist.Id, artist.Version, artist.Name, null, [], null, null, null, null, null,
                artist.ExternalReferences.Select(r => new EnrichmentReference(r.Source, r.ExternalId)).ToArray());
        }
        else if (kind == EnrichmentEntityKind.Label)
        {
            var label = await labels.GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new EnrichmentRequestException("label_not_found", "Catalog Label was not found.");
            query = new(kind, label.Id, label.Version, label.Name, null, [], null, null, null, null, null,
                label.ExternalReferences.Select(r => new EnrichmentReference(r.Source, r.ExternalId)).ToArray());
        }
        else if (kind == EnrichmentEntityKind.Release)
        {
            var release = await releases.GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new EnrichmentRequestException("release_not_found", "Catalog Release was not found.");
            var primary = release.Credits.Where(c => c.Role == "primary").Select(c => c.CreditedName ?? c.ArtistName)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var labelNames = release.Labels.Select(l => l.Name).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            query = new(kind, release.Id, release.Version, release.Title, null, primary, null, release.CatalogNumber,
                labelNames.Length == 1 ? labelNames[0] : null, null, null,
                release.ExternalReferences.Select(r => new EnrichmentReference(r.Source, r.ExternalId)).ToArray(),
                labelNames, candidateId, editionId, release.PublishDate, release.MusicalMetadata);
        }
        else throw new NotSupportedException("Named entity enrichment supports Artists, Labels and Releases only.");
        if (kind != EnrichmentEntityKind.Release && (candidateId is not null || editionId is not null))
            throw new EnrichmentRequestException("enrichment_query_incomplete", "Only Release lookups support candidate and edition selection.");
        return await EnrichmentLookupRunner.FindAsync(query, providerId, providers, ct).ConfigureAwait(false);
    }
}
