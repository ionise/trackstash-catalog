using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public sealed class EnrichmentApplyException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>A selectable field from a frozen provider response; null is never deletion.</summary>
public sealed record EnrichmentReviewField(string Field, string Label, JsonElement Current, JsonElement Proposed);
/// <summary>Immutable review, original catalog state and provider evidence, with an optional saved outcome.</summary>
public sealed record EnrichmentReview(string Id, string Version, EnrichmentEntityKind EntityKind, string EntityId,
    DateTimeOffset CreatedAt, EnrichmentCandidate Candidate, IReadOnlyList<EnrichmentReviewField> Fields,
    IReadOnlyList<string> Warnings, EnrichmentApplyOutcome? Outcome = null);
/// <summary>One persisted, replayable reviewed update.</summary>
public sealed record EnrichmentApplyOutcome(string ReviewId, string EntityId, DateTimeOffset AppliedAt, IReadOnlyList<string> Fields);

public interface IEnrichmentApplyService
{
    ValueTask<EnrichmentReview> PrepareAsync(EnrichmentEntityKind kind, string id, string candidateId,
        string? editionId, string catalogVersion, CancellationToken ct = default, string providerId = "sonovault");
    ValueTask<EnrichmentReview> GetAsync(string reviewId, CancellationToken ct = default);
    ValueTask<EnrichmentApplyOutcome> ApplyAsync(string reviewId, string version, IReadOnlyList<string> fields,
        string? preferredGenre, string reason, bool confirmed, CancellationToken ct = default);
}

public sealed class EnrichmentApplyService(IStorageProvider storage, IRecordingEnrichmentService recordings,
    INamedEntityEnrichmentService named, TimeProvider clock) : IEnrichmentApplyService
{
    private sealed record Snapshot(Recording? Recording, Release? Release, CatalogMusicalMetadata Musical,
        IReadOnlyList<CatalogCredit> Credits, IReadOnlyList<ReleasePlacement> Placements)
    {
        public string Version => Recording?.Version ?? Release!.Version;
        public CanonicalEntity Entity => (CanonicalEntity?)Recording ?? Release!;
    }
    private sealed record Payload(Snapshot Snapshot, EnrichmentReview Review);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(value)))).ToLowerInvariant();
    private static bool SameProviderId(string left, string right) =>
        long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var first) && first > 0 &&
        long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var second) && first == second;
    private static CatalogEntityKind Kind(EnrichmentEntityKind kind) => kind switch
    {
        EnrichmentEntityKind.Recording => CatalogEntityKind.Recording, EnrichmentEntityKind.Release => CatalogEntityKind.Release,
        _ => throw new EnrichmentApplyException(400, "enrichment_kind_unsupported", "Reviewed apply supports Recordings and Releases only."),
    };
    private static IEnrichmentRepository Repo(IUnitOfWork unit) => unit.Enrichment
        ?? throw new EnrichmentApplyException(503, "enrichment_storage_unavailable", "Storage does not support reviewed enrichment.");
    private async ValueTask Ready(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 13)
            throw new EnrichmentApplyException(409, "enrichment_schema_required",
                "Reviewed enrichment requires schema 13. A live migration must be approved separately; lookup remains available.");
    }
    private static async ValueTask<Snapshot> Read(IUnitOfWork unit, CatalogEntityKind kind, string id, CancellationToken ct)
    {
        var recording = kind == CatalogEntityKind.Recording ? await unit.Recordings.GetByIdAsync(id, ct).ConfigureAwait(false) : null;
        var release = kind == CatalogEntityKind.Release ? await unit.Releases.GetByIdAsync(id, ct).ConfigureAwait(false) : null;
        if (recording is null && release is null)
            throw new EnrichmentApplyException(404, "enrichment_entity_not_found", "Catalog entity was not found.");
        return new(recording, release, await Repo(unit).GetMetadataAsync(kind, id, ct).ConfigureAwait(false),
            await unit.CatalogFoundations!.GetCreditsAsync(kind, id, ct).ConfigureAwait(false),
            release is null ? [] : await unit.CatalogFoundations.GetPlacementsAsync(id, ct).ConfigureAwait(false));
    }
    public async ValueTask<EnrichmentReview> PrepareAsync(EnrichmentEntityKind kind, string id, string candidateId,
        string? editionId, string catalogVersion, CancellationToken ct = default, string providerId = "sonovault")
    {
        var storageKind = Kind(kind);
        if (!long.TryParse(candidateId, NumberStyles.None, CultureInfo.InvariantCulture, out var candidateNumber) || candidateNumber <= 0 ||
            providerId is not ("sonovault" or "beatport") ||
            (kind == EnrichmentEntityKind.Recording || providerId == "beatport") && editionId is not null)
            throw new EnrichmentApplyException(400, "enrichment_invalid_review", "Choose a valid supported provider candidate; editions are available only for SonoVault Releases.");
        await Ready(ct).ConfigureAwait(false);
        Snapshot before;
        await using (var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false))
            before = await Read(unit, storageKind, id, ct).ConfigureAwait(false);
        if (before.Version != catalogVersion) throw Stale();
        var lookup = kind == EnrichmentEntityKind.Recording
            ? await recordings.FindAsync(id, providerId, ct, candidateId).ConfigureAwait(false)
            : await named.FindAsync(kind, id, providerId, ct, candidateId, editionId).ConfigureAwait(false);
        var candidate = lookup.Candidates.SingleOrDefault(c => c.CandidateId == candidateNumber.ToString(CultureInfo.InvariantCulture)
            && c.EntityKind == kind && c.ProviderId == providerId)
            ?? throw new EnrichmentApplyException(409, "enrichment_candidate_unavailable", "The selected provider candidate is no longer available. Look up again.");
        if (lookup.Query.CatalogVersion != before.Version) throw Stale();
        var fields = Fields(before, candidate);
        var warnings = lookup.Warnings.Concat(candidate.Match.Conflicts).Concat(new[]
        {
            "Only selected fields will be applied. Genre/subgenre selections replace that field's list; no values are cleared automatically.",
            "Credits, Label links and provider tracklists remain evidence: they require canonical identity mapping.",
            "Accepting the provider identity is a separate decision; other provider and MusicBrainz edition/group identities are not copied.",
            providerId == "sonovault"
                ? "A Recording update affects every Release that uses the same Recording. SonoVault dates/barcodes require an explicitly selected edition; group dates are not applied."
                : "A Recording update affects every Release that uses the same Recording. Beatport dates describe the selected provider Release, not its purchase date or every Recording appearance.",
        }).Concat(UnavailableFields(candidate, fields)).ToArray();
        var review = new EnrichmentReview(Guid.NewGuid().ToString("N"), "", kind, id, clock.GetUtcNow(), candidate, fields, warnings);
        review = review with { Version = Hash(new Payload(before, review)) };
        await using var save = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        if (Hash(await Read(save, storageKind, id, ct).ConfigureAwait(false)) != Hash(before)) throw Stale();
        await Repo(save).SaveReviewAsync(new(review.Id, storageKind, id, review.Version, Json(new Payload(before, review)), review.CreatedAt), ct).ConfigureAwait(false);
        await save.CommitAsync(ct).ConfigureAwait(false);
        return review;
    }
    private static IReadOnlyList<EnrichmentReviewField> Fields(Snapshot before, EnrichmentCandidate candidate)
    {
        var fields = new List<EnrichmentReviewField>();
        void Add(string field, string label, object? current, object? proposed)
        {
            if (proposed is not null && (proposed is not string text || !string.IsNullOrWhiteSpace(text)))
                fields.Add(new(field, label, JsonSerializer.SerializeToElement(current), JsonSerializer.SerializeToElement(proposed)));
        }
        var m = candidate.Metadata;
        var musical = before.Musical;
        Add("Name", "Title", before.Recording?.Title ?? before.Release?.Title ?? before.Entity.Name, m.Name);
        if (before.Recording is { } recording)
        {
            Add("MixName", "Mix / version", recording.MixName, m.MixName);
            if (m.Isrc is { } isrc && Regex.IsMatch(isrc.Trim().ToUpperInvariant(), "^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$"))
                Add("Isrc", "ISRC", recording.Isrc, isrc.Trim().ToUpperInvariant());
            if (m.Bpm is > 0 and <= 1000) Add("Bpm", "BPM", musical.Bpm, m.Bpm);
            if (m.MusicalKey is { } key) Add("MusicalKey", "Musical key", musical.KeyValue is null ? null : new EnrichmentMusicalKey(musical.KeyValue, musical.KeyNotation!), key);
        }
        else if (m.ReleaseDetails?.SelectedEditionId is { } selectedId)
        {
            var edition = m.ReleaseDetails.Editions.Single(e => e.Id == selectedId);
            Add("CatalogNumber", "Selected edition catalog number", before.Release!.CatalogNumber, edition.CatalogNumber);
            Add("Upc", "Selected edition barcode", before.Release.Upc, edition.Barcode);
            if (edition.DatePrecision is "day" or "date" && DateOnly.TryParseExact(edition.ReleaseDate, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                Add("ReleaseDate", "Selected edition release date", before.Release.PublishDate, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else if (candidate.ProviderId == "beatport")
        {
            Add("CatalogNumber", "Beatport catalog number", before.Release!.CatalogNumber, m.CatalogNumber);
            Add("ReleaseDate", "Beatport release date", before.Release.PublishDate,
                m.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        var genres = m.Genres ?? (m.Genre is { } genre ? [genre] : []);
        var subgenres = m.Subgenres ?? [];
        if (genres.Count > 0) Add("Genre", "Genres (replace list)", musical.Genres, genres);
        if (subgenres.Count > 0) Add("Subgenre", "Subgenres (replace list)", musical.Subgenres, subgenres);
        var choices = genres.Concat(subgenres).Distinct(StringComparer.Ordinal).ToArray();
        if (choices.Length > 0) Add("PreferredGenre", "Preferred classification (choose one)", musical.PreferredGenre, choices);
        var providerName = candidate.ProviderId == "beatport" ? "Beatport" : "SonoVault";
        Add($"{providerName}Identity", $"Accept {providerName} identity",
            before.Entity.ExternalReferences.Where(r => r.Source == candidate.ProviderId).Select(r => r.ExternalId).ToArray(), candidate.CandidateId);
        return fields;
    }
    private static IEnumerable<string> UnavailableFields(EnrichmentCandidate candidate, IReadOnlyList<EnrichmentReviewField> fields)
    {
        if (candidate.Metadata.Isrc is not null && !fields.Any(f => f.Field == "Isrc"))
            yield return "The provider ISRC is not a valid 12-character ISRC and is not selectable.";
        if (candidate.Metadata.Bpm is not null && !fields.Any(f => f.Field == "Bpm"))
            yield return "The provider tempo is outside the writable Recording BPM range (greater than 0, up to 1000) or does not belong to this entity.";
        if (candidate.EntityKind == EnrichmentEntityKind.Release &&
            candidate.Metadata.ReleaseDetails?.SelectedEditionId is not null && !fields.Any(f => f.Field == "ReleaseDate"))
            yield return "The selected edition has no validated day-precision date; its raw date remains evidence only.";
        if (candidate.EntityKind == EnrichmentEntityKind.Release && candidate.ProviderId == "beatport" &&
            candidate.Metadata.ReleaseDetails?.ReportedReleaseDate is not null && !fields.Any(f => f.Field == "ReleaseDate"))
            yield return "The Beatport Release has no validated yyyy-MM-dd date; its raw date remains evidence only.";
    }
    private static EnrichmentApplyException Stale() => new(412, "enrichment_state_changed", "The catalog changed since lookup/review. Prepare a fresh review; nothing was applied.");
    private static async ValueTask<Payload> Load(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await Repo(unit).GetReviewAsync(id, ct).ConfigureAwait(false)
            ?? throw new EnrichmentApplyException(404, "enrichment_review_not_found", "Enrichment review was not found.");
        return JsonSerializer.Deserialize<Payload>(stored.PayloadJson)
            ?? throw new InvalidDataException("Stored enrichment review is invalid.");
    }
    public async ValueTask<EnrichmentReview> GetAsync(string reviewId, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var payload = await Load(unit, reviewId, ct).ConfigureAwait(false);
        var audit = await Repo(unit).GetAuditAsync(reviewId, ct).ConfigureAwait(false);
        return payload.Review with { Outcome = audit is null ? null : Outcome(audit, payload.Review.EntityId) };
    }
    private static EnrichmentApplyOutcome Outcome(EnrichmentApplyAudit audit, string id) =>
        new(audit.ReviewId, id, audit.AppliedAt, JsonSerializer.Deserialize<string[]>(audit.FieldsJson)!);

    public async ValueTask<EnrichmentApplyOutcome> ApplyAsync(string reviewId, string version, IReadOnlyList<string> fields,
        string? preferredGenre, string reason, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000 || fields.Count is 0 or > 20 ||
            fields.Any(string.IsNullOrWhiteSpace) || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count ||
            preferredGenre is not null && !fields.Contains("PreferredGenre"))
            throw new EnrichmentApplyException(400, "enrichment_invalid_apply", "Select fields, give a review reason and explicitly confirm; a preferred classification needs its checkbox.");
        await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var payload = await Load(unit, reviewId, ct).ConfigureAwait(false);
        var review = payload.Review;
        if (version != review.Version) throw Stale();
        if (fields.Any(f => !review.Fields.Any(row => row.Field == f)))
            throw new EnrichmentApplyException(400, "enrichment_field_unavailable", "A selected field is not available in this frozen review.");
        var selected = fields.Order(StringComparer.Ordinal).ToArray();
        var intent = Json(new { Fields = selected, PreferredGenre = preferredGenre });
        var existing = await Repo(unit).GetAuditAsync(reviewId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // The saved field intent includes the chosen classification, not just its checkbox.
            var saved = JsonSerializer.Deserialize<AppliedSnapshot>(existing.AfterJson)!;
            if (saved.Intent != intent || existing.Reason != reason.Trim())
                throw new EnrichmentApplyException(409, "enrichment_already_applied", "This review was applied with different selections or reason. Prepare a new review.");
            return Outcome(existing, review.EntityId);
        }
        if (clock.GetUtcNow() - review.CreatedAt >= TimeSpan.FromHours(24))
            throw new EnrichmentApplyException(410, "enrichment_review_expired", "This review expired. Prepare a fresh review.");
        var kind = Kind(review.EntityKind);
        var before = await Read(unit, kind, review.EntityId, ct).ConfigureAwait(false);
        if (Hash(before) != Hash(payload.Snapshot)) throw Stale();
        var choices = review.Fields.ToDictionary(f => f.Field, StringComparer.Ordinal);
        bool Has(string field) => selected.Contains(field);
        string? Text(string field, string? current) => Has(field) ? choices[field].Proposed.GetString() : current;
        var refs = before.Entity.ExternalReferences;
        var providerId = review.Candidate.ProviderId;
        var providerName = providerId == "beatport" ? "Beatport" : "SonoVault";
        var identityField = $"{providerName}Identity";
        if (Has(identityField))
        {
            var candidateId = review.Candidate.CandidateId;
            if (refs.Any(r => r.Source == providerId && !SameProviderId(r.ExternalId, candidateId)) ||
                (await unit.CatalogFoundations!.ListReferenceOwnersAsync(kind, providerId, ct).ConfigureAwait(false))
                    .Any(owner => SameProviderId(owner.ExternalId, candidateId) && owner.OwnerId != review.EntityId))
                throw new EnrichmentApplyException(409, "enrichment_identity_conflict", $"The {providerName} identity is already linked elsewhere or conflicts with this entity's accepted identity.");
            if (!refs.Any(r => r.Source == providerId && SameProviderId(r.ExternalId, candidateId)))
                refs = refs.Append(new EntityReference { Source = providerId, ExternalId = candidateId, IsPrimary = false,
                    LastSeenUtc = review.CreatedAt, PayloadJson = Json(new { ReviewId = reviewId, review.Candidate.EditionId }) }).ToArray();
        }
        var title = Text("Name", before.Recording?.Title ?? before.Release?.Title ?? before.Entity.Name);
        if (Has("Name") && string.IsNullOrWhiteSpace(title)) throw new EnrichmentApplyException(400, "enrichment_invalid_title", "Title must not be empty.");
        if (before.Recording is { } recording && (Has(identityField) || selected.Any(f => f is "Name" or "MixName" or "Isrc")))
        {
            var isrc = Text("Isrc", recording.Isrc);
            if (Has("Isrc") && isrc is not null &&
                await unit.Recordings.GetByIsrcAsync(isrc, ct).ConfigureAwait(false) is { } owner && owner.Id != recording.Id)
                throw new EnrichmentApplyException(409, "enrichment_isrc_conflict", "Another Recording already has this ISRC. Review identities instead of joining recordings through enrichment.");
            await Repo(unit).UpdateEntityAsync(recording with { Title = Has("Name") ? title : recording.Title,
                Name = Has("Name") ? title : recording.Name,
                NormalizedName = Has("Name") ? EntityNameNormalizer.NormalizeStrict(title!) : recording.NormalizedName, MixName = Text("MixName", recording.MixName),
                Isrc = isrc, ExternalReferences = refs }, recording.Version, ct).ConfigureAwait(false);
        }
        if (before.Release is { } release && (Has(identityField) || selected.Any(f => f is "Name" or "CatalogNumber" or "Upc" or "ReleaseDate")))
            await Repo(unit).UpdateEntityAsync(release with { Title = Has("Name") ? title : release.Title,
                Name = Has("Name") ? title : release.Name,
                NormalizedName = Has("Name") ? EntityNameNormalizer.NormalizeStrict(title!) : release.NormalizedName, CatalogNumber = Text("CatalogNumber", release.CatalogNumber),
                Upc = Text("Upc", release.Upc), PublishDate = Text("ReleaseDate", release.PublishDate), ExternalReferences = refs },
                release.Version, ct).ConfigureAwait(false);
        var musical = before.Musical;
        if (selected.Any(f => f is "Bpm" or "MusicalKey" or "Genre" or "Subgenre" or "PreferredGenre"))
        {
            var key = Has("MusicalKey") ? choices["MusicalKey"].Proposed.Deserialize<EnrichmentMusicalKey>()! : null;
            musical = musical with
            {
                Bpm = Has("Bpm") ? choices["Bpm"].Proposed.GetDecimal() : musical.Bpm,
                KeyValue = key?.Value ?? musical.KeyValue, KeyNotation = key?.Notation ?? musical.KeyNotation,
                Genres = Has("Genre") ? choices["Genre"].Proposed.Deserialize<string[]>()! : musical.Genres,
                Subgenres = Has("Subgenre") ? choices["Subgenre"].Proposed.Deserialize<string[]>()! : musical.Subgenres,
                PreferredGenre = Has("PreferredGenre") ? preferredGenre : musical.PreferredGenre,
            };
            if (Has("PreferredGenre") && (preferredGenre is null || !choices["PreferredGenre"].Proposed.Deserialize<string[]>()!.Contains(preferredGenre)) ||
                musical.PreferredGenre is not null && !(musical.Genres ?? []).Concat(musical.Subgenres ?? []).Contains(musical.PreferredGenre))
                throw new EnrichmentApplyException(400, "enrichment_classification_invalid", "Choose a preferred classification retained in the selected genre/subgenre lists. Existing preferred values are not cleared silently.");
            await Repo(unit).SaveMetadataAsync(kind, review.EntityId, musical, ct).ConfigureAwait(false);
        }
        var after = await Read(unit, kind, review.EntityId, ct).ConfigureAwait(false);
        var audit = new EnrichmentApplyAudit(reviewId, Json(selected), reason.Trim(), Json(before),
            Json(new AppliedSnapshot(after, intent)), clock.GetUtcNow());
        await Repo(unit).SaveAuditAsync(audit, ct).ConfigureAwait(false);
        await unit.CommitAsync(ct).ConfigureAwait(false);
        return Outcome(audit, review.EntityId);
    }
    private sealed record AppliedSnapshot(Snapshot Snapshot, string Intent);
}
