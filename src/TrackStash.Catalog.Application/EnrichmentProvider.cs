namespace TrackStash.Catalog.Application;

/// <summary>Canonical entity types a metadata provider can look up.</summary>
public enum EnrichmentEntityKind { Recording, Release, Artist, Label }

/// <summary>Describes a server-registered provider and its implemented lookup capabilities.</summary>
public sealed record EnrichmentProviderDescriptor(
    string Id, string DisplayName, IReadOnlyList<EnrichmentEntityKind> SupportedEntityKinds);

/// <summary>An identifier in its source namespace; it is evidence, not an accepted catalog link.</summary>
public sealed record EnrichmentReference(string Source, string ExternalId);

/// <summary>Public catalog metadata supplied to a provider for identity lookup.</summary>
/// <remarks>
/// The caller loads and authorizes the entity. CatalogVersion is an opaque snapshot token
/// for later review, not permission for the provider to write. Do not include private notes,
/// credentials, filesystem paths or audio in this request.
/// </remarks>
public sealed record EnrichmentQuery(
    EnrichmentEntityKind EntityKind,
    string CatalogId,
    string CatalogVersion,
    string? Name,
    string? MixName,
    IReadOnlyList<string> ArtistNames,
    string? Isrc,
    string? CatalogNumber,
    string? LabelName,
    DateOnly? ReleaseDate,
    long? DurationMs,
    IReadOnlyList<EnrichmentReference> ExternalReferences,
    IReadOnlyList<string>? LabelNames = null,
    string? LookupCandidateId = null,
    string? LookupEditionId = null,
    string? CatalogPublishDate = null,
    TrackStash.Core.Storage.CatalogMusicalMetadata? CatalogMusicalMetadata = null);

/// <summary>How a provider found a candidate; none of these values certifies canonical identity.</summary>
public enum EnrichmentMatchBasis { ExternalReference, Isrc, MetadataSearch }

/// <summary>Explicit reasons a candidate was returned, including conflicting identity evidence.</summary>
public sealed record EnrichmentMatchEvidence(
    EnrichmentMatchBasis Basis, string Explanation, IReadOnlyList<string> Conflicts);

/// <summary>A proposed credit with the provider's role and optional source identity, not a canonical Artist ID.</summary>
public sealed record EnrichmentCredit(string Name, string Role, EnrichmentReference? Reference);

/// <summary>A musical key in an explicit notation, avoiding implicit conversion between key systems.</summary>
public sealed record EnrichmentMusicalKey(string Value, string Notation);

/// <summary>A provider edition to inspect, not an accepted canonical release or source link.</summary>
public sealed record EnrichmentReleaseEdition(
    string Id, string Source, string ExternalId, string? Title, string? Format,
    string? ReleaseDate, string? DatePrecision, string? CatalogNumber, string? Barcode,
    string? LabelName, int? TrackCount);

/// <summary>Provider tracklist identity and numbering, without ownership or canonical Recording assertions.</summary>
public sealed record EnrichmentReleaseTrack(
    string Id, string Title, string? Isrc, int? DiscNumber, int? TrackNumber,
    IReadOnlyList<EnrichmentCredit> Credits);

/// <summary>Release-group date and edition/tracklist evidence; an unselected tracklist may use consensus numbering.</summary>
public sealed record EnrichmentReleaseDetails(
    string? ReportedReleaseDate, string? SelectedEditionId,
    IReadOnlyList<EnrichmentReleaseEdition> Editions, IReadOnlyList<EnrichmentReleaseTrack> Tracks,
    IReadOnlyList<string>? MusicBrainzReleaseIds = null, IReadOnlyList<string>? MusicBrainzReleaseGroupIds = null);

/// <summary>Optional proposed metadata; null means unavailable, never a request to clear a catalog value.</summary>
/// <remarks>
/// Empty reference/credit lists mean no proposals, not replacement of existing lists.
/// BPM and key are metadata values, not audio embeddings. Release dates apply to the
/// specific release/edition identified by the candidate, not every appearance of a Recording.
/// </remarks>
public sealed record EnrichmentMetadata(
    string? Name = null,
    string? MixName = null,
    string? Isrc = null,
    string? CatalogNumber = null,
    DateOnly? ReleaseDate = null,
    long? DurationMs = null,
    decimal? Bpm = null,
    EnrichmentMusicalKey? MusicalKey = null,
    string? Genre = null,
    string? LabelName = null,
    IReadOnlyList<EnrichmentReference>? ExternalReferences = null,
    IReadOnlyList<EnrichmentCredit>? Credits = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Subgenres = null,
    string? Country = null,
    int? FormationYear = null,
    DateOnly? FormationDate = null,
    IReadOnlyDictionary<string, string>? SocialLinks = null,
    EnrichmentReleaseDetails? ReleaseDetails = null);

/// <summary>Proposed fields for which an adapter can expose source evidence.</summary>
public enum EnrichmentField
{
    Name, MixName, Isrc, CatalogNumber, ReleaseDate, DurationMs, Bpm,
    MusicalKey, Genre, LabelName, ExternalReferences, Credits, Subgenre,
    Country, FormationYear, FormationDate, SocialLinks, ReleaseDetails
}

/// <summary>Evidence for one proposed field, separating the API provider from an attributed upstream source.</summary>
/// <remarks>
/// Source is the origin actually exposed by the response, not an assumed Discogs or
/// MusicBrainz attribution. A null source URL means no public evidence URL was supplied.
/// RetrievedAt records when the response was obtained, including for cached responses.
/// </remarks>
public sealed record EnrichmentFieldEvidence(
    EnrichmentField Field, string Source, string? SourceRecordId,
    Uri? SourceUrl, DateTimeOffset RetrievedAt, string? Attribution);

/// <summary>A provider-scoped candidate and optional edition identity, ready for comparison but not application.</summary>
/// <remarks>
/// CandidateId and EditionId are opaque provider IDs, never canonical IDs.
/// Revision is optional because not all sources supply version tokens. Missing revision
/// does not permit unversioned apply: a future review must freeze its own proposal.
/// </remarks>
public sealed record EnrichmentCandidate(
    string ProviderId,
    string CandidateId,
    string? EditionId,
    string? Revision,
    EnrichmentEntityKind EntityKind,
    EnrichmentMatchEvidence Match,
    EnrichmentMetadata Metadata,
    IReadOnlyList<EnrichmentFieldEvidence> Evidence);

/// <summary>Successful lookup results for the input snapshot, including explicit coverage warnings.</summary>
/// <remarks>
/// An empty candidate list means a successful lookup found no match. Configuration,
/// authentication, quota, network and unsupported-capability failures must throw;
/// they must not be represented as an empty success. Warnings describe limitations
/// such as incomplete or truncated results, not hidden request failures.
/// </remarks>
public sealed record EnrichmentLookup(
    string ProviderId, EnrichmentQuery Query, DateTimeOffset RetrievedAt,
    IReadOnlyList<EnrichmentCandidate> Candidates, IReadOnlyList<string> Warnings);

/// <summary>Server-configured, read-only metadata adapter; catalog review/apply owns all canonical writes.</summary>
/// <remarks>
/// Register implementations using the host's dependency injection, for example
/// AddScoped&lt;IEnrichmentProvider, SonoVaultEnrichmentProvider&gt; when that adapter exists.
/// Provider IDs must be stable and unique within the host. SupportedEntityKinds must
/// only advertise implemented capabilities. Adapters must use server-held credentials,
/// approved endpoints, bounded requests and cancellation; caller cancellation propagates
/// as OperationCanceledException. Unsupported entity kinds throw NotSupportedException.
/// Returned evidence must not contain credentials or private request data.
/// </remarks>
public interface IEnrichmentProvider
{
    EnrichmentProviderDescriptor Descriptor { get; }
    ValueTask<EnrichmentLookup> FindAsync(EnrichmentQuery query, CancellationToken ct = default);
}
