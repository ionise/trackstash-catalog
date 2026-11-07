using System.Globalization;
using TrackStash.Catalog.Application;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api.Beatport;

/// <summary>Read-only Recording and Release enrichment through the existing authenticated Beatport broker.</summary>
public sealed partial class BeatportEnrichmentProvider(IBeatportBrowseService browse, IBeatportBrokerClient broker) : IEnrichmentProvider
{
    public EnrichmentProviderDescriptor Descriptor { get; } = new("beatport", "Beatport", [EnrichmentEntityKind.Release, EnrichmentEntityKind.Recording]);

    public async ValueTask<EnrichmentLookup> FindAsync(EnrichmentQuery query, CancellationToken ct = default)
    {
        if (query.EntityKind == EnrichmentEntityKind.Recording) return await FindRecordingAsync(query, ct);
        if (query.EntityKind != EnrichmentEntityKind.Release)
            throw new NotSupportedException("Beatport enrichment supports Recordings and Releases only.");
        if (query.LookupEditionId is not null) throw new ArgumentException("Beatport has no SonoVault edition selector.");
        var ids = query.LookupCandidateId is { } candidate ? [candidate]
            : query.ExternalReferences.Where(r => r.Source == "beatport").Select(r => r.ExternalId).Distinct().ToArray();
        if (ids.Length > 1) throw new ArgumentException("Choose one Beatport Release identity.");
        BeatportReleaseDetail? detail = null;
        IReadOnlyList<BeatportReleaseItem> releases;
        var warnings = new List<string>();
        if (ids.Length == 1)
        {
            if (!long.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ArgumentException("Beatport Release IDs must be positive integers.");
            detail = await browse.GetReleaseAsync(id, ct, requireEnrichmentEvidence: true);
            if (detail.Release.Id != id) throw new ApiRequestException(502, "beatport_invalid_response", "Beatport returned a different Release identity.");
            releases = [detail.Release];
            if (detail.TrackTotal > detail.Tracks.Count) warnings.Add("Only the first 100 provider tracks are returned; the tracklist is incomplete.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(query.Name)) throw new ArgumentException("A Release title or Beatport ID is required.");
            var page = await browse.SearchReleasesAsync(new(query.Name, null, null, "name", 1, 10), ct);
            releases = page.Items;
            if (page.Total > releases.Count) warnings.Add("Only the first ten search candidates are shown; no further pages were fetched.");
        }
        var retrieved = DateTimeOffset.UtcNow;
        var results = releases.Select(release =>
        {
            var conflicts = new List<string>();
            static bool Same(string? a, string? b) => a is not null && b is not null
                && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!Same(query.Name, release.Name)) conflicts.Add("Returned title differs from the catalog Release.");
            if (query.ArtistNames.Count > 0 && !release.Artists.Any(a => query.ArtistNames.Any(n => Same(n, a.Name))))
                conflicts.Add("Returned primary Artists differ from or are missing relative to the catalog.");
            var labels = query.LabelNames ?? (query.LabelName is { } label ? [label] : []);
            if (labels.Count > 0 && !labels.Any(l => Same(l, release.Label?.Name)))
                conflicts.Add("Returned Label differs from or is missing relative to the catalog.");
            if (query.CatalogNumber is { } number && release.CatalogNumber is { } returned && !Same(number, returned))
                conflicts.Add("Returned catalog number differs from the catalog Release.");
            var id = release.Id.ToString(CultureInfo.InvariantCulture);
            var credits = release.Artists.Select(a => new EnrichmentCredit(a.Name, "primary", new("beatport", a.Id.ToString(CultureInfo.InvariantCulture)))).ToArray();
            var tracks = detail?.Tracks.Select(t => new EnrichmentReleaseTrack(t.Id.ToString(CultureInfo.InvariantCulture),
                t.Name + (string.IsNullOrWhiteSpace(t.MixName) ? "" : $" ({t.MixName})"), t.Isrc, null, t.Number,
                t.Artists.Select(a => new EnrichmentCredit(a.Name, "primary", new("beatport", a.Id.ToString(CultureInfo.InvariantCulture))))
                    .Concat(t.Remixers.Select(a => new EnrichmentCredit(a.Name, "remixer", new("beatport", a.Id.ToString(CultureInfo.InvariantCulture))))).ToArray())).ToArray() ?? [];
            var metadata = new EnrichmentMetadata(Name: release.Name, LabelName: release.Label?.Name,
                CatalogNumber: release.CatalogNumber, Credits: credits, ExternalReferences: [new("beatport", id)],
                ReleaseDate: DateOnly.TryParseExact(release.PublishDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) ? date : null,
                ReleaseDetails: new(release.PublishDate, null, [], tracks));
            return new EnrichmentCandidate("beatport", id, null, null, EnrichmentEntityKind.Release,
                new(ids.Length == 1 ? EnrichmentMatchBasis.ExternalReference : EnrichmentMatchBasis.MetadataSearch,
                    "Beatport Release candidate; known metadata agreement does not establish canonical identity.", conflicts),
                metadata, new[] { EnrichmentField.Name, EnrichmentField.LabelName, EnrichmentField.CatalogNumber,
                    EnrichmentField.Credits, EnrichmentField.ExternalReferences, EnrichmentField.ReleaseDetails, EnrichmentField.ReleaseDate }
                    .Select(f => new EnrichmentFieldEvidence(f, "beatport", id, new Uri(release.BeatportUrl), retrieved, null)).ToArray());
        }).OrderBy(c => c.Match.Conflicts.Count).ToArray();
        warnings.Add("Lookup does not apply metadata or identities. Artwork is handled separately through reviewed artwork candidates. Search uses one broker call; details/review preparation use two (Release and tracks). No ownership is inferred.");
        return new("beatport", query, retrieved, results, warnings);
    }
}
