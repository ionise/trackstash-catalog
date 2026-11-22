using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

internal sealed partial class RemoteInventoryService
{
    private sealed record RemotePseudoPayload(string Type, CreateRemotePseudoRequest Request);

    public async Task<PseudoReleaseOutcome> CreatePseudoAsync(CreateRemotePseudoRequest request, CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct) < 13)
            throw new ApiRequestException(409, "enrichment_schema_required", "Pseudo releases require schema 13.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200 ||
            request.CatalogNumber?.Length > 200 || request.Items is null ||
            request.Items.Count is < 1 or > 100 || request.Items.Any(i => i is null))
            throw new ApiRequestException(400, "validation_failed", "Choose a release title and 1-100 device records.");
        ValidateKeys(request.Items.Select(i => i.Key).ToArray());
        if (request.Items.Any(i => i.Version is not { Length: 64 } || !i.Version.All(Uri.IsHexDigit)))
            throw new ApiRequestException(400, "validation_failed", "Choose current scanner snapshot versions.");
        var choices = request.Items.Select((item, index) => new PseudoMediaChoice(index + 1, item.RecordingId,
            item.CopyOfIndex, item.Title, item.Mix, item.ArtistId, item.NewArtist)).ToArray();
        MediaService.ValidatePseudoChoices(choices);
        var payload = new RemotePseudoPayload("RemotePseudoRelease", request);
        async Task<PseudoReleaseOutcome?> Replay(IUnitOfWork unit)
        {
            var review = await unit.Enrichment!.GetReviewAsync(request.OperationId, ct);
            if (review is null) return null;
            using var json = JsonDocument.Parse(review.PayloadJson);
            if (!json.RootElement.TryGetProperty("Type", out var type) || type.GetString() != payload.Type ||
                JsonSerializer.Serialize(JsonSerializer.Deserialize<RemotePseudoPayload>(review.PayloadJson)) != JsonSerializer.Serialize(payload))
                throw new ApiRequestException(409, "pseudo_operation_conflict", "This operation ID was already used for a different request.");
            var audit = await unit.Enrichment.GetAuditAsync(request.OperationId, ct)
                ?? throw new InvalidDataException("Remote pseudo-release operation is missing its outcome.");
            return JsonSerializer.Deserialize<PseudoReleaseOutcome>(audit.AfterJson)
                ?? throw new InvalidDataException("Remote pseudo-release outcome is invalid.");
        }
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
            if (await Replay(read) is { } saved) return saved;
        await using var connection = await OpenMatchStore(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await CheckStore(connection, transaction, false, ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if (await Replay(unit) is { } replay) return replay;
        var records = new List<RemoteInventoryRecord>();
        foreach (var item in request.Items)
        {
            var (record, version) = await ReadRemote(connection, transaction, item.Key, ct);
            if (version != item.Version)
                throw new ApiRequestException(412, "remote_inventory_changed", "Device inventory changed; reload the selected files.");
            var mediaId = RemoteMediaId(item.Key);
            if (await unit.Matches.GetBestMatchByMediaFileIdAsync(mediaId, ct) is not null ||
                await unit.MediaFiles.GetByIdAsync(mediaId, ct) is not null)
                throw new ApiRequestException(409, "media_already_registered", "A selected device record is already registered.");
            records.Add(record);
        }
        var now = clock.GetUtcNow();
        var (releaseId, recordingIds, count) = await MediaService.CreatePseudoGraphAsync(unit, request.Title, request.CatalogNumber,
            choices, records.Select((record, index) => (Id: (long)index + 1, record.Isrc)).ToDictionary(x => x.Id, x => x.Isrc), now, ct);
        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            var choice = choices[index];
            var record = records[index];
            var recordingId = recordingIds[choice.CopyOfMediaId ?? choice.MediaId];
            var mediaId = RemoteMediaId(item.Key);
            var candidate = await MediaService.CompareRemote(unit, record, recordingId, ct);
            var fingerprint = await FingerprintKey(connection, transaction, record, ct);
            var media = new MediaInventoryItem(record.MediaFileId, item.Key.DeviceId, record.Path!,
                record.Title, record.Subtitle, record.Artist, record.Album, record.Isrc, record.DurationSeconds,
                null, record.SizeBytes, record.ScannedUtc.ToString("O"), record.LastModifiedUtc.ToString("O"),
                record.Path!, record.Label, record.Release, fingerprint);
            var evidence = JsonSerializer.Serialize(new { Type = "RemoteMediaLink", item.Key, item.Version,
                ScannerRecord = record with { FingerprintRaw = null }, Candidate = candidate, FileVerified = false });
            await unit.MediaFiles.UpsertAsync(new MediaFile { Id = mediaId, Path = record.Path!,
                MetadataJson = JsonSerializer.Serialize(media), SourcePayloadJson = evidence, CreatedUtc = now, UpdatedUtc = now }, ct);
            await unit.Matches.UpsertBestMatchAsync(new MatchRecord { MediaFileId = mediaId, RecordingId = recordingId,
                OverrideState = MatchOverrideState.Manual, Score = 0, Confidence = 0, EvidenceJson = evidence,
                CreatedUtc = now, UpdatedUtc = now }, ct);
            var reviewId = Guid.NewGuid().ToString("N");
            await unit.Enrichment!.SaveReviewAsync(new(reviewId, CatalogEntityKind.Recording, recordingId, item.Version, evidence, now), ct);
            await unit.Enrichment.SaveAuditAsync(new(reviewId, "[\"RemoteMediaLink\"]",
                "User created a pseudo release using historical scanner metadata; file bytes were not verified.",
                evidence, JsonSerializer.Serialize(new { MediaId = mediaId, RecordingId = recordingId }), now), ct);
        }
        var outcome = new PseudoReleaseOutcome(request.OperationId, releaseId, count, request.Items.Count);
        var jsonPayload = JsonSerializer.Serialize(payload);
        await unit.Enrichment!.SaveReviewAsync(new(request.OperationId, CatalogEntityKind.Release, releaseId,
            Digest(jsonPayload), jsonPayload, now), ct);
        await unit.Enrichment.SaveAuditAsync(new(request.OperationId, "[\"RemotePseudoRelease\"]",
            "User created a pseudo release from uploaded scanner snapshots; file bytes were not verified.",
            jsonPayload, JsonSerializer.Serialize(outcome), now), ct);
        await unit.CommitAsync(ct);
        await transaction.CommitAsync(ct);
        return outcome;
    }
}
