using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public interface IReleaseImportService
{
    ValueTask<StoredReleaseImport> PreviewAsync(long releaseId, string? basePreviewId, ReleaseImportDecision? decision, CancellationToken cancellationToken = default);
    ValueTask<StoredReleaseImport> GetAsync(string operationId, CancellationToken cancellationToken = default);
    ValueTask<ReleaseImportOutcome> ApplyAsync(string operationId, string expectedVersion, string reason, bool confirmed, CancellationToken cancellationToken = default);
    /// <summary>Builds a fresh review with all <paramref name="decisions"/>; when <paramref name="persist"/> is false nothing is stored.</summary>
    ValueTask<ReleaseImportPlan> PlanAsync(long releaseId, IReadOnlyList<ReleaseImportDecision> decisions, bool persist, CancellationToken cancellationToken = default);
}

public sealed class ReleaseImportService(IStorageProvider storage, IPurchaseReleaseSource source, TimeProvider clock) : IReleaseImportService
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Normalize(string? name) => EntityNameNormalizer.NormalizeStrict(name ?? "");
    private static bool Compound(string name) => name.Contains(',') || name.Contains(" & ", StringComparison.Ordinal);
    private static IReleaseImportRepository Imports(IUnitOfWork unit) => unit.ReleaseImports
        ?? throw new ReleaseImportException(503, "import_unavailable", "Storage does not support reviewed imports.");
    private async Task RequireSchema(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 10)
            throw new ReleaseImportException(409, "import_schema_required", "Reviewed imports require schema 10. Keep using the read-only inspector until a separately approved migration.");
    }

    private async ValueTask<PurchaseReleaseInspection> Bundle(long id, CancellationToken ct)
    {
        var first = await source.InspectAsync(id, new(PageSize: 100) { AllowCachedRelease = true }, ct).ConfigureAwait(false)
            ?? throw new ReleaseImportException(404, "purchase_candidate_not_found", "Release is not in the configured corpus or cached catalog source. Fetch the Beatport release for admission first.");
        var tracks = first.Tracks.Items.ToList();
        for (var page = 2; tracks.Count < first.Tracks.Total; page++)
        {
            var next = await source.InspectAsync(id, new(Page: page, PageSize: 100) { AllowCachedRelease = true }, ct).ConfigureAwait(false);
            if (next is null || next.BundleDigest != first.BundleDigest || next.Tracks.Total != first.Tracks.Total || next.Tracks.Items.Count == 0)
                throw new ReleaseImportException(412, "import_source_changed", "Source changed while assembling the bundle. Inspect again.");
            tracks.AddRange(next.Tracks.Items);
        }
        return first with { Tracks = new(tracks, tracks.Count, 1, Math.Max(1, tracks.Count)) };
    }
    public async ValueTask<StoredReleaseImport> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await RequireSchema(cancellationToken).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        return await Imports(unit).GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new ReleaseImportException(404, "import_not_found", "Import review was not found.");
    }
    public async ValueTask<StoredReleaseImport> PreviewAsync(long releaseId, string? basePreviewId, ReleaseImportDecision? decision, CancellationToken cancellationToken = default)
    {
        if (releaseId <= 0) throw new ReleaseImportException(400, "validation_failed", "Release ID must be positive.");
        await RequireSchema(cancellationToken).ConfigureAwait(false);
        ReleaseImportPlan? previous = null;
        if (basePreviewId is not null)
        {
            var stored = await GetAsync(basePreviewId, cancellationToken).ConfigureAwait(false);
            previous = stored.Plan;
            if (previous.Source.ReleaseId != releaseId || stored.Outcome is not null || stored.SupersededBy is not null || previous.ExpiresUtc <= clock.GetUtcNow())
                throw new ReleaseImportException(409, "import_base_invalid", "Use an unapplied, unexpired review of this release.");
        }
        if (decision is not null)
        {
            if (string.IsNullOrWhiteSpace(decision.Evidence)) decision = decision with { Evidence = DefaultEvidence };
            ValidateDecision(decision);
        }
        var decisions = previous?.Decisions.ToList() ?? [];
        if (decision is not null)
        {
            decisions.RemoveAll(d => d.Kind == decision.Kind && d.ProviderId == decision.ProviderId);
            decisions.Add(decision);
        }
        return new(await BuildAsync(releaseId, previous, decisions, persist: true, cancellationToken).ConfigureAwait(false), null);
    }
    public async ValueTask<ReleaseImportPlan> PlanAsync(long releaseId, IReadOnlyList<ReleaseImportDecision> decisions, bool persist, CancellationToken cancellationToken = default)
    {
        if (releaseId <= 0) throw new ReleaseImportException(400, "validation_failed", "Release ID must be positive.");
        await RequireSchema(cancellationToken).ConfigureAwait(false);
        var list = decisions.Select(d => string.IsNullOrWhiteSpace(d.Evidence) ? d with { Evidence = DefaultEvidence } : d).ToList();
        list.ForEach(ValidateDecision);
        if (list.GroupBy(d => (d.Kind, d.ProviderId)).Any(g => g.Count() > 1))
            throw new ReleaseImportException(400, "validation_failed", "Each identity may have only one decision.");
        return await BuildAsync(releaseId, null, list, persist, cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<ReleaseImportPlan> BuildAsync(long releaseId, ReleaseImportPlan? previous, List<ReleaseImportDecision> decisions, bool persist,
        CancellationToken cancellationToken)
    {
        var bundle = await Bundle(releaseId, cancellationToken).ConfigureAwait(false);
        var fingerprint = Fingerprint(bundle);
        if (previous is not null && previous.Source.BundleDigest != bundle.BundleDigest)
            throw new ReleaseImportException(412, "import_source_changed", "Source changed; start a fresh preview and review decisions again.");
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var imports = Imports(unit);
        var foundations = unit.CatalogFoundations!;
        var stamp = await imports.GetCatalogStampAsync(cancellationToken).ConfigureAwait(false);
        var blockers = bundle.Diagnostics.Where(d => d.Severity == "blocker").Select(d => d.Message).Distinct().ToList();
        if (!bundle.SourceComplete) blockers.Add("Complete cached source coverage is required.");
        var warnings = bundle.Diagnostics.Where(d => d.Severity == "warning").Select(d => d.Message).Distinct().ToList();
        var identities = new List<ReleaseImportIdentity>();
        var artistWrites = new List<ReleaseImportArtistWrite>();
        var recordingWrites = new List<ReleaseImportRecordingWrite>();
        var placements = new List<ReleasePlacement>();
        var labelId = "";
        if (bundle.Label is not null)
        {
            var labels = await unit.Labels.QueryAsync(new LabelQuery { Source = "beatport", ExternalId = Id(bundle.Label.ProviderId), PageSize = 100 }, cancellationToken).ConfigureAwait(false);
            if (labels.Total == 1) labelId = labels.Items[0].Id;
            else blockers.Add("Resolve the source label to exactly one active catalog label using label maintenance before import.");
        }
        var releaseOwners = await foundations.FindReferenceOwnersAsync(CatalogEntityKind.Release, "beatport", Id(releaseId), cancellationToken).ConfigureAwait(false);
        Release? existing = releaseOwners.Count == 1 ? await unit.Releases.GetByIdAsync(releaseOwners[0], cancellationToken).ConfigureAwait(false) : null;
        var binding = await imports.GetBindingAsync("beatport", Id(releaseId), cancellationToken).ConfigureAwait(false);
        var noOp = existing is not null && binding?.ReleaseId == existing.Id && binding.SourceFingerprint == fingerprint;
        if (releaseOwners.Count > 1) blockers.Add("Multiple Releases own the provider release ID. Resolve identity ambiguity first.");
        if (existing is not null && !noOp)
            blockers.Add("This release already exists with different or unreviewed source state. Graph reconciliation is not available; no existing metadata or manual order will be replaced.");
        // A same-title Release already bound to a different Beatport release is a distinct edition, not a candidate.
        var releaseCandidates = existing is null ? (await imports.FindCandidatesAsync(CatalogEntityKind.Release, Normalize(bundle.Title), cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(c => c.BeatportIds is not { Count: > 0 }).ToList() : [];
        if (releaseCandidates.Count > 100) blockers.Add("More than 100 Release candidates match. Resolve canonical identity before importing.");
        if (releaseCandidates.Count > 0 && !decisions.Any(d => d.Kind == "release" && d.ProviderId == releaseId && d.Action == "create"))
            blockers.Add("Review same-title Release candidates before choosing to create a distinct source edition.");
        if (decisions.Any(d => d.Kind == "release" && d.ProviderId != releaseId))
            throw new ReleaseImportException(400, "validation_failed", "Release decision does not belong to this bundle.");
        var proposed = existing ?? new Release
        {
            Id = NewId(), Title = bundle.Title, Name = bundle.Title, NormalizedName = Normalize(bundle.Title),
            CatalogNumber = bundle.CatalogNumber, Upc = bundle.Upc, PublishDate = bundle.PublishDate, NewReleaseDate = bundle.NewReleaseDate,
            CreatedUtc = clock.GetUtcNow(), UpdatedUtc = clock.GetUtcNow(), ExternalReferences = [new() { Source = "beatport", ExternalId = Id(releaseId), IsPrimary = true }],
            LabelLinks = labelId.Length == 0 ? [] : [new() { LabelId = labelId, IsPrimary = true, Role = "issuing" }],
        };
        identities.Add(new("release", releaseId, bundle.Title ?? "Unknown release", noOp ? "unchanged" : existing is null ? "create" : "blocked",
            proposed.Id, false, releaseCandidates));
        var sourceArtists = bundle.Credits.Concat(bundle.Tracks.Items.SelectMany(t => t.Credits)).GroupBy(c => c.ProviderId).ToArray();
        var relevant = sourceArtists.Select(a => ("artist", a.Key)).Concat(bundle.Tracks.Items.Where(t => t.ProviderId is not null).Select(t => ("recording", t.ProviderId!.Value)))
            .Append(("release", releaseId)).ToHashSet();
        if (decisions.Any(d => !relevant.Contains((d.Kind, d.ProviderId))))
            throw new ReleaseImportException(400, "validation_failed", "Decision does not belong to this source bundle.");
        var artistIds = new Dictionary<long, string>();
        if (!noOp)
        {
            foreach (var group in sourceArtists)
            {
                var name = group.First().Name;
                if (group.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != 1)
                    blockers.Add($"Artist {group.Key} has contradictory names within the source bundle.");
                var owners = await foundations.FindReferenceOwnersAsync(CatalogEntityKind.Artist, "beatport", Id(group.Key), cancellationToken).ConfigureAwait(false);
                var allCandidates = await imports.FindCandidatesAsync(CatalogEntityKind.Artist, Normalize(name), cancellationToken: cancellationToken).ConfigureAwait(false);
                // Same-name Artists bound only to other Beatport artist IDs are different acts.
                var candidates = allCandidates.Where(c => c.BeatportIds is not { Count: > 0 } || c.BeatportIds.Contains(Id(group.Key))).ToList();
                if (allCandidates.Count > 100) blockers.Add($"Artist {group.Key}: more than 100 matches; resolve identity before importing.");
                var choice = decisions.SingleOrDefault(d => d.Kind == "artist" && d.ProviderId == group.Key);
                Artist? before = owners.Count == 1 ? await unit.Artists.GetByIdAsync(owners[0], cancellationToken).ConfigureAwait(false) : null;
                if (owners.Count > 1) blockers.Add($"Artist {group.Key} has multiple provider-reference owners.");
                if (choice?.Action == "reuse")
                {
                    if (owners.Count != 0 && (owners.Count != 1 || owners[0] != choice.CanonicalId))
                        blockers.Add($"Artist {group.Key}: decision conflicts with existing provider ownership.");
                    before = await unit.Artists.GetByIdAsync(choice.CanonicalId!, cancellationToken).ConfigureAwait(false);
                    if (before is null) blockers.Add($"Artist {group.Key}: selected identity does not exist.");
                }
                if (owners.Count > 0 && choice?.Action == "create")
                    blockers.Add($"Artist {group.Key}: provider identity is already owned; cannot create another.");
                var compound = Compound(name);
                var action = before is not null ? "reuse" : choice?.Action ?? "undecided";
                if (before is null && action == "undecided") blockers.Add($"Artist {group.Key}: explicitly accept a new identity, unresolved compound, or evidenced reuse.");
                if (compound && before is null && choice is null) blockers.Add($"Artist {group.Key}: compound expression needs explicit review.");
                if (compound && before?.IdentityStatus == "unknown" && choice is null)
                    blockers.Add($"Artist {group.Key}: {UnreviewedCompoundBlocker}; choose evidenced reuse or unresolved status.");
                if (before is not null && before.Name != name)
                    warnings.Add($"Artist {group.Key}: keep curated name '{before.Name}' and retain source credit '{name}'.");
                var after = before ?? new Artist { Id = NewId(), Name = name, NormalizedName = Normalize(name),
                    IdentityStatus = action == "unresolved" ? "unresolved_compound" : "accepted", Kind = "unknown",
                    CreatedUtc = clock.GetUtcNow(), UpdatedUtc = clock.GetUtcNow() };
                var priorWrite = artistWrites.SingleOrDefault(w => w.After.Id == after.Id);
                if (priorWrite is not null) after = priorWrite.After;
                var identityStatus = choice?.Action == "unresolved" ? "unresolved_compound" :
                    compound && before?.IdentityStatus == "unknown" && choice?.Action == "reuse" ? "accepted" : after.IdentityStatus;
                after = after with { IdentityStatus = identityStatus };
                artistIds[group.Key] = after.Id;
                var hasReference = after.ExternalReferences.Any(r => r.Source == "beatport" && r.ExternalId == Id(group.Key));
                if (!hasReference)
                    after = after with { ExternalReferences = after.ExternalReferences.Append(new EntityReference { Source = "beatport", ExternalId = Id(group.Key) }).ToArray() };
                if (before is null || !hasReference || before.IdentityStatus != after.IdentityStatus)
                {
                    if (priorWrite is not null) artistWrites.Remove(priorWrite);
                    artistWrites.Add(new(priorWrite?.Before ?? before, after));
                }
                identities.Add(new("artist", group.Key, name, action, after.Id, compound, candidates));
            }
            var sharedIsrcs = bundle.Tracks.Items.Where(t => t.Isrc is not null).GroupBy(t => t.Isrc!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            var sharedVersions = bundle.Tracks.Items.GroupBy(t => (Normalize(t.Title), t.MixName))
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            foreach (var track in bundle.Tracks.Items.Where(t => t.ProviderId is not null && t.Status == "cached"))
            {
                var trackId = track.ProviderId!.Value;
                if (track.Isrc is not null && sharedIsrcs.Contains(track.Isrc) || sharedVersions.Contains((Normalize(track.Title), track.MixName)))
                    warnings.Add($"Track {trackId}: {SharedListingWarning} ISRC or title/version. Review master identity; distinct listings are not automatically merged.");
                var choice = decisions.SingleOrDefault(d => d.Kind == "recording" && d.ProviderId == trackId);
                var legacyOwners = await foundations.FindReferenceOwnersAsync(CatalogEntityKind.Recording, "beatport", Id(trackId), cancellationToken).ConfigureAwait(false);
                var listingOwners = await imports.FindPlacementOwnersAsync("beatport", Id(trackId), cancellationToken).ConfigureAwait(false);
                if (listingOwners.Count > 0) blockers.Add($"Track {trackId} already has a placement binding. Resolve release identity before importing.");
                var allCandidates = await imports.FindCandidatesAsync(CatalogEntityKind.Recording, Normalize(track.Title), track.Isrc, cancellationToken).ConfigureAwait(false);
                // Keep only Recordings this track could reuse: same mix, no contradictory ISRC, and either the same ISRC
                // (compilations share masters) or no Beatport listing yet. Others are distinct versions or listings.
                var candidates = allCandidates.Where(c => c.BeatportIds?.Contains(Id(trackId)) == true ||
                    c.MixName == track.MixName && !(c.Isrc is not null && track.Isrc is not null && c.Isrc != track.Isrc) &&
                    (c.Isrc is not null && c.Isrc == track.Isrc || c.BeatportIds is not { Count: > 0 })).ToList();
                if (allCandidates.Count > 100) blockers.Add($"Track {trackId}: more than 100 matches; resolve identity before importing.");
                foreach (var owner in legacyOwners.Where(id => candidates.All(c => c.Id != id)))
                {
                    var candidate = (await unit.Recordings.GetByIdAsync(owner, cancellationToken).ConfigureAwait(false))!;
                    candidates.Add(new(candidate.Id, candidate.Title, candidate.MixName, candidate.Isrc, [Id(trackId)]));
                }
                Recording? before = null;
                if (choice?.Action == "reuse")
                {
                    before = await unit.Recordings.GetByIdAsync(choice.CanonicalId!, cancellationToken).ConfigureAwait(false);
                    if (before is null) blockers.Add($"Track {trackId}: selected Recording does not exist.");
                    else if (before.MixName != track.MixName || before.Isrc is not null && track.Isrc is not null && before.Isrc != track.Isrc)
                        blockers.Add($"Track {trackId}: chosen Recording has contradictory mix/ISRC. Versions must stay distinct.");
                }
                if (legacyOwners.Count > 1 || legacyOwners.Count == 1 && (before?.Id != legacyOwners[0]))
                    blockers.Add($"Track {trackId}: legacy Recording reference requires an evidenced reuse decision; it will not be reassigned.");
                var action = before is not null ? "reuse" : choice?.Action ?? "undecided";
                if (action == "undecided") blockers.Add($"Track {trackId}: accept a new Recording or evidenced reuse.");
                var trackCredits = track.Credits.Select(c => Credit(c, artistIds, "track")).ToArray();
                var after = before ?? new Recording
                {
                    Id = NewId(), Name = track.Title, Title = track.Title, NormalizedName = Normalize(track.Title),
                    MixName = track.MixName, Isrc = track.Isrc, DurationMs = track.DurationMs, CreatedUtc = clock.GetUtcNow(), UpdatedUtc = clock.GetUtcNow(),
                    ArtistCredits = trackCredits.Select(c => new RecordingArtistCredit { ArtistId = c.ArtistId, CreditName = c.CreditedName, Role = c.Role, Position = c.Position,
                        Detail = c with { Id = NewId() } }).ToArray(),
                };
                recordingWrites.Add(new(before, after));
                placements.Add(new(NewId(), proposed.Id, after.Id, "", track.SourcePosition, track.SourcePosition, "source", null, null, trackCredits,
                    [new() { Source = "beatport", ExternalId = Id(trackId), IsPrimary = true, LastSeenUtc = track.RetrievedUtc }]));
                identities.Add(new("recording", trackId, track.Title!, action, after.Id, false, candidates, track.MixName, track.Isrc));
            }
            if (existing is null)
                proposed = proposed with { ArtistCredits = bundle.Credits.Select(c =>
                {
                    var detail = Credit(c, artistIds, "release");
                    return new ReleaseArtistCredit { ArtistId = detail.ArtistId, CreditName = detail.CreditedName, Position = detail.Position, Detail = detail };
                }).ToArray() };
        }
        else warnings.Add("Previously accepted source is unchanged. Apply will record a no-op outcome without rewriting catalog metadata, credits, manual order or embedding work.");
        if (bundle.PublishDate is not null && !ValidDate(bundle.PublishDate) || bundle.NewReleaseDate is not null && !ValidDate(bundle.NewReleaseDate))
            blockers.Add("Source date is invalid; no date inference or silent substitution is allowed.");
        if (bundle.Tracks.Items.Any(t => t.DurationMs is < 0)) blockers.Add("Source duration is invalid.");
        warnings.Add("No media, purchases, file matches or backups will be created. Vector generation is deferred; accepted text documents and durable pending work are stored.");
        var now = clock.GetUtcNow();
        var plan = new ReleaseImportPlan(1, NewId(), "", now, now.AddMinutes(15), previous?.OperationId, stamp, fingerprint, bundle, labelId, existing, proposed,
            decisions.OrderBy(d => d.Kind, StringComparer.Ordinal).ThenBy(d => d.ProviderId).ToArray(), identities, artistWrites, recordingWrites,
            placements, blockers.Distinct().ToArray(), warnings.Distinct().ToArray(), noOp);
        plan = plan with { Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan)))).ToLowerInvariant() };
        if (!persist) return plan;
        await imports.SavePreviewAsync(plan, cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }
    private static bool ValidDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static string Fingerprint(PurchaseReleaseInspection bundle) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            bundle.ReleaseId, bundle.Title, bundle.Label, bundle.CatalogNumber, bundle.Upc, bundle.PublishDate, bundle.NewReleaseDate,
            bundle.SourceComplete, bundle.DeclaredTrackCount, bundle.Credits,
            Tracks = bundle.Tracks.Items.Select(t => new { t.SourcePosition, t.ProviderId, t.Status, t.Title, t.MixName, t.Isrc, t.DurationMs, t.Credits }),
        })))).ToLowerInvariant();
    private static CatalogCredit Credit(SourceArtistCredit c, IReadOnlyDictionary<long, string> artists, string scope) =>
        new(NewId(), artists[c.ProviderId], c.Role, c.Position, c.Name, "beatport",
            $"{scope}.{(c.Role == "primary" ? "artists" : "remixers")}[{c.Position - 1}]", c.Name,
            JsonSerializer.Serialize(new { SourceArtistId = c.ProviderId, Scope = scope }));
    internal const string UnreviewedCompoundBlocker = "existing compound expression has not been reviewed";
    internal const string SharedListingWarning = "another source listing shares";
    public const string DefaultEvidence = "Reviewer chose this identity in the import review.";
    public const string DefaultReason = "Reviewed and confirmed in the import review.";
    private static void ValidateDecision(ReleaseImportDecision decision)
    {
        if (decision.Kind is not ("artist" or "recording" or "release") || decision.ProviderId <= 0 ||
            decision.Action is not ("create" or "reuse" or "unresolved") ||
            decision.Kind == "release" && decision.Action != "create" ||
            decision.Kind != "artist" && decision.Action == "unresolved" ||
            decision.Action == "reuse" && string.IsNullOrWhiteSpace(decision.CanonicalId) ||
            decision.Action != "reuse" && decision.CanonicalId is not null ||
            string.IsNullOrWhiteSpace(decision.Evidence) || decision.Evidence.Length > 2000)
            throw new ReleaseImportException(400, "validation_failed", "Choose a valid identity action; an optional note may be up to 2000 characters.");
    }
    public async ValueTask<ReleaseImportOutcome> ApplyAsync(string operationId, string expectedVersion, string reason, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new ReleaseImportException(400, "confirmation_required", "Deliberate confirmation is required.");
        if (string.IsNullOrWhiteSpace(reason)) reason = DefaultReason;
        await RequireSchema(cancellationToken).ConfigureAwait(false);
        var stored = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (stored.Outcome is null)
        {
            if (expectedVersion != stored.Plan.Version)
                throw new ReleaseImportException(412, "import_state_changed", "The original review token is required.");
            if (stored.SupersededBy is not null)
                throw new ReleaseImportException(409, "import_superseded", "Review decisions changed. Use the newest immutable preview.");
            if (stored.Plan.ExpiresUtc <= clock.GetUtcNow())
                throw new ReleaseImportException(410, "import_expired", "Review expired. Create a new preview.");
            if (stored.Plan.Blockers.Count > 0)
                throw new ReleaseImportException(409, "import_blocked", "Resolve all review blockers before applying.");
            var current = await Bundle(stored.Plan.Source.ReleaseId, cancellationToken).ConfigureAwait(false);
            if (current.BundleDigest != stored.Plan.Source.BundleDigest)
                throw new ReleaseImportException(412, "import_source_changed", "Source changed since review. No import was applied.");
        }
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var result = await Imports(unit).ApplyAsync(operationId, expectedVersion, reason, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
