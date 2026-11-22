using System.Text.Json;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>One explicitly chosen pseudo-release track or additional copy.</summary>
public sealed record PseudoMediaChoice(long MediaId, string? RecordingId, long? CopyOfMediaId,
    string? Title, string? Mix, string? ArtistId, string? NewArtist);

/// <summary>Creates a pseudo release from server-held scanner records and explicit identity choices.</summary>
public sealed record CreatePseudoReleaseRequest(string OperationId, string PlanVersion, string Title,
    string? CatalogNumber, IReadOnlyList<PseudoMediaChoice> Items);

/// <summary>Replayable result of an atomic pseudo-release creation.</summary>
public sealed record PseudoReleaseOutcome(string OperationId, string ReleaseId, int Recordings, int MediaFiles);

public sealed partial class MediaService
{
    private sealed record PseudoPayload(string Type, CreatePseudoReleaseRequest Request, string SourceId,
        IReadOnlyList<MediaInventoryItem> Media);

    public async Task<PseudoReleaseOutcome> CreatePseudoReleaseAsync(CreatePseudoReleaseRequest request, CancellationToken ct)
    {
        await Ready(ct);
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.PlanVersion) || request.PlanVersion.Length != 64 || !request.PlanVersion.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200 ||
            request.CatalogNumber?.Length > 200 || request.Items is null ||
            request.Items.Count is < 1 or > 100 || request.Items.Any(i => i is null))
            throw Error(400, "validation_failed", "Choose a release title, current proposals and 1–100 media files.");
        ValidatePseudoChoices(request.Items);

        async Task<PseudoReleaseOutcome?> Replay(IUnitOfWork unit)
        {
            var stored = await unit.Enrichment!.GetReviewAsync(request.OperationId, ct);
            if (stored is null) return null;
            var payload = JsonSerializer.Deserialize<PseudoPayload>(stored.PayloadJson);
            if (payload?.Type != "PseudoRelease" || Json(payload.Request) != Json(request))
                throw Error(409, "pseudo_operation_conflict", "This operation ID was already used for a different request.");
            var audit = await unit.Enrichment.GetAuditAsync(request.OperationId, ct)
                ?? throw new InvalidDataException("Pseudo-release operation is missing its outcome.");
            return JsonSerializer.Deserialize<PseudoReleaseOutcome>(audit.AfterJson)
                ?? throw new InvalidDataException("Pseudo-release outcome is invalid.");
        }
        MatchState state;
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
        {
            if (await Replay(read) is { } saved) return saved;
            state = await MatchStateAsync(read, request.Items.Select(i => i.MediaId).ToArray(), ct);
        }
        if (state.Plan.Version != request.PlanVersion)
            throw Error(412, "enrichment_state_changed", "Media proposals changed; reload them before creating the release.");
        if (state.Plan.Files.Any(f => f.AlreadyRegistered || f.LinkedRecordingId is not null))
            throw Error(409, "media_already_linked", "Only unmatched, unregistered media can be added.");
        var verified = new Dictionary<long, (string Hash, DateTime Modified, string? SidecarHash)>();
        foreach (var row in state.Plan.Files)
        {
            var bytes = await Verify(row.Media, ct);
            verified.Add(row.Media.Id, (bytes.Hash, bytes.Modified, (await ReadSidecar(row.Media, ct))?.Hash));
        }
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if (await Replay(unit) is { } replay) return replay;
        if ((await MatchStateAsync(unit, request.Items.Select(i => i.MediaId).ToArray(), ct)).Plan.Version != request.PlanVersion)
            throw Error(412, "enrichment_state_changed", "Media or Catalog evidence changed during verification.");
        var now = clock.GetUtcNow();
        var (releaseId, recordingIds, placements) = await CreatePseudoGraphAsync(unit, request.Title, request.CatalogNumber,
            request.Items, state.Plan.Files.ToDictionary(f => f.Media.Id, f => f.Media.Isrc), now, ct);
        foreach (var choice in request.Items)
        {
            var recordingId = recordingIds[choice.CopyOfMediaId ?? choice.MediaId];
            var row = state.Plan.Files.Single(f => f.Media.Id == choice.MediaId);
            var recording = await ReadRecording(unit, recordingId, ct);
            var sidecar = await ReadSidecar(row.Media, ct);
            if (sidecar?.Hash != verified[row.Media.Id].SidecarHash)
                throw Error(412, "enrichment_state_changed", "Sidecar changed during verification.");
            var candidate = Compare(WithExplicitTitleMix(row.Media, recording), recording, sidecar);
            if (candidate.Blockers.Any(b => b.StartsWith("Sidecar ", StringComparison.Ordinal)))
                throw Error(409, "media_identity_conflict", string.Join(" ", candidate.Blockers));
            var linkReview = new MediaLinkReview(Guid.NewGuid().ToString("N"), "", row.Media, candidate, now,
                verified[row.Media.Id].Hash, sidecar?.Title, sidecar?.Mix);
            var link = new LinkPayload("MediaLink", SourceId, linkReview, recording, sidecar?.Hash);
            linkReview = linkReview with { Version = Hash(link) };
            link = link with { Review = linkReview };
            await unit.Enrichment!.SaveReviewAsync(new(linkReview.Id, CatalogEntityKind.Recording, recordingId,
                linkReview.Version, Json(link), now), ct);
            await SaveLink(unit, link, "User created a pseudo release and explicitly selected the media.", ct);
        }
        var payload = new PseudoPayload("PseudoRelease", request, SourceId, state.Plan.Files.Select(f => f.Media).ToArray());
        var outcome = new PseudoReleaseOutcome(request.OperationId, releaseId, placements, request.Items.Count);
        await unit.Enrichment!.SaveReviewAsync(new(request.OperationId, CatalogEntityKind.Release, releaseId,
            request.PlanVersion, Json(payload), now), ct);
        await unit.Enrichment.SaveAuditAsync(new(request.OperationId, Json(new[] { "PseudoRelease" }),
            "User created a pseudo release and explicitly selected the media.", Json(payload), Json(outcome), now), ct);
        foreach (var row in state.Plan.Files)
        {
            CheckVerifiedFile(row.Media, verified[row.Media.Id].Modified);
            if ((await ReadSidecar(row.Media, ct))?.Hash != verified[row.Media.Id].SidecarHash ||
                EvidenceJson(await Item(row.Media.Id, ct)) != EvidenceJson(row.Media))
                throw Error(412, "enrichment_state_changed", "File, sidecar or scanner evidence changed; nothing was saved.");
        }
        await unit.CommitAsync(ct);
        return outcome;
    }

    internal static void ValidatePseudoChoices(IReadOnlyList<PseudoMediaChoice> items)
    {
        MatchIds(items.Select(i => i.MediaId).ToArray());
        foreach (var choice in items)
        {
            if (choice.CopyOfMediaId is not null && (choice.CopyOfMediaId == choice.MediaId ||
                choice.RecordingId is not null || choice.Title is not null || choice.Mix is not null ||
                choice.ArtistId is not null || choice.NewArtist is not null) ||
                choice.CopyOfMediaId is null && choice.RecordingId is null &&
                (string.IsNullOrWhiteSpace(choice.Title) || choice.Title.Trim().Length > 200 ||
                 choice.Mix?.Length > 200 || choice.NewArtist?.Length > 200 ||
                 choice.ArtistId is not null && choice.NewArtist is not null) ||
                choice.RecordingId is not null &&
                (choice.Title is not null || choice.Mix is not null || choice.ArtistId is not null || choice.NewArtist is not null) ||
                choice.RecordingId is { Length: 0 or > 200 } || choice.ArtistId is { Length: 0 or > 200 } ||
                choice.NewArtist is not null && string.IsNullOrWhiteSpace(choice.NewArtist))
                throw Error(400, "validation_failed", "Choose an existing Recording, create a titled Recording, or identify another selected copy.");
        }
        var choices = items.ToDictionary(i => i.MediaId);
        if (items.Any(i => i.CopyOfMediaId is { } source &&
            (!choices.TryGetValue(source, out var original) || original.CopyOfMediaId is not null)))
            throw Error(400, "validation_failed", "A copy must refer to a selected primary track, not another copy.");
    }

    internal static async Task<(string ReleaseId, Dictionary<long, string> RecordingIds, int Placements)> CreatePseudoGraphAsync(
        IUnitOfWork unit, string title, string? catalogNumber, IReadOnlyList<PseudoMediaChoice> choices,
        IReadOnlyDictionary<long, string?> isrcs, DateTimeOffset now, CancellationToken ct)
    {
        var normalizedLabel = EntityNameNormalizer.NormalizeStrict("Not on Label");
        var labelPage = await unit.Labels.QueryAsync(new LabelQuery { Search = "Not on Label", PageSize = 100 }, ct);
        if (labelPage.Total > labelPage.Items.Count)
            throw Error(409, "pseudo_label_ambiguous", "Too many similar Labels exist; resolve Not on Label before creating a pseudo release.");
        var labels = labelPage.Items.Where(l => EntityNameNormalizer.NormalizeStrict(l.Name) == normalizedLabel).ToArray();
        if (labels.Length > 1)
            throw Error(409, "pseudo_label_ambiguous", "Multiple Not on Label identities exist; resolve them before creating a pseudo release.");
        var labelId = labels.SingleOrDefault()?.Id;
        if (labelId is null)
        {
            labelId = Guid.NewGuid().ToString("N");
            if (!await unit.Labels.TryCreateAsync(new Label { Id = labelId, Name = "Not on Label",
                NormalizedName = normalizedLabel, CreatedUtc = now, UpdatedUtc = now }, ct))
                throw Error(409, "pseudo_label_conflict", "Not on Label could not be created; reload and retry.");
        }
        else
        {
            var identity = await unit.Labels.ResolveIdentityAsync(labelId, ct);
            if (identity.CanonicalId != labelId)
                throw Error(409, "pseudo_label_ambiguous", "Not on Label is retired; resolve the label identity first.");
        }
        var releaseId = Guid.NewGuid().ToString("N");
        title = title.Trim();
        var release = new Release { Id = releaseId, Title = title, Name = title,
            NormalizedName = EntityNameNormalizer.NormalizeStrict(title),
            CatalogNumber = string.IsNullOrWhiteSpace(catalogNumber) ? "NO CAT" : catalogNumber.Trim(),
            CreatedUtc = now, UpdatedUtc = now,
            LabelLinks = [new() { LabelId = labelId, IsPrimary = true, Role = "issuing" }] };
        var foundation = unit.CatalogFoundations!;
        await foundation.SaveEntityAsync(release, null, ct);
        var recordingIds = new Dictionary<long, string>();
        var artists = new Dictionary<string, string>(StringComparer.Ordinal);
        var placements = 0;
        foreach (var choice in choices.Where(i => i.CopyOfMediaId is null))
        {
            string recordingId;
            if (choice.RecordingId is { } existing)
            {
                if (await unit.Recordings.GetByIdAsync(existing, ct) is null)
                    throw Error(404, "recording_not_found", "A chosen Recording no longer exists.");
                recordingId = existing;
            }
            else
            {
                string? artistId = choice.ArtistId;
                if (artistId is not null && await unit.Artists.GetByIdAsync(artistId, ct) is null)
                    throw Error(404, "artist_not_found", "A chosen Artist no longer exists.");
                if (choice.NewArtist is { } artistName)
                {
                    artistName = artistName.Trim();
                    if (!artists.TryGetValue(artistName, out artistId))
                    {
                        var candidates = await unit.ReleaseImports!.FindCandidatesAsync(CatalogEntityKind.Artist,
                            EntityNameNormalizer.NormalizeStrict(artistName), cancellationToken: ct);
                        if (candidates.Count != 0)
                            throw Error(409, "pseudo_artist_ambiguous", $"Artist '{artistName}' already has Catalog candidates; choose an existing Artist explicitly.");
                        artistId = Guid.NewGuid().ToString("N");
                        await foundation.SaveEntityAsync(new Artist { Id = artistId, Name = artistName,
                            NormalizedName = EntityNameNormalizer.NormalizeStrict(artistName), Kind = "unknown",
                            IdentityStatus = artistName.Contains(',') || artistName.Contains(" & ", StringComparison.Ordinal)
                                ? "unresolved_compound" : "accepted", CreatedUtc = now, UpdatedUtc = now }, null, ct);
                        artists.Add(artistName, artistId);
                    }
                }
                var mediaIsrc = isrcs[choice.MediaId];
                var isrc = Isrc(mediaIsrc) ? mediaIsrc!.Trim().ToUpperInvariant() : null;
                if (isrc is not null && await unit.Recordings.GetByIsrcAsync(isrc, ct) is not null)
                    throw Error(409, "pseudo_recording_ambiguous", "A media item has an existing ISRC match; choose that Recording explicitly.");
                recordingId = Guid.NewGuid().ToString("N");
                var trackTitle = choice.Title!.Trim();
                var creditName = choice.NewArtist?.Trim();
                var credit = artistId is null ? null : new CatalogCredit(Guid.NewGuid().ToString("N"),
                    artistId, "primary", 1, creditName, null, null, null, null);
                await foundation.SaveEntityAsync(new Recording { Id = recordingId, Name = trackTitle, Title = trackTitle,
                    NormalizedName = EntityNameNormalizer.NormalizeStrict(trackTitle), MixName = choice.Mix?.Trim(),
                    Isrc = isrc, CreatedUtc = now, UpdatedUtc = now,
                    ArtistCredits = artistId is null ? [] : [new() { ArtistId = artistId, CreditName = choice.NewArtist?.Trim(),
                        Role = "primary", Position = 1, Detail = credit }] }, null, ct);
            }
            if (recordingIds.Values.Contains(recordingId, StringComparer.Ordinal))
                throw Error(409, "pseudo_duplicate_recording", "Use the copy option when several files belong to one Recording.");
            recordingIds.Add(choice.MediaId, recordingId);
            var releaseVersion = (await foundation.GetMetadataAsync(CatalogEntityKind.Release, releaseId, ct))!.Version;
            await foundation.SavePlacementAsync(new(Guid.NewGuid().ToString("N"), releaseId, recordingId, "",
                ++placements, null, "manual", null, null, [], []), releaseVersion, ct);
        }
        return (releaseId, recordingIds, placements);
    }
}
