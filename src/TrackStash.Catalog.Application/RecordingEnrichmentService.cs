namespace TrackStash.Catalog.Application;

/// <summary>Explicit lookup failures before a provider can be invoked.</summary>
public sealed class EnrichmentRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Loads a Recording snapshot and retrieves metadata candidates without applying them.</summary>
public interface IRecordingEnrichmentService
{
    ValueTask<EnrichmentLookup> FindAsync(string recordingId, string providerId, CancellationToken ct = default, string? candidateId = null);
}

/// <summary>Authoritative catalog-to-provider boundary; callers cannot submit arbitrary query metadata.</summary>
public sealed class RecordingEnrichmentService(ITrackService tracks, IEnumerable<IEnrichmentProvider> providers)
    : IRecordingEnrichmentService
{
    public async ValueTask<EnrichmentLookup> FindAsync(string recordingId, string providerId, CancellationToken ct = default, string? candidateId = null)
    {
        var recording = await tracks.GetRecordingAsync(recordingId, ct).ConfigureAwait(false)
            ?? throw new EnrichmentRequestException("recording_not_found", "Catalog Recording was not found.");
        var artists = recording.Credits.Where(c => c.Role == "primary")
            .Select(c => c.CreditedName ?? c.ArtistName).OfType<string>().Distinct().ToArray();
        var query = new EnrichmentQuery(EnrichmentEntityKind.Recording, recording.Id, recording.Version,
            recording.Title, recording.MixName, artists, recording.Isrc, null, null, null, recording.DurationMs,
            recording.ExternalReferences.Select(r => new EnrichmentReference(r.Source, r.ExternalId)).ToArray(),
            LookupCandidateId: candidateId, CatalogMusicalMetadata: recording.MusicalMetadata);
        return await EnrichmentLookupRunner.FindAsync(query, providerId, providers, ct).ConfigureAwait(false);
    }
}

internal static class EnrichmentLookupRunner
{
    internal static async ValueTask<EnrichmentLookup> FindAsync(EnrichmentQuery query, string providerId,
        IEnumerable<IEnrichmentProvider> providers, CancellationToken ct)
    {
        var configured = providers.Where(p => p.Descriptor.Id == providerId).ToArray();
        if (configured.Length != 1 || !configured[0].Descriptor.SupportedEntityKinds.Contains(query.EntityKind))
            throw new EnrichmentRequestException("enrichment_provider_unavailable", "Exactly one compatible enrichment provider must be configured.");
        try { return await configured[0].FindAsync(query, ct).ConfigureAwait(false); }
        catch (ArgumentException)
        {
            throw new EnrichmentRequestException("enrichment_query_incomplete",
                query.EntityKind == EnrichmentEntityKind.Recording
                    ? "Lookup needs one valid provider identity, an ISRC, or suitable title/Artist metadata. Check the Recording's identities and credits."
                    : query.EntityKind == EnrichmentEntityKind.Release
                        ? "Lookup needs one valid provider Release identity or a title. Candidate and edition IDs must be positive integers."
                        : "Lookup needs one valid SonoVault identity or a name. Check the entity's identities.");
        }
    }
}
