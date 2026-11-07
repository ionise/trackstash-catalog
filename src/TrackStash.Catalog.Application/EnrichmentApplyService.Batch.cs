using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

/// <summary>A provider track and its conservative match decision; only confident, unique matches have a writable review.</summary>
public sealed record EnrichmentBatchTrack(string CandidateId, string Title, string Status,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> RecordingIds, EnrichmentReview? Review);
/// <summary>A frozen Release-to-Recording batch. No Release metadata or ownership is applied.</summary>
public sealed record EnrichmentBatchReview(string Id, string Version, string ReleaseId, DateTimeOffset CreatedAt,
    EnrichmentCandidate Candidate, IReadOnlyList<EnrichmentBatchTrack> Tracks, IReadOnlyList<string> Warnings,
    EnrichmentBatchOutcome? Outcome = null);
/// <summary>Selected field names and optional classification for one frozen Recording review.</summary>
public sealed record EnrichmentBatchSelection(string ReviewId, IReadOnlyList<string> Fields, string? PreferredGenre);
/// <summary>Permanent atomic batch outcome with individual Recording audit outcomes.</summary>
public sealed record EnrichmentBatchOutcome(string ReviewId, string ReleaseId, DateTimeOffset AppliedAt,
    IReadOnlyList<EnrichmentApplyOutcome> Recordings);

public sealed partial class EnrichmentApplyService
{
    private sealed record BatchPayload(string Type, Snapshot Release, IReadOnlyList<Snapshot> Recordings, EnrichmentBatchReview Review);
    private sealed record BatchApplied(string Intent, EnrichmentBatchOutcome Outcome);
    private static bool SameText(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool ValidIsrc(string? value) => value is not null &&
        Regex.IsMatch(value.Trim().ToUpperInvariant(), "^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$");

    public async ValueTask<EnrichmentBatchReview> PrepareBatchAsync(string releaseId, string candidateId,
        string catalogVersion, CancellationToken ct = default)
    {
        if (!long.TryParse(candidateId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new EnrichmentApplyException(400, "enrichment_invalid_review", "Choose a valid Beatport Release candidate.");
        await Ready(ct).ConfigureAwait(false);
        Snapshot release;
        var snapshots = new List<Snapshot>();
        await using (var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false))
        {
            release = await Read(unit, CatalogEntityKind.Release, releaseId, ct).ConfigureAwait(false);
            if (release.Version != catalogVersion) throw Stale();
            if (release.Placements.Count > 100)
                throw new EnrichmentApplyException(409, "enrichment_batch_limit", "This first batch workflow supports Releases with at most 100 placements.");
            foreach (var id in release.Placements.Select(p => p.RecordingId).Distinct(StringComparer.Ordinal))
                snapshots.Add(await Read(unit, CatalogEntityKind.Recording, id, ct).ConfigureAwait(false));
        }
        var lookup = await named.FindAsync(EnrichmentEntityKind.Release, releaseId, "beatport", ct, candidateId).ConfigureAwait(false);
        if (lookup.Query.CatalogVersion != release.Version) throw Stale();
        var candidate = lookup.Candidates.SingleOrDefault(c => c.ProviderId == "beatport" &&
            c.EntityKind == EnrichmentEntityKind.Release && c.CandidateId == number.ToString(CultureInfo.InvariantCulture))
            ?? throw new EnrichmentApplyException(409, "enrichment_candidate_unavailable", "The selected Beatport Release is unavailable.");
        if (release.Entity.ExternalReferences.Any(r => r.Source == "beatport" && !SameProviderId(r.ExternalId, candidate.CandidateId)))
            throw new EnrichmentApplyException(409, "enrichment_identity_conflict", "The selected Beatport Release conflicts with this Release's accepted identity.");
        var tracks = candidate.Metadata.ReleaseDetails?.Tracks
            ?? throw new EnrichmentApplyException(502, "enrichment_track_evidence_missing", "The provider returned no tracklist evidence.");
        if (tracks.Count > 100 || tracks.Any(t => t.RecordingCandidate is not { ProviderId: "beatport", EntityKind: EnrichmentEntityKind.Recording } source ||
            !SameProviderId(t.Id, source.CandidateId)))
            throw new EnrichmentApplyException(502, "enrichment_track_evidence_missing", "The provider returned incomplete track metadata evidence.");
        var batchId = Guid.NewGuid().ToString("N");
        var rows = new List<EnrichmentBatchTrack>();
        var childPayloads = new Dictionary<string, Payload>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            var source = track.RecordingCandidate!;
            var metadata = source.Metadata;
            var direct = snapshots.Where(s => s.Entity.ExternalReferences.Any(r => r.Source == "beatport" &&
                SameProviderId(r.ExternalId, source.CandidateId))).ToArray();
            var possible = direct.Length > 0 ? direct : snapshots.Where(s =>
                ValidIsrc(metadata.Isrc) && SameText(s.Recording!.Isrc, metadata.Isrc) ||
                SameText(s.Recording!.Title ?? s.Entity.Name, metadata.Name)).ToArray();
            if (possible.Length == 0)
            {
                rows.Add(new(source.CandidateId, track.Title, "Unmatched", ["No accepted track ID, exact ISRC or title match within this Release."], [], null));
                continue;
            }
            var reasons = new List<string>();
            var confident = new List<Snapshot>();
            foreach (var snapshot in possible)
            {
                var recording = snapshot.Recording!;
                if (recording.ExternalReferences.Any(r => r.Source == "beatport" && !SameProviderId(r.ExternalId, source.CandidateId)))
                {
                    reasons.Add($"{recording.Id}: conflicting accepted Beatport track identity.");
                    continue;
                }
                if (!SameText(recording.Title ?? recording.Name, metadata.Name) ||
                    !SameText(recording.MixName, metadata.MixName))
                {
                    reasons.Add($"{recording.Id}: title or mix/version differs or is missing; no version inference.");
                    continue;
                }
                if (recording.Isrc is not null && metadata.Isrc is not null && !SameText(recording.Isrc, metadata.Isrc))
                {
                    reasons.Add($"{recording.Id}: ISRC conflicts.");
                    continue;
                }
                var accepted = direct.Contains(snapshot);
                var exactIsrc = ValidIsrc(metadata.Isrc) && SameText(recording.Isrc, metadata.Isrc);
                var catalogArtists = snapshot.Credits.Where(c => c.Role == "primary").ToArray();
                var providerArtists = (metadata.Credits ?? []).Where(c => c.Role == "primary").ToArray();
                var artistMatches = await ArtistsAgree(catalogArtists, providerArtists, ct).ConfigureAwait(false);
                if (accepted && catalogArtists.Length > 0 && providerArtists.Length > 0 && !artistMatches)
                {
                    reasons.Add($"{recording.Id}: primary Artist evidence conflicts despite the accepted track ID.");
                    continue;
                }
                if (!accepted && (!exactIsrc || !artistMatches))
                {
                    reasons.Add($"{recording.Id}: needs accepted Beatport track ID or valid ISRC plus complete primary Artist agreement.");
                    continue;
                }
                confident.Add(snapshot);
                reasons.Add($"{recording.Id}: {(accepted ? "accepted Beatport track ID" : "valid ISRC and primary Artists")}, exact title and mix/version agreement.");
            }
            if (possible.Length != 1 || confident.Count != 1 || tracks.Count(t => t.Id == track.Id) != 1)
            {
                rows.Add(new(source.CandidateId, track.Title, "Ambiguous", reasons, possible.Select(s => s.Entity.Id).ToArray(), null));
                continue;
            }
            var matched = confident[0];
            var recordingCandidate = source with { Match = new(direct.Length > 0 ? EnrichmentMatchBasis.ExternalReference : EnrichmentMatchBasis.Isrc,
                "Reviewed Release track match; affects every appearance of this Recording.", []) };
            var review = new EnrichmentReview(Guid.NewGuid().ToString("N"), "", EnrichmentEntityKind.Recording, matched.Entity.Id,
                clock.GetUtcNow(), recordingCandidate, Fields(matched, recordingCandidate),
                ["Only explicitly selected Recording fields are applied. Credits, duration, placements, artwork, ownership and Release metadata are not changed."]);
            review = review with { Version = Hash(new Payload(matched, review, batchId)) };
            childPayloads.Add(review.Id, new(matched, review, batchId));
            rows.Add(new(source.CandidateId, track.Title, "Confident", reasons, [matched.Entity.Id], review));
        }
        // A Recording reached by multiple provider tracks has no unique one-to-one version match.
        var duplicateIds = rows.Where(r => r.Review is not null).GroupBy(r => r.Review!.EntityId)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        rows = rows.Select(row => row.Review is { } review && duplicateIds.Contains(review.EntityId)
            ? row with { Status = "Ambiguous", Reasons = row.Reasons.Append("Multiple provider tracks target the same Recording.").ToArray(), Review = null }
            : row).ToList();
        foreach (var id in childPayloads.Keys.Where(id => !rows.Any(r => r.Review?.Id == id)).ToArray()) childPayloads.Remove(id);
        foreach (var snapshot in snapshots.Where(s => !rows.Any(r => r.RecordingIds.Contains(s.Entity.Id))))
            rows.Add(new("", snapshot.Recording!.Title ?? snapshot.Entity.Name ?? snapshot.Entity.Id, "Unmatched",
                ["Catalog Recording has no provider track match."], [snapshot.Entity.Id], null));
        var batch = new EnrichmentBatchReview(batchId, "", releaseId, clock.GetUtcNow(), candidate, rows,
            lookup.Warnings.Concat(candidate.Match.Conflicts).Concat([
                "Confirm the selected provider Release/edition before applying tracks. No Release identity is accepted automatically.",
                "Only confident unique matches can be selected. Missing fields default on; overwrites and provider identity acceptance require explicit selection.",
                "All selected Recordings apply in one transaction or none do. Changing a Recording affects all Releases using it."
            ]).ToArray());
        batch = batch with { Version = Hash(new BatchPayload("RecordingBatch", release, snapshots, batch)) };
        await using var save = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        if (Hash(await Read(save, CatalogEntityKind.Release, releaseId, ct).ConfigureAwait(false)) != Hash(release)) throw Stale();
        foreach (var snapshot in snapshots)
            if (Hash(await Read(save, CatalogEntityKind.Recording, snapshot.Entity.Id, ct).ConfigureAwait(false)) != Hash(snapshot)) throw Stale();
        foreach (var payload in childPayloads.Values)
            await Repo(save).SaveReviewAsync(new(payload.Review.Id, CatalogEntityKind.Recording, payload.Review.EntityId,
                payload.Review.Version, Json(payload), payload.Review.CreatedAt), ct).ConfigureAwait(false);
        await Repo(save).SaveReviewAsync(new(batch.Id, CatalogEntityKind.Release, releaseId, batch.Version,
            Json(new BatchPayload("RecordingBatch", release, snapshots, batch)), batch.CreatedAt), ct).ConfigureAwait(false);
        await save.CommitAsync(ct).ConfigureAwait(false);
        return batch;
    }

    private async ValueTask<bool> ArtistsAgree(IReadOnlyList<CatalogCredit> catalog,
        IReadOnlyList<EnrichmentCredit> provider, CancellationToken ct)
    {
        if (catalog.Count == 0 || catalog.Count != provider.Count) return false;
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        return await ArtistsAgreeInUnit(unit, catalog, provider, ct).ConfigureAwait(false);
    }
    private static async ValueTask<bool> ArtistsAgreeInUnit(IUnitOfWork unit, IReadOnlyList<CatalogCredit> catalog,
        IReadOnlyList<EnrichmentCredit> provider, CancellationToken ct)
    {
        if (catalog.Count == 0 || catalog.Count != provider.Count) return false;
        var names = new List<string>();
        foreach (var credit in catalog)
        {
            var artist = await unit.Artists.GetByIdAsync(credit.ArtistId, ct).ConfigureAwait(false);
            var name = credit.CreditedName ?? artist?.Name;
            if (string.IsNullOrWhiteSpace(name)) return false;
            names.Add(name.Trim().ToUpperInvariant());
        }
        return names.Order(StringComparer.Ordinal).SequenceEqual(provider.Select(c => c.Name.Trim().ToUpperInvariant()).Order(StringComparer.Ordinal));
    }

    private static async ValueTask<BatchPayload> LoadBatch(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await Repo(unit).GetReviewAsync(id, ct).ConfigureAwait(false)
            ?? throw new EnrichmentApplyException(404, "enrichment_review_not_found", "Batch review was not found.");
        using var document = JsonDocument.Parse(stored.PayloadJson);
        if (!document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "RecordingBatch")
            throw new EnrichmentApplyException(400, "enrichment_batch_required", "This is not a Recording batch review.");
        return JsonSerializer.Deserialize<BatchPayload>(stored.PayloadJson) ?? throw new InvalidDataException("Invalid batch review.");
    }
    public async ValueTask<EnrichmentBatchReview> GetBatchAsync(string reviewId, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var payload = await LoadBatch(unit, reviewId, ct).ConfigureAwait(false);
        var audit = await Repo(unit).GetAuditAsync(reviewId, ct).ConfigureAwait(false);
        return payload.Review with { Outcome = audit is null ? null : JsonSerializer.Deserialize<BatchApplied>(audit.AfterJson)!.Outcome };
    }
    public async ValueTask<EnrichmentBatchOutcome> ApplyBatchAsync(string reviewId, string version,
        IReadOnlyList<EnrichmentBatchSelection> selections, string reason, bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000 || selections.Count is 0 or > 100 ||
            selections.Any(s => s is null || s.Fields is null || s.Fields.Count is 0 or > 20) ||
            selections.Select(s => s.ReviewId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            throw new EnrichmentApplyException(400, "enrichment_invalid_apply", "Select distinct Recording reviews and fields, give a reason and confirm the Release match.");
        await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var payload = await LoadBatch(unit, reviewId, ct).ConfigureAwait(false);
        var batch = payload.Review;
        if (batch.Version != version) throw Stale();
        var reviews = batch.Tracks.Where(t => t.Review is not null).ToDictionary(t => t.Review!.Id, t => t.Review!, StringComparer.Ordinal);
        if (selections.Any(s => !reviews.ContainsKey(s.ReviewId)))
            throw new EnrichmentApplyException(400, "enrichment_field_unavailable", "A selected Recording does not belong to the confident matches in this batch.");
        var ordered = selections.OrderBy(s => s.ReviewId, StringComparer.Ordinal)
            .Select(s => s with { Fields = s.Fields.Order(StringComparer.Ordinal).ToArray() }).ToArray();
        var intent = Json(new { Selections = ordered, Reason = reason.Trim() });
        var existing = await Repo(unit).GetAuditAsync(reviewId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var saved = JsonSerializer.Deserialize<BatchApplied>(existing.AfterJson)!;
            if (saved.Intent != intent) throw new EnrichmentApplyException(409, "enrichment_already_applied", "This batch was applied with different selections or reason.");
            return saved.Outcome;
        }
        if (clock.GetUtcNow() - batch.CreatedAt >= TimeSpan.FromHours(24))
            throw new EnrichmentApplyException(410, "enrichment_review_expired", "This batch expired. Prepare a fresh review.");
        if (Hash(await Read(unit, CatalogEntityKind.Release, batch.ReleaseId, ct).ConfigureAwait(false)) != Hash(payload.Release)) throw Stale();
        foreach (var selection in ordered)
        {
            var snapshot = payload.Recordings.Single(s => s.Entity.Id == reviews[selection.ReviewId].EntityId);
            if (Hash(await Read(unit, CatalogEntityKind.Recording, snapshot.Entity.Id, ct).ConfigureAwait(false)) != Hash(snapshot)) throw Stale();
            var catalogArtists = snapshot.Credits.Where(c => c.Role == "primary").ToArray();
            var sourceArtists = (reviews[selection.ReviewId].Candidate.Metadata.Credits ?? []).Where(c => c.Role == "primary").ToArray();
            if (catalogArtists.Length > 0 && sourceArtists.Length > 0 &&
                !await ArtistsAgreeInUnit(unit, catalogArtists, sourceArtists, ct).ConfigureAwait(false)) throw Stale();
        }
        var outcomes = new List<EnrichmentApplyOutcome>();
        foreach (var selection in ordered)
            outcomes.Add(await ApplyInUnit(unit, selection.ReviewId, reviews[selection.ReviewId].Version, selection.Fields,
                selection.PreferredGenre, reason, true, ct, batch.Id).ConfigureAwait(false));
        var outcome = new EnrichmentBatchOutcome(batch.Id, batch.ReleaseId, clock.GetUtcNow(), outcomes);
        await Repo(unit).SaveAuditAsync(new(batch.Id, Json(ordered), reason.Trim(), Json(payload),
            Json(new BatchApplied(intent, outcome)), outcome.AppliedAt), ct).ConfigureAwait(false);
        await unit.CommitAsync(ct).ConfigureAwait(false);
        return outcome;
    }
}
