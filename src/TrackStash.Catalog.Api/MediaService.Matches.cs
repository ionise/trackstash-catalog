using System.Text.Json;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>One explicitly selected existing Recording and/or independent media classification.</summary>
public sealed record MediaMatchSelection(long MediaId, string? RecordingId, string? Classification);
/// <summary>A proposed match with historical tags, copy evidence and independent classification.</summary>
public sealed record MediaMatchRow(MediaInventoryItem Media, IReadOnlyList<MediaRecordingCandidate> Candidates,
    string? SuggestedRecordingId, string? LinkedRecordingId, bool AlreadyRegistered, string Classification);
/// <summary>Bounded read-only proposals, independent of any whole-group Release association.</summary>
public sealed record MediaMatchPlan(string Version, IReadOnlyList<MediaMatchRow> Files);
/// <summary>Frozen atomic link/classification batch; selected copies and supporting audio copies are verified.</summary>
public sealed record MediaMatchReview(string Id, string Version, MediaMatchPlan Plan, IReadOnlyList<MediaMatchSelection> Selections,
    IReadOnlyList<long> VerifiedMediaIds, DateTimeOffset CreatedAt, MediaMatchOutcome? Outcome = null);
/// <summary>Saved atomic matches and media-only classifications, with no new Catalog entities.</summary>
public sealed record MediaMatchOutcome(string ReviewId, IReadOnlyList<MediaLinkOutcome> Links,
    IReadOnlyList<MediaMatchSelection> Classifications, DateTimeOffset AppliedAt, long Revision = 0);

public sealed partial class MediaService
{
    private const string MatchReason = "User explicitly selected and accepted these media links/classifications.";
    private sealed record LinkedAudioAnchor(long MediaId, string RecordingId, string Sha256);
    private sealed record MatchState(MediaMatchPlan Plan, IReadOnlyList<RecordingState> Recordings,
        IReadOnlyList<MediaInventoryItem> Copies, IReadOnlyList<RegistrationState> Registrations,
        IReadOnlyList<LinkedAudioAnchor> Anchors);
    private sealed record MatchPayload(string Type, string SourceId, MediaMatchReview Review,
        IReadOnlyList<LinkPayload> Links, IReadOnlyList<VerifiedDependency> Dependencies);

    private static void MatchIds(IReadOnlyList<long> ids)
    {
        if (ids.Count is < 1 or > 100 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
            throw Error(400, "validation_failed", "Choose between 1 and 100 distinct scanner media identities.");
    }

    private async Task<Dictionary<long, string>> MediaClassifications(IUnitOfWork unit, CancellationToken ct)
    {
        var stored = await unit.Enrichment!.ListAppliedReviewsAsync("MediaMatches", ct);
        if (stored.Count > 1000)
            throw Error(409, "media_history_limit", "More than 1000 accepted media batches; paged history is required before further matching.");
        var values = new Dictionary<long, string>();
        var outcomes = new List<MediaMatchOutcome>();
        foreach (var review in stored)
        {
            var payload = JsonSerializer.Deserialize<MatchPayload>(review.PayloadJson)
                ?? throw new InvalidDataException("Invalid stored media batch.");
            if (payload.SourceId != SourceId) continue;
            var audit = await unit.Enrichment.GetAuditAsync(review.Id, ct)
                ?? throw new InvalidDataException("Missing media batch audit.");
            outcomes.Add(JsonSerializer.Deserialize<MediaMatchOutcome>(audit.AfterJson)
                ?? throw new InvalidDataException("Invalid media batch outcome."));
        }
        foreach (var outcome in outcomes.OrderBy(o => o.Revision))
            foreach (var selection in outcome.Classifications)
                values[selection.MediaId] = selection.Classification!;
        return values;
    }

    private static MediaRecordingCandidate MatchCandidate(MediaInventoryItem media, RecordingState recording,
        IEnumerable<MediaInventoryItem> copies, IEnumerable<LinkedAudioAnchor> anchors, Sidecar? sidecar = null)
    {
        var direct = Compare(WithExplicitTitleMix(media, recording), recording, sidecar);
        var linkedAudio = media.FingerprintKey is not null && copies.Any(f => f.Id != media.Id &&
            f.FingerprintKey == media.FingerprintKey && anchors.Any(a => a.MediaId == f.Id && a.RecordingId == recording.Recording.Id));
        if (linkedAudio && direct.Status != "Supported" &&
            !direct.Blockers.Any(b => b.StartsWith("Sidecar ", StringComparison.Ordinal)))
            return direct with { Status = "Needs manual review: matches linked audio", Blockers = [],
                Evidence = direct.Evidence.Concat([
                    "Exact stored audio fingerprint matches an already linked copy of this Recording. Choose this Recording explicitly to accept your audio-identity judgement despite incomplete or differing tags.",
                    .. direct.Blockers.Select(b => $"Metadata warning: {b}")
                ]).ToArray() };
        if (direct.Status == "Supported" || media.FingerprintKey is null ||
            direct.Blockers.Any(b => b != "Insufficient independent tagged metadata to accept a link."))
            return direct;
        var family = copies.Where(f => f.Id != media.Id && f.FingerprintKey == media.FingerprintKey).ToArray();
        if (family.Length == 0) return direct;
        var conflicts = family.SelectMany(f => Compare(WithExplicitTitleMix(f, recording), recording).Blockers)
            .Where(b => b != "Insufficient independent tagged metadata to accept a link.").Distinct().ToArray();
        var isrcs = family.Append(media).Where(f => Isrc(f.Isrc)).Select(f => f.Isrc!.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (conflicts.Length > 0 || isrcs.Length > 1)
            return direct with { Status = "Blocked", Blockers = ["Exact fingerprint copies have conflicting identity tags; reconcile these versions individually."] };
        string? Fill(string? value, Func<MediaInventoryItem, string?> field) =>
            !string.IsNullOrWhiteSpace(value) ? value : family.Select(field).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var title = recording.Recording.Title ?? recording.Recording.Name;
        var mix = recording.Recording.MixName;
        var explicitMix = !string.IsNullOrWhiteSpace(mix) && family.Any(f =>
            Same(f.Title, $"{title} ({mix})") || Same(f.Title, $"{title} {mix}")) ? mix : null;
        var composite = media with {
            Title = Fill(media.Title, f => f.Title), Mix = Fill(media.Mix, f => f.Mix) ?? explicitMix,
            Artist = Fill(media.Artist, f => f.Artist), Isrc = Fill(media.Isrc, f => f.Isrc),
            DurationSeconds = media.DurationSeconds ?? family.Select(f => f.DurationSeconds).FirstOrDefault(d => d is not null)
        };
        var merged = Compare(WithExplicitTitleMix(composite, recording), recording, sidecar);
        return merged.Blockers.Count == 0 ? merged with {
            Evidence = merged.Evidence.Concat(["Composite tags from exact stored audio fingerprint copies; every supporting copy is verified before acceptance. Duplicate tags are not independent votes."]).ToArray()
        } : direct;
    }

    private async Task<MatchState> MatchStateAsync(IUnitOfWork unit, IReadOnlyList<long> mediaIds, CancellationToken ct)
    {
        MatchIds(mediaIds);
        var inventory = await Inventory(ct);
        var selected = mediaIds.Order().Select(id => inventory.SingleOrDefault(f => f.Id == id)
            ?? throw Error(404, "media_not_found", "A selected scanner file is no longer in an approved library.")).ToArray();
        var fingerprints = selected.Where(f => f.FingerprintKey is not null).Select(f => f.FingerprintKey!).ToHashSet(StringComparer.Ordinal);
        var copies = inventory.Where(f => mediaIds.Contains(f.Id) || f.FingerprintKey is not null && fingerprints.Contains(f.FingerprintKey))
            .OrderBy(f => f.Id).ToArray();
        if (copies.Length > 1000)
            throw Error(409, "media_copy_limit", "These proposals involve more than 1000 fingerprint copies; narrow the selected inventory page.");
        var registrations = new List<RegistrationState>();
        var anchors = new List<LinkedAudioAnchor>();
        foreach (var file in copies)
        {
            var match = await unit.Matches.GetBestMatchByMediaFileIdAsync(MediaId(file.Id), ct);
            var registered = await unit.MediaFiles.GetByIdAsync(MediaId(file.Id), ct);
            registrations.Add(new(file.Id, match?.RecordingId, registered is not null ||
                await unit.MediaFiles.GetByPathAsync(file.Path, ct) is not null));
            if (match?.RecordingId is not null && match.OverrideState is MatchOverrideState.Accepted or MatchOverrideState.Manual &&
                registered?.MetadataJson is not null && registered.ContentHash is { Length: 64 } hash)
            {
                MediaInventoryItem? snapshot;
                try { snapshot = JsonSerializer.Deserialize<MediaInventoryItem>(registered.MetadataJson); }
                catch (JsonException) { throw Error(409, "media_evidence_invalid", "Stored linked-copy audio evidence is malformed."); }
                if (snapshot?.Path == file.Path && snapshot.FingerprintKey is not null && snapshot.FingerprintKey == file.FingerprintKey)
                    anchors.Add(new(file.Id, match.RecordingId, hash));
            }
        }
        var classifications = await MediaClassifications(unit, ct);
        var searches = new Dictionary<string, (string[] Ids, bool Truncated)>(StringComparer.OrdinalIgnoreCase);
        var recordings = new Dictionary<string, RecordingState>(StringComparer.Ordinal);
        var rows = new List<MediaMatchRow>();
        foreach (var media in selected)
        {
            ct.ThrowIfCancellationRequested();
            var family = copies.Where(f => f.Id == media.Id ||
                media.FingerprintKey is not null && f.FingerprintKey == media.FingerprintKey).ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var truncated = false;
            foreach (var copy in family)
            {
                if (Isrc(copy.Isrc) && await unit.Recordings.GetByIsrcAsync(copy.Isrc!.Trim().ToUpperInvariant(), ct) is { } exact)
                    ids.Add(exact.Id);
                var registered = registrations.Single(r => r.MediaId == copy.Id);
                if (registered.RecordingId is not null) ids.Add(registered.RecordingId);
                if (string.IsNullOrWhiteSpace(copy.Title)) continue;
                var suffix = copy.Title.LastIndexOf(" (", StringComparison.Ordinal);
                var title = suffix > 0 && copy.Title.EndsWith(')') ? copy.Title[..suffix] : copy.Title;
                var search = string.Join(" ", title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
                if (!searches.TryGetValue(search, out var found))
                {
                    var page = await unit.Recordings.QueryTracksAsync(new TrackQuery(Search: search, GroupByRecording: true, PageSize: 100), ct);
                    searches[search] = found = (page.Items.Select(t => t.RecordingId).Distinct().ToArray(), page.Total > page.Items.Count);
                }
                truncated |= found.Truncated;
                foreach (var id in found.Ids) ids.Add(id);
            }
            var candidates = new List<MediaRecordingCandidate>();
            foreach (var id in ids.Order(StringComparer.Ordinal))
            {
                if (!recordings.TryGetValue(id, out var recording))
                    recordings[id] = recording = await ReadRecording(unit, id, ct);
                var supporting = family.Where(f => registrations.Any(r => r.MediaId == f.Id &&
                    (r.RecordingId == id || r.RecordingId is null && !r.Registered)));
                var candidate = MatchCandidate(media, recording, supporting, anchors);
                if (truncated) candidate = candidate with { Status = "Blocked",
                    Blockers = candidate.Blockers.Concat(["Candidate search is incomplete; use Release reconciliation to resolve this identity."]).ToArray() };
                candidates.Add(candidate);
            }
            var registration = registrations.Single(r => r.MediaId == media.Id);
            var supported = candidates.Where(c => c.Status == "Supported").ToArray();
            rows.Add(new(media, candidates.OrderBy(c => c.Blockers.Count).ThenBy(c => c.RecordingId, StringComparer.Ordinal).ToArray(),
                !registration.Registered && registration.RecordingId is null && supported.Length == 1 ? supported[0].RecordingId : null,
                registration.RecordingId, registration.Registered, classifications.GetValueOrDefault(media.Id, "unclassified")));
        }
        var states = recordings.Values.OrderBy(r => r.Recording.Id, StringComparer.Ordinal).ToArray();
        var plan = new MediaMatchPlan(Hash(new { SourceId, Rows = rows, Recordings = states, Copies = copies, Registrations = registrations, Anchors = anchors }), rows);
        return new(plan, states, copies, registrations, anchors);
    }

    public async Task<MediaMatchPlan> MatchPlanAsync(IReadOnlyList<long> mediaIds, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        return (await MatchStateAsync(unit, mediaIds, ct)).Plan;
    }

    public async Task<MediaMatchReview> PrepareMatchesAsync(IReadOnlyList<long> mediaIds,
        IReadOnlyList<MediaMatchSelection> selections, string version, CancellationToken ct)
    {
        await Ready(ct);
        if (selections.Count is < 1 or > 100 || selections.Select(s => s.MediaId).Distinct().Count() != selections.Count ||
            selections.Any(s => !mediaIds.Contains(s.MediaId) || s.RecordingId is null && s.Classification is null ||
                s.Classification is not (null or "unclassified" or "unofficial") || s.RecordingId is { Length: 0 or > 200 }))
            throw Error(400, "validation_failed", "Select distinct proposed files, existing Recordings and/or an independent media classification.");
        MatchState state;
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
            state = await MatchStateAsync(read, mediaIds, ct);
        if (state.Plan.Version != version) throw Error(412, "enrichment_state_changed", "Media proposals changed; reload the worklist.");
        var dependencies = new Dictionary<long, VerifiedDependency>();
        var modified = new Dictionary<long, DateTime>();
        var sidecars = new Dictionary<long, Sidecar?>();
        async Task<Sidecar?> VerifyCopy(MediaInventoryItem file)
        {
            if (dependencies.ContainsKey(file.Id)) return sidecars[file.Id];
            var verified = await Verify(file, ct, state.Anchors.FirstOrDefault(a => a.MediaId == file.Id)?.Sha256);
            var sidecar = await ReadSidecar(file, ct);
            dependencies[file.Id] = new(file, sidecar?.Hash, verified.Hash);
            modified[file.Id] = verified.Modified;
            sidecars[file.Id] = sidecar;
            return sidecar;
        }
        var links = new List<LinkPayload>();
        foreach (var selection in selections.OrderBy(s => s.MediaId))
        {
            if (selection.RecordingId is null) continue;
            var row = state.Plan.Files.Single(f => f.Media.Id == selection.MediaId);
            if (row.LinkedRecordingId is not null || row.AlreadyRegistered)
                throw Error(409, "media_already_linked", "Existing media registrations cannot be overwritten.");
            if (!row.Candidates.Any(c => c.RecordingId == selection.RecordingId && c.Blockers.Count == 0))
                throw Error(409, "media_identity_conflict", "Choose an eligible proposed Recording; conflicts cannot be overridden.");
            var recording = state.Recordings.Single(r => r.Recording.Id == selection.RecordingId);
            var supporting = state.Copies.Where(f => state.Registrations.Any(r => r.MediaId == f.Id &&
                (r.RecordingId == selection.RecordingId || r.RecordingId is null && !r.Registered))).ToArray();
            var sidecar = await VerifyCopy(row.Media);
            var candidate = MatchCandidate(row.Media, recording, supporting, state.Anchors, sidecar);
            if (candidate.Blockers.Count > 0)
                throw Error(409, "media_identity_conflict", $"Media {row.Media.Id}: {string.Join(" ", candidate.Blockers)}");
            if (candidate.Status == "Needs manual review: matches linked audio")
            {
                var anchor = supporting.First(f => f.Id != row.Media.Id && f.FingerprintKey == row.Media.FingerprintKey &&
                    state.Anchors.Any(a => a.MediaId == f.Id && a.RecordingId == selection.RecordingId));
                await VerifyCopy(anchor);
            }
            if (candidate.Evidence.Any(e => e.StartsWith("Composite tags", StringComparison.Ordinal)))
            {
                foreach (var copy in supporting.Where(f => f.Id != row.Media.Id && row.Media.FingerprintKey is not null &&
                    f.FingerprintKey == row.Media.FingerprintKey))
                {
                    var comparison = Compare(WithExplicitTitleMix(copy, recording), recording, await VerifyCopy(copy));
                    if (comparison.Blockers.Any(b => b != "Insufficient independent tagged metadata to accept a link."))
                        throw Error(409, "media_identity_conflict", "Composite fingerprint support conflicts with a supporting copy's sidecar.");
                }
            }
            var linkReview = new MediaLinkReview(Guid.NewGuid().ToString("N"), "", row.Media, candidate, clock.GetUtcNow(),
                dependencies[row.Media.Id].VerifiedSha256!, sidecar?.Title, sidecar?.Mix);
            linkReview = linkReview with { Version = Hash(new LinkPayload("MediaLink", SourceId, linkReview, recording, sidecar?.Hash)) };
            links.Add(new("MediaLink", SourceId, linkReview, recording, sidecar?.Hash));
        }
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if ((await MatchStateAsync(unit, mediaIds, ct)).Plan.Version != version)
            throw Error(412, "enrichment_state_changed", "Media or Catalog evidence changed during verification; no batch was saved.");
        var review = new MediaMatchReview(Guid.NewGuid().ToString("N"), "", state.Plan,
            selections.OrderBy(s => s.MediaId).ToArray(), dependencies.Keys.Order().ToArray(), clock.GetUtcNow());
        var payload = new MatchPayload("MediaMatches", SourceId, review, links, dependencies.Values.ToArray());
        review = review with { Version = Hash(payload) };
        await unit.Enrichment!.SaveReviewAsync(new(review.Id, CatalogEntityKind.Recording, links.FirstOrDefault()?.Recording.Recording.Id ?? MediaId(selections[0].MediaId),
            review.Version, Json(payload with { Review = review }), review.CreatedAt), ct);
        await CheckDependencies(payload.Dependencies, modified, ct);
        await unit.CommitAsync(ct);
        return review;
    }

    private static async Task<MatchPayload> LoadMatches(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await unit.Enrichment!.GetReviewAsync(id, ct) ?? throw Error(404, "media_review_not_found", "Media batch was not found.");
        using var json = JsonDocument.Parse(stored.PayloadJson);
        if (!json.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "MediaMatches")
            throw Error(400, "media_review_invalid", "Use a media matching batch review.");
        return JsonSerializer.Deserialize<MatchPayload>(stored.PayloadJson) ?? throw new InvalidDataException("Invalid media batch.");
    }

    public async Task<MediaMatchReview> GetMatchesAsync(string id, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var payload = await LoadMatches(unit, id, ct);
        var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
        return payload.Review with { Outcome = audit is null ? null : JsonSerializer.Deserialize<MediaMatchOutcome>(audit.AfterJson) };
    }

    public async Task<MediaMatchOutcome> ApplyMatchesAsync(string id, string version, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) throw Error(400, "validation_failed", "Explicitly accept the selected media links/classifications.");
        await Ready(ct);
        MatchPayload payload;
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
        {
            payload = await LoadMatches(read, id, ct);
            if (payload.Review.Version != version) throw Error(412, "enrichment_state_changed", "Use the original media batch ETag.");
            if (await read.Enrichment!.GetAuditAsync(id, ct) is { } saved)
                return JsonSerializer.Deserialize<MediaMatchOutcome>(saved.AfterJson)!;
        }
        if (clock.GetUtcNow() - payload.Review.CreatedAt >= TimeSpan.FromHours(24))
            throw Error(410, "enrichment_review_expired", "Media batch expired; reload proposals.");
        if (payload.SourceId != SourceId) throw Error(412, "enrichment_state_changed", "Scanner identity changed.");
        var modified = new Dictionary<long, DateTime>();
        foreach (var dependency in payload.Dependencies)
            modified[dependency.Media.Id] = (await Verify(dependency.Media, ct, dependency.VerifiedSha256)).Modified;
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if (await unit.Enrichment!.GetAuditAsync(id, ct) is { } completed)
            return JsonSerializer.Deserialize<MediaMatchOutcome>(completed.AfterJson)!;
        var history = await unit.Enrichment.ListAppliedReviewsAsync("MediaMatches", ct);
        if (history.Count >= 1000)
            throw Error(409, "media_history_limit", "Matching history reached 1000 accepted batches; paged history support is required before accepting another batch.");
        var ids = payload.Review.Plan.Files.Select(f => f.Media.Id).ToArray();
        if ((await MatchStateAsync(unit, ids, ct)).Plan.Version != payload.Review.Plan.Version)
            throw Error(412, "enrichment_state_changed", "Media, Catalog or classification evidence changed; nothing was saved.");
        await CheckDependencies(payload.Dependencies, modified, ct);
        var outcomes = new List<MediaLinkOutcome>();
        foreach (var link in payload.Links)
        {
            await unit.Enrichment.SaveReviewAsync(new(link.Review.Id, CatalogEntityKind.Recording, link.Review.Candidate.RecordingId,
                link.Review.Version, Json(link), link.Review.CreatedAt), ct);
            outcomes.Add(await SaveLink(unit, link, MatchReason, ct));
        }
        var revision = history.Count + 1L;
        var outcome = new MediaMatchOutcome(id, outcomes, payload.Review.Selections.Where(s => s.Classification is not null).ToArray(), clock.GetUtcNow(), revision);
        await unit.Enrichment.SaveAuditAsync(new(id, Json(new[] { "MediaMatches" }), MatchReason, Json(payload), Json(outcome), outcome.AppliedAt), ct);
        await CheckDependencies(payload.Dependencies, modified, ct);
        var current = await Inventory(ct);
        if (payload.Review.Plan.Files.Any(f => Json(current.SingleOrDefault(m => m.Id == f.Media.Id)) != Json(f.Media)) ||
            payload.Dependencies.Any(d => Json(current.SingleOrDefault(m => m.Id == d.Media.Id)) != Json(d.Media)))
            throw Error(412, "enrichment_state_changed", "Scanner evidence changed during acceptance; nothing was saved.");
        await unit.CommitAsync(ct);
        return outcome;
    }
}
