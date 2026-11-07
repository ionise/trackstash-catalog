namespace TrackStash.Catalog.Application;

/// <summary>Explicit lookup failures before a provider can be invoked.</summary>
public sealed class EnrichmentRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Loads a Recording snapshot and retrieves metadata candidates without applying them.</summary>
public interface IRecordingEnrichmentService
{
    ValueTask<EnrichmentLookup> FindAsync(string recordingId, string providerId, CancellationToken ct = default);
}

/// <summary>Authoritative catalog-to-provider boundary; callers cannot submit arbitrary query metadata.</summary>
public sealed class RecordingEnrichmentService(ITrackService tracks, IEnumerable<IEnrichmentProvider> providers)
    : IRecordingEnrichmentService
{
    public async ValueTask<EnrichmentLookup> FindAsync(string recordingId, string providerId, CancellationToken ct = default)
    {
        var recording = await tracks.GetRecordingAsync(recordingId, ct).ConfigureAwait(false)
            ?? throw new EnrichmentRequestException("recording_not_found", "Catalog Recording was not found.");
        var configured = providers.Where(p => p.Descriptor.Id == providerId).ToArray();
        if (configured.Length != 1 || !configured[0].Descriptor.SupportedEntityKinds.Contains(EnrichmentEntityKind.Recording))
            throw new EnrichmentRequestException("enrichment_provider_unavailable", "Exactly one Recording enrichment provider must be configured.");
        var artists = recording.Credits.Where(c => c.Role == "primary")
            .Select(c => c.CreditedName ?? c.ArtistName).OfType<string>().Distinct().ToArray();
        var query = new EnrichmentQuery(EnrichmentEntityKind.Recording, recording.Id, recording.Version,
            recording.Title, recording.MixName, artists, recording.Isrc, null, null, null, recording.DurationMs,
            recording.ExternalReferences.Select(r => new EnrichmentReference(r.Source, r.ExternalId)).ToArray());
        try { return await configured[0].FindAsync(query, ct).ConfigureAwait(false); }
        catch (ArgumentException)
        {
            throw new EnrichmentRequestException("enrichment_query_incomplete",
                "Lookup needs one valid SonoVault identity, an ISRC, or a title and primary Artist. Check the Recording's identities and credits.");
        }
    }
}
