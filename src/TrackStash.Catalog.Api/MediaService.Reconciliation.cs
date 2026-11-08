using System.Text.Json;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>One proposed file-to-Recording choice; values and paths are derived on the server.</summary>
public sealed record MediaTrackSelection(long MediaId, string RecordingId);
/// <summary>A file and bounded candidates drawn only from the chosen Release's existing tracks.</summary>
public sealed record MediaReconciliationFile(MediaInventoryItem Media, IReadOnlyList<MediaRecordingCandidate> Candidates,
    string? SuggestedRecordingId, string? LinkedRecordingId, bool AlreadyRegistered);
/// <summary>One existing Catalog track and its proposed media copies.</summary>
public sealed record MediaReconciliationTrack(string RecordingId, string? Title, string? Mix, IReadOnlyList<string> Artists);
/// <summary>A read-only whole-release proposal; exact fingerprints corroborate audio, never release identity.</summary>
public sealed record MediaReconciliationPlan(string GroupKey, string ReleaseId, string CatalogTitle, string Version,
    IReadOnlyList<string> Evidence, IReadOnlyList<MediaReconciliationTrack> Tracks, IReadOnlyList<MediaReconciliationFile> Files,
    bool ReleaseAlreadyAssociated);
/// <summary>A frozen batch of explicitly selected verified files and a whole-group Release association.</summary>
public sealed record MediaReconciliationReview(string Id, string Version, MediaReconciliationPlan Plan,
    IReadOnlyList<MediaTrackSelection> Selections, IReadOnlyList<long> VerifiedMediaIds, DateTimeOffset CreatedAt,
    MediaReconciliationOutcome? Outcome = null);
/// <summary>An atomic audited batch result; unselected files remain unlinked.</summary>
public sealed record MediaReconciliationOutcome(string ReviewId, string ReleaseId, IReadOnlyList<MediaLinkOutcome> Links,
    MediaReleaseAssociation Association, DateTimeOffset AppliedAt);

public sealed partial class MediaService
{
    private sealed record ReconciliationContext(MediaInventoryItem[] Files, ReleaseState Catalog, RecordingState[] Recordings,
        MediaReleaseAssociation? Association, IReadOnlyList<RegistrationState> Registrations);
    private sealed record RegistrationState(long MediaId, string? RecordingId, bool Registered);
    private sealed record VerifiedDependency(MediaInventoryItem Media, string? SidecarHash, string? VerifiedSha256 = null);
    private sealed record ReconciliationPayload(string Type, string SourceId, MediaReconciliationReview Review,
        ReconciliationContext Context, IReadOnlyList<LinkPayload> Links, IReadOnlyList<VerifiedDependency> Dependencies);

    private async Task<ReconciliationContext> ReconciliationState(IUnitOfWork unit, string groupKey, string releaseId, CancellationToken ct)
    {
        var files = await ReleaseGroup(groupKey, ct);
        var catalog = await ReadRelease(unit, releaseId, ct);
        var ids = catalog.Placements.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => p.RecordingId).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length > 100) throw Error(409, "media_track_limit", "Reconciliation supports at most 100 existing Recordings per Release.");
        var recordings = new List<RecordingState>();
        foreach (var id in ids) recordings.Add(await ReadRecording(unit, id, ct));
        var applied = (await AppliedReleaseGroups(unit, ct)).Where(p => p.SourceId == SourceId &&
            p.Review.Files.Any(f => files.Any(current => current.Id == f.Id))).ToArray();
        if (applied.Length > 1 || applied.Any(p => p.Review.ReleaseId != releaseId ||
            p.Review.GroupKey != groupKey || Json(p.Review.Files.Select(f => f.Id)) != Json(files.Select(f => f.Id))))
            throw Error(409, "media_release_already_associated", "These files already have a different Release association. No existing association can be replaced.");
        MediaReleaseAssociation? association = null;
        if (applied.Length == 1)
        {
            var audit = await unit.Enrichment!.GetAuditAsync(applied[0].Review.Id, ct)
                ?? throw new InvalidDataException("Missing media association audit.");
            association = JsonSerializer.Deserialize<MediaReleaseAssociation>(audit.AfterJson)
                ?? throw new InvalidDataException("Invalid media association audit.");
        }
        var registrations = new List<RegistrationState>();
        foreach (var file in files)
        {
            var match = await unit.Matches.GetBestMatchByMediaFileIdAsync(MediaId(file.Id), ct);
            registrations.Add(new(file.Id, match?.RecordingId, await unit.MediaFiles.GetByIdAsync(MediaId(file.Id), ct) is not null ||
                await unit.MediaFiles.GetByPathAsync(file.Path, ct) is not null));
        }
        return new(files, catalog, recordings.ToArray(), association, registrations);
    }
    private static IEnumerable<MediaInventoryItem> SupportingFiles(ReconciliationContext context, string recordingId) =>
        context.Files.Where(f => context.Registrations.Any(r => r.MediaId == f.Id &&
            (r.RecordingId == recordingId || r.RecordingId is null && !r.Registered)));
    private static MediaInventoryItem WithExplicitTitleMix(MediaInventoryItem media, RecordingState state)
    {
        var title = state.Recording.Title ?? state.Recording.Name;
        var mix = state.Recording.MixName;
        return !string.IsNullOrWhiteSpace(mix) && Same(media.Title, $"{title} {mix}")
            ? media with { Title = $"{title} ({mix})" } : media;
    }
    private static MediaRecordingCandidate ReconciliationCandidate(MediaInventoryItem media, RecordingState recording,
        IEnumerable<MediaInventoryItem> files, Sidecar? sidecar = null)
    {
        var candidate = Compare(WithExplicitTitleMix(media, recording), recording, sidecar);
        if (candidate.Status == "Supported") return candidate;
        var conflicts = candidate.Blockers.Where(b => b != "Insufficient independent tagged metadata to accept a link.").ToArray();
        if (conflicts.Length != 0 || media.FingerprintKey is null ||
            !files.Any(anchor => anchor.Id != media.Id && anchor.FingerprintKey == media.FingerprintKey &&
                Compare(WithExplicitTitleMix(anchor, recording), recording).Status == "Supported"))
            return candidate;
        return candidate with { Status = "Supported", Blockers = [],
            Evidence = candidate.Evidence.Concat(["Exact stored audio fingerprint agrees with an independently supported copy in this Release group. That supporting file must also be byte/sidecar verified."]).ToArray() };
    }
    private MediaReconciliationPlan BuildReconciliationPlan(string groupKey, string releaseId,
        ReconciliationContext context, CancellationToken ct)
    {
        var files = new List<MediaReconciliationFile>();
        foreach (var media in context.Files)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = context.Recordings.Select(r => ReconciliationCandidate(media, r, SupportingFiles(context, r.Recording.Id)))
                .OrderBy(c => c.Blockers.Count).ThenBy(c => c.RecordingId, StringComparer.Ordinal).ToArray();
            var supported = candidates.Where(c => c.Status == "Supported").ToArray();
            var registration = context.Registrations.Single(r => r.MediaId == media.Id);
            files.Add(new(media, candidates, registration.RecordingId is null && !registration.Registered && supported.Length == 1 ? supported[0].RecordingId : null,
                registration.RecordingId, registration.Registered));
        }
        var title = context.Catalog.Release.Title ?? context.Catalog.Release.Name ?? releaseId;
        var evidence = new List<string> {
            Same(context.Files[0].Release, title) ? "Release title agrees." : "Release title differs; confirm the full group's Release identity.",
            context.Catalog.Labels.Any(l => Same(context.Files[0].Label, l)) ? "Embedded Label agrees." : "Embedded Label differs or is unknown; check it before confirming.",
            "The whole metadata group is associated with the Release; only selected files are linked to its existing Recordings. No tracks, placements, purchases or media copies are created."
        };
        var plan = new MediaReconciliationPlan(groupKey, releaseId, title, "", evidence,
            context.Recordings.Select(r => new MediaReconciliationTrack(r.Recording.Id, r.Recording.Title ?? r.Recording.Name,
                r.Recording.MixName, r.PrimaryArtists)).ToArray(), files, context.Association is not null);
        return plan with { Version = Hash(new { SourceId, Context = context, Plan = plan }) };
    }
    public async Task<MediaReconciliationPlan> ReconcileAsync(string groupKey, string releaseId, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        return BuildReconciliationPlan(groupKey, releaseId, await ReconciliationState(unit, groupKey, releaseId, ct), ct);
    }
    public async Task<MediaReconciliationReview> PrepareReconciliationAsync(string groupKey, string releaseId, string version,
        IReadOnlyList<MediaTrackSelection> selections, CancellationToken ct)
    {
        if (selections.Count is < 1 or > 100 || selections.Select(s => s.MediaId).Distinct().Count() != selections.Count)
            throw Error(400, "validation_failed", "Select between 1 and 100 distinct files.");
        await Ready(ct);
        ReconciliationContext context;
        MediaReconciliationPlan plan;
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
        {
            context = await ReconciliationState(read, groupKey, releaseId, ct);
            plan = BuildReconciliationPlan(groupKey, releaseId, context, ct);
        }
        if (plan.Version != version) throw Error(412, "enrichment_state_changed", "Release proposals changed; reload matches before verification.");
        var dependencies = new Dictionary<long, VerifiedDependency>();
        var modified = new Dictionary<long, DateTime>();
        var verification = new Dictionary<long, (string Hash, Sidecar? Sidecar)>();
        async Task<(string Hash, Sidecar? Sidecar)> VerifyDependency(MediaInventoryItem media)
        {
            if (verification.TryGetValue(media.Id, out var cached)) return cached;
            var bytes = await Verify(media, ct);
            var sidecar = await ReadSidecar(media, ct);
            dependencies[media.Id] = new(media, sidecar?.Hash, bytes.Hash);
            modified[media.Id] = bytes.Modified;
            return verification[media.Id] = (bytes.Hash, sidecar);
        }
        var links = new List<LinkPayload>();
        foreach (var selection in selections.OrderBy(s => s.MediaId))
        {
            var file = plan.Files.SingleOrDefault(f => f.Media.Id == selection.MediaId)
                ?? throw Error(400, "media_candidate_unavailable", "Select only files in the complete Release group.");
            if (file.LinkedRecordingId is not null || file.AlreadyRegistered)
                throw Error(409, "media_already_linked", "Existing file registrations are not overwritten.");
            var candidate = file.Candidates.SingleOrDefault(c => c.RecordingId == selection.RecordingId && c.Blockers.Count == 0)
                ?? throw Error(409, "media_identity_conflict", "Choose an eligible existing Release track; conflicting or insufficient metadata cannot be accepted.");
            var recording = context.Recordings.Single(r => r.Recording.Id == selection.RecordingId);
            var verified = await VerifyDependency(file.Media);
            var sidecar = verified.Sidecar;
            var supporting = SupportingFiles(context, recording.Recording.Id).ToArray();
            candidate = ReconciliationCandidate(file.Media, recording, supporting, sidecar);
            if (candidate.Blockers.Count > 0) throw Error(409, "media_identity_conflict", $"Media {file.Media.Id}: {string.Join(" ", candidate.Blockers)}");
            if (Compare(WithExplicitTitleMix(file.Media, recording), recording, sidecar).Status != "Supported" && candidate.Status == "Supported")
            {
                var anchor = supporting.First(f => f.Id != file.Media.Id && f.FingerprintKey == file.Media.FingerprintKey &&
                    Compare(WithExplicitTitleMix(f, recording), recording).Status == "Supported");
                var anchorVerified = await VerifyDependency(anchor);
                if (Compare(WithExplicitTitleMix(anchor, recording), recording, anchorVerified.Sidecar).Status != "Supported")
                    throw Error(409, "media_identity_conflict", "The fingerprint-supporting copy has conflicting sidecar evidence.");
            }
            var linkReview = new MediaLinkReview(Guid.NewGuid().ToString("N"), "", file.Media, candidate, clock.GetUtcNow(),
                verified.Hash, sidecar?.Title, sidecar?.Mix);
            linkReview = linkReview with { Version = Hash(new LinkPayload("MediaLink", SourceId, linkReview, recording, sidecar?.Hash)) };
            links.Add(new("MediaLink", SourceId, linkReview, recording, sidecar?.Hash));
        }
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var current = await ReconciliationState(unit, groupKey, releaseId, ct);
        if (BuildReconciliationPlan(groupKey, releaseId, current, ct).Version != version)
            throw Error(412, "enrichment_state_changed", "Scanner, Release, Recording or existing links changed during verification.");
        var review = new MediaReconciliationReview(Guid.NewGuid().ToString("N"), "", plan,
            selections.OrderBy(s => s.MediaId).ToArray(), dependencies.Keys.Order().ToArray(), clock.GetUtcNow());
        var payload = new ReconciliationPayload("MediaReconciliation", SourceId, review, context, links, dependencies.Values.ToArray());
        review = review with { Version = Hash(payload) };
        await unit.Enrichment!.SaveReviewAsync(new(review.Id, CatalogEntityKind.Release, releaseId, review.Version,
            Json(payload with { Review = review }), review.CreatedAt), ct);
        await CheckDependencies(dependencies.Values, modified, ct);
        await unit.CommitAsync(ct);
        return review;
    }
    private async Task CheckDependencies(IEnumerable<VerifiedDependency> dependencies, IReadOnlyDictionary<long, DateTime> modified, CancellationToken ct)
    {
        foreach (var dependency in dependencies)
        {
            CheckVerifiedFile(dependency.Media, modified[dependency.Media.Id]);
            if ((await ReadSidecar(dependency.Media, ct))?.Hash != dependency.SidecarHash)
                throw Error(412, "enrichment_state_changed", "A verified sidecar changed; nothing was saved.");
        }
    }
    private static async Task<ReconciliationPayload> LoadReconciliation(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await unit.Enrichment!.GetReviewAsync(id, ct) ?? throw Error(404, "media_review_not_found", "Reconciliation review was not found.");
        using var json = JsonDocument.Parse(stored.PayloadJson);
        if (!json.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "MediaReconciliation")
            throw Error(400, "media_review_invalid", "Use a media reconciliation review.");
        return JsonSerializer.Deserialize<ReconciliationPayload>(stored.PayloadJson) ?? throw new InvalidDataException("Invalid reconciliation review.");
    }
    public async Task<MediaReconciliationReview> GetReconciliationAsync(string id, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var payload = await LoadReconciliation(unit, id, ct);
        var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
        return payload.Review with { Outcome = audit is null ? null : JsonSerializer.Deserialize<MediaReconciliationOutcome>(audit.AfterJson) };
    }
    public async Task<MediaReconciliationOutcome> ApplyReconciliationAsync(string id, string version, string reason, bool confirmed, CancellationToken ct)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw Error(400, "validation_failed", "Confirm the whole Release and selected Recording versions with a bounded audit reason.");
        await Ready(ct);
        ReconciliationPayload payload;
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
        {
            payload = await LoadReconciliation(read, id, ct);
            if (payload.Review.Version != version) throw Error(412, "enrichment_state_changed", "Use the original reconciliation review ETag.");
            var audit = await read.Enrichment!.GetAuditAsync(id, ct);
            if (audit is not null)
            {
                if (audit.Reason != reason.Trim()) throw Error(409, "enrichment_already_applied", "Reconciliation was applied with a different reason.");
                return JsonSerializer.Deserialize<MediaReconciliationOutcome>(audit.AfterJson)!;
            }
        }
        if (clock.GetUtcNow() - payload.Review.CreatedAt >= TimeSpan.FromHours(24)) throw Error(410, "enrichment_review_expired", "Reconciliation review expired.");
        if (payload.SourceId != SourceId) throw Error(412, "enrichment_state_changed", "Scanner source changed.");
        var modified = new Dictionary<long, DateTime>();
        foreach (var dependency in payload.Dependencies)
        {
            var expectedHash = dependency.VerifiedSha256 ?? dependency.Media.ContentHash;
            if (string.IsNullOrWhiteSpace(expectedHash))
                throw Error(409, "media_review_invalid", "A supporting file has no frozen SHA-256; prepare a fresh reconciliation review.");
            var verified = await Verify(dependency.Media, ct, expectedHash);
            modified[dependency.Media.Id] = verified.Modified;
        }
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var saved = await unit.Enrichment!.GetAuditAsync(id, ct);
        if (saved is not null)
        {
            if (saved.Reason != reason.Trim()) throw Error(409, "enrichment_already_applied", "Reconciliation was applied with a different reason.");
            return JsonSerializer.Deserialize<MediaReconciliationOutcome>(saved.AfterJson)!;
        }
        var context = await ReconciliationState(unit, payload.Review.Plan.GroupKey, payload.Review.Plan.ReleaseId, ct);
        if (BuildReconciliationPlan(payload.Review.Plan.GroupKey, payload.Review.Plan.ReleaseId, context, ct).Version != payload.Review.Plan.Version)
            throw Error(412, "enrichment_state_changed", "Release group, track evidence or existing links changed; prepare a fresh review.");
        await CheckDependencies(payload.Dependencies, modified, ct);
        var outcomes = new List<MediaLinkOutcome>();
        foreach (var link in payload.Links)
        {
            await unit.Enrichment.SaveReviewAsync(new(link.Review.Id, CatalogEntityKind.Recording, link.Review.Candidate.RecordingId,
                link.Review.Version, Json(link), link.Review.CreatedAt), ct);
            outcomes.Add(await SaveLink(unit, link, reason, ct));
        }
        var association = context.Association;
        if (association is null)
        {
            var plan = payload.Review.Plan;
            var releaseReview = new MediaReleaseReview(Guid.NewGuid().ToString("N"), "", plan.GroupKey, context.Files[0].Label,
                context.Files[0].Release!, plan.ReleaseId, plan.CatalogTitle, context.Files, plan.Evidence, payload.Review.CreatedAt);
            releaseReview = releaseReview with { Version = Hash(new ReleasePayload("MediaReleaseGroup", SourceId, releaseReview, context.Catalog)) };
            var releasePayload = new ReleasePayload("MediaReleaseGroup", SourceId, releaseReview, context.Catalog);
            association = new(releaseReview.Id, plan.GroupKey, plan.ReleaseId, plan.CatalogTitle, releaseReview.Label,
                releaseReview.ReleaseTitle, context.Files.Length, context.Files.Select(f => f.Library).Distinct().ToArray(), clock.GetUtcNow());
            await unit.Enrichment.SaveReviewAsync(new(releaseReview.Id, CatalogEntityKind.Release, plan.ReleaseId, releaseReview.Version,
                Json(releasePayload), releaseReview.CreatedAt), ct);
            await unit.Enrichment.SaveAuditAsync(new(releaseReview.Id, Json(new[] { "MediaReleaseAssociation" }), reason.Trim(),
                Json(releasePayload), Json(association), association.AppliedAt), ct);
        }
        var outcome = new MediaReconciliationOutcome(id, payload.Review.Plan.ReleaseId, outcomes, association, clock.GetUtcNow());
        await unit.Enrichment.SaveAuditAsync(new(id, Json(new[] { "MediaReconciliation" }), reason.Trim(), Json(payload), Json(outcome), outcome.AppliedAt), ct);
        await CheckDependencies(payload.Dependencies, modified, ct);
        if (Json(await ReleaseGroup(payload.Review.Plan.GroupKey, ct)) != Json(context.Files))
            throw Error(412, "enrichment_state_changed", "Scanner group changed during linking; no batch links were saved.");
        await unit.CommitAsync(ct);
        return outcome;
    }
}
