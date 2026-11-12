using System.Text.Json;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Explicit review of a manual Recording link or reassignment.</summary>
public sealed record ManualMediaReview(string Id, string Version, MediaInventoryItem Media,
    MediaRecordingCandidate Candidate, string? PreviousRecordingId, string VerifiedSha256,
    DateTimeOffset CreatedAt, MediaLinkOutcome? Outcome = null);

public sealed partial class MediaService
{
    private sealed record ManualMediaPayload(string Type, string SourceId, ManualMediaReview Review,
        RecordingState Recording, MediaFile? PreviousFile, MatchRecord? PreviousMatch, string? SidecarHash);
    private const string ManualMediaReason = "User explicitly reviewed and confirmed the manual media Recording assignment.";

    public async Task<ManualMediaReview> PrepareManualMediaAsync(long id, string recordingId, CancellationToken ct)
    {
        await Ready(ct);
        if (id <= 0 || string.IsNullOrWhiteSpace(recordingId) || recordingId.Length > 200)
            throw Error(400, "validation_failed", "Choose a scanner file and an existing Recording.");
        var media = await Item(id, ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var recording = await ReadRecording(unit, recordingId, ct);
        var mediaId = MediaId(id);
        var file = await unit.MediaFiles.GetByIdAsync(mediaId, ct);
        var match = await unit.Matches.GetBestMatchByMediaFileIdAsync(mediaId, ct);
        var pathOwner = await unit.MediaFiles.GetByPathAsync(media.Path, ct);
        if (file is null && (match is not null || pathOwner is not null) ||
            file is not null && (file.Path != media.Path || pathOwner?.Id != mediaId || match is null ||
                match.OverrideState is not (MatchOverrideState.Accepted or MatchOverrideState.Manual) ||
                string.IsNullOrWhiteSpace(file.ContentHash)))
            throw Error(409, "media_already_linked", "This media registration cannot be safely reassigned in this workflow.");
        if (match?.RecordingId == recordingId)
            throw Error(409, "media_already_linked", "This file is already linked to that Recording.");
        var verified = await Verify(media, ct, file?.ContentHash);
        var sidecar = await ReadSidecar(media, ct);
        if (await unit.MediaFiles.HasConflictingRecordingMatchExceptAsync(verified.Hash, recordingId, mediaId, ct))
            throw Error(409, "media_identity_conflict", "Another copy with identical verified bytes is linked to a different Recording.");
        if (Json(await Item(id, ct)) != Json(media))
            throw Error(412, "enrichment_state_changed", "Scanner metadata changed during preparation.");
        var candidate = Compare(WithExplicitTitleMix(media, recording), recording, sidecar);
        var review = new ManualMediaReview(Guid.NewGuid().ToString("N"), "", media, candidate,
            match?.RecordingId, verified.Hash, clock.GetUtcNow());
        var payload = new ManualMediaPayload("ManualMediaLink", SourceId, review, recording, file, match, sidecar?.Hash);
        review = review with { Version = Hash(payload) };
        payload = payload with { Review = review };
        await unit.Enrichment!.SaveReviewAsync(new(review.Id, CatalogEntityKind.Recording, recordingId, review.Version,
            Json(payload), review.CreatedAt), ct);
        CheckVerifiedFile(media, verified.Modified);
        if ((await ReadSidecar(media, ct))?.Hash != sidecar?.Hash)
            throw Error(412, "enrichment_state_changed", "Sidecar changed during preparation.");
        await unit.CommitAsync(ct);
        return review;
    }

    private static async Task<ManualMediaPayload> LoadManualMedia(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await unit.Enrichment!.GetReviewAsync(id, ct) ?? throw Error(404, "media_review_not_found", "Manual media review was not found.");
        using var json = JsonDocument.Parse(stored.PayloadJson);
        if (!json.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "ManualMediaLink")
            throw Error(400, "media_review_invalid", "Use a manual media review.");
        return JsonSerializer.Deserialize<ManualMediaPayload>(stored.PayloadJson) ?? throw new InvalidDataException("Invalid manual media review.");
    }

    public async Task<ManualMediaReview> GetManualMediaAsync(string id, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var payload = await LoadManualMedia(unit, id, ct);
        var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
        return payload.Review with { Outcome = audit is null ? null : JsonSerializer.Deserialize<MediaLinkOutcome>(audit.AfterJson) };
    }

    public async Task<MediaLinkOutcome> ApplyManualMediaAsync(string id, string version, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) throw Error(400, "validation_failed", "Confirm the manual Recording assignment.");
        await Ready(ct);
        ManualMediaPayload payload;
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
        {
            payload = await LoadManualMedia(read, id, ct);
            if (payload.Review.Version != version) throw Error(412, "enrichment_state_changed", "Use the original manual review ETag.");
            if (await read.Enrichment!.GetAuditAsync(id, ct) is { } saved)
                return JsonSerializer.Deserialize<MediaLinkOutcome>(saved.AfterJson) ?? throw new InvalidDataException("Invalid manual media outcome.");
        }
        if (clock.GetUtcNow() - payload.Review.CreatedAt >= TimeSpan.FromHours(24))
            throw Error(410, "enrichment_review_expired", "Manual review expired; prepare a new review.");
        if (payload.SourceId != SourceId) throw Error(412, "enrichment_state_changed", "Scanner identity changed.");
        var media = await Item(payload.Review.Media.Id, ct);
        if (Json(media) != Json(payload.Review.Media)) throw Error(412, "enrichment_state_changed", "Scanner evidence changed.");
        var verified = await Verify(media, ct, payload.Review.VerifiedSha256);
        if ((await ReadSidecar(media, ct))?.Hash != payload.SidecarHash)
            throw Error(412, "enrichment_state_changed", "Sidecar changed.");
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if (await unit.Enrichment!.GetAuditAsync(id, ct) is { } completed)
            return JsonSerializer.Deserialize<MediaLinkOutcome>(completed.AfterJson) ?? throw new InvalidDataException("Invalid manual media outcome.");
        var mediaId = MediaId(media.Id);
        var file = await unit.MediaFiles.GetByIdAsync(mediaId, ct);
        var match = await unit.Matches.GetBestMatchByMediaFileIdAsync(mediaId, ct);
        var pathOwner = await unit.MediaFiles.GetByPathAsync(media.Path, ct);
        if (Json(file) != Json(payload.PreviousFile) || Json(match) != Json(payload.PreviousMatch) ||
            (payload.PreviousFile is null ? pathOwner is not null : pathOwner?.Id != mediaId) ||
            Json(await ReadRecording(unit, payload.Recording.Recording.Id, ct)) != Json(payload.Recording))
            throw Error(412, "enrichment_state_changed", "The prior link or Catalog Recording changed; prepare a new review.");
        if (await unit.MediaFiles.HasConflictingRecordingMatchExceptAsync(verified.Hash, payload.Recording.Recording.Id, mediaId, ct))
            throw Error(409, "media_identity_conflict", "Another copy with identical verified bytes is linked to a different Recording.");
        var now = clock.GetUtcNow();
        if (file is null)
            await unit.MediaFiles.UpsertAsync(new MediaFile { Id = mediaId, Path = media.Path, ContentHash = verified.Hash,
                MetadataJson = Json(media), SourcePayloadJson = Json(new { ScannerIdentity = SourceId, ScannerMediaId = media.Id, ReviewId = id }),
                CreatedUtc = now, UpdatedUtc = now }, ct);
        await unit.Matches.UpsertBestMatchAsync(new MatchRecord { MediaFileId = mediaId, RecordingId = payload.Recording.Recording.Id,
            OverrideState = MatchOverrideState.Manual, Score = 0, Confidence = 0, CreatedUtc = match?.CreatedUtc ?? now, UpdatedUtc = now,
            EvidenceJson = Json(new { ReviewId = id, Candidate = payload.Review.Candidate, Authority = "ExplicitUserReview" }) }, ct);
        var outcome = new MediaLinkOutcome(id, mediaId, payload.Recording.Recording.Id, now);
        await unit.Enrichment!.SaveAuditAsync(new(id, Json(new[] { "ManualMediaLink" }), ManualMediaReason, Json(payload), Json(outcome), now), ct);
        CheckVerifiedFile(media, verified.Modified);
        if ((await ReadSidecar(media, ct))?.Hash != payload.SidecarHash || Json(await Item(media.Id, ct)) != Json(media) || payload.SourceId != SourceId)
            throw Error(412, "enrichment_state_changed", "File, sidecar or scanner evidence changed; nothing was saved.");
        await unit.CommitAsync(ct);
        return outcome;
    }
}
