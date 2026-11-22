using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrackStash.Catalog.Application;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>One provider identity and its possible Catalog matches.</summary>
public sealed record SonoImportIdentity(string Id, string Name, string? OwnerId,
    IReadOnlyList<ReleaseImportCandidate> Candidates);
/// <summary>Frozen provider evidence and Catalog identity candidates.</summary>
public sealed record SonoImportPreview(string Id, string Version, DateTimeOffset CreatedAt, string CatalogStamp,
    string SourceVersion, EnrichmentCandidate Candidate, IReadOnlyList<LabelListItem> Labels,
    IReadOnlyList<SonoImportIdentity> Artists, IReadOnlyList<SonoImportIdentity> Recordings,
    string? ExistingReleaseId, IReadOnlyList<ReleaseImportCandidate> ReleaseCandidates,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings,
    SonoImportOutcome? Outcome = null);
/// <summary>Explicit identity selection; null canonical ID creates a distinct entity.</summary>
public sealed record SonoImportDecision(string ProviderId, string? CanonicalId);
/// <summary>All identity choices for one frozen SonoVault release.</summary>
public sealed record ApplySonoImportRequest(string? LabelId, IReadOnlyList<SonoImportDecision> Artists,
    IReadOnlyList<SonoImportDecision> Recordings, bool AllowIncomplete, bool ConfirmDistinctRelease);
/// <summary>Replayable result of a SonoVault release admission.</summary>
public sealed record SonoImportOutcome(string ReviewId, string ReleaseId, int Recordings, int Placements);

/// <summary>Requests a frozen review of a previously inspected SonoVault Release.</summary>
public sealed record CreateSonoImportRequest(string ReleaseId, string? EditionId, string SourceVersion);

internal static class SonoVaultImportEndpoints
{
    public static void MapSonoVaultImports(this WebApplication app)
    {
        app.MapPost("/api/v1/sonovault-imports", async (HttpContext context, ISonoVaultImportService service, CancellationToken ct) =>
        {
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["releaseId", "editionId", "sourceVersion"]);
            string Required(string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
                throw new Middleware.ApiRequestException(400, "validation_failed", $"{name} is required.");
            var edition = fields.TryGetValue("editionId", out var value) ? LabelRequests.NullableString(value, "editionId") : null;
            var review = await service.PrepareAsync(Required("releaseId"), edition, Required("sourceVersion"), ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Created($"/api/v1/sonovault-imports/{review.Id}", review);
        }).WithName("PrepareSonoVaultImport").WithTags("Media")
          .WithSummary("Freeze SonoVault Release evidence and Catalog identity candidates")
          .Accepts<CreateSonoImportRequest>("application/json").Produces<SonoImportPreview>(201)
          .ProducesProblem(400).ProducesProblem(409).ProducesProblem(412).ProducesProblem(502).ProducesProblem(503);

        app.MapGet("/api/v1/sonovault-imports/{id}", async (string id, HttpContext context, ISonoVaultImportService service, CancellationToken ct) =>
        {
            var review = await service.GetAsync(id, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Ok(review);
        }).WithName("GetSonoVaultImport").WithTags("Media")
          .WithSummary("Read a frozen SonoVault import review and saved outcome")
          .Produces<SonoImportPreview>().ProducesProblem(400).ProducesProblem(404);

        app.MapPost("/api/v1/sonovault-imports/{id}/apply", async (string id, HttpContext context, ISonoVaultImportService service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["labelId", "artists", "recordings", "allowIncomplete", "confirmDistinctRelease"]);
            var label = fields.TryGetValue("labelId", out var labelValue) ? LabelRequests.NullableString(labelValue, "labelId") : null;
            IReadOnlyList<SonoImportDecision> Decisions(string name)
            {
                if (!fields.TryGetValue(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 200)
                    throw new Middleware.ApiRequestException(400, "validation_failed", $"{name} must be a bounded array.");
                var result = new List<SonoImportDecision>();
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("providerId" or "canonicalId")) ||
                        item.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1) ||
                        !item.TryGetProperty("providerId", out var provider) || provider.ValueKind != JsonValueKind.String ||
                        !item.TryGetProperty("canonicalId", out var canonical))
                        throw new Middleware.ApiRequestException(400, "validation_failed", "Each identity requires providerId and canonicalId.");
                    result.Add(new(provider.GetString()!, LabelRequests.NullableString(canonical, "canonicalId")));
                }
                return result;
            }
            if (!fields.TryGetValue("allowIncomplete", out var allow) || allow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new Middleware.ApiRequestException(400, "validation_failed", "Explicit tracklist completeness acknowledgement is required.");
            if (!fields.TryGetValue("confirmDistinctRelease", out var distinct) || distinct.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new Middleware.ApiRequestException(400, "validation_failed", "Explicit Release identity judgement is required.");
            var result = await service.ApplyAsync(id, version, new(label, Decisions("artists"), Decisions("recordings"),
                allow.GetBoolean(), distinct.GetBoolean()), ct);
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(result);
        }).WithName("ApplySonoVaultImport").WithTags("Media")
          .WithSummary("Create a reviewed SonoVault Release atomically")
          .WithDescription("Rechecks provider evidence and Catalog identity state. Media assignment is a separate operation.")
          .Accepts<ApplySonoImportRequest>("application/json").Produces<SonoImportOutcome>()
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428);
    }
}

public interface ISonoVaultImportService
{
    Task<SonoImportPreview> PrepareAsync(string releaseId, string? editionId, string sourceVersion, CancellationToken ct);
    Task<SonoImportPreview> GetAsync(string id, CancellationToken ct);
    Task<SonoImportOutcome> ApplyAsync(string id, string version, ApplySonoImportRequest request, CancellationToken ct);
}

public sealed class SonoVaultImportService(IStorageProvider storage, IEnumerable<IEnrichmentProvider> providers,
    TimeProvider clock) : ISonoVaultImportService
{
    private sealed record Payload(string Type, SonoImportPreview Preview, string ReleaseId);
    private sealed record Applied(ApplySonoImportRequest Request, SonoImportOutcome Outcome);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(value)))).ToLowerInvariant();
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Normalize(string? value) => EntityNameNormalizer.NormalizeStrict(value ?? "");
    private static EnrichmentApplyException Error(int status, string code, string message) => new(status, code, message);
    private async Task Ready(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct) < 13)
            throw Error(409, "enrichment_schema_required", "SonoVault import requires schema 13.");
    }
    private static bool PositiveId(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    private static string[] Owners(IReadOnlyList<ExternalReferenceOwner> owners, string id) =>
        owners.Where(o => PositiveId(o.ExternalId) && long.Parse(o.ExternalId, CultureInfo.InvariantCulture) ==
            long.Parse(id, CultureInfo.InvariantCulture)).Select(o => o.OwnerId).Distinct(StringComparer.Ordinal).ToArray();

    public async Task<SonoImportPreview> PrepareAsync(string releaseId, string? editionId, string sourceVersion, CancellationToken ct)
    {
        await Ready(ct);
        if (!PositiveId(releaseId) || editionId is not null && !PositiveId(editionId) ||
            sourceVersion is not { Length: 64 } || !sourceVersion.All(Uri.IsHexDigit))
            throw Error(400, "validation_failed", "Choose a SonoVault Release, optional edition and its original source version.");
        var lookup = await MediaProviderLookup.FindSonoVaultReleaseAsync(releaseId, editionId, providers, ct);
        if (lookup.Candidates.Count != 1 || lookup.SourceVersion != sourceVersion)
            throw Error(412, "enrichment_state_changed", "SonoVault evidence changed or disappeared; inspect the Release again.");
        var candidate = lookup.Candidates[0];
        var details = candidate.Metadata.ReleaseDetails
            ?? throw Error(502, "sonovault_invalid_response", "SonoVault did not provide Release details.");
        if (details.Tracks.Count is < 1 or > 100)
            throw Error(409, "sonovault_tracklist_unavailable", "Admission requires between 1 and 100 reported tracks.");
        var blockers = new List<string>();
        var warnings = new List<string>();
        var edition = details.SelectedEditionId is null ? null : details.Editions.Single(e => e.Id == details.SelectedEditionId);
        if (edition is null || edition.TrackCount is null || edition.TrackCount != details.Tracks.Count)
            warnings.Add("The selected tracklist is not verified complete for an edition. Explicitly accept incomplete or group-level evidence to import it.");
        if (candidate.Metadata.LabelName is null)
            blockers.Add("SonoVault did not report a Label for this selection.");
        var groups = (candidate.Metadata.Credits ?? []).Concat(details.Tracks.SelectMany(t => t.Credits))
            .GroupBy(c => c.Reference?.ExternalId).ToArray();
        if (groups.Any(g => g.Key is null || !PositiveId(g.Key) ||
            g.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != 1))
            blockers.Add("SonoVault Artist identities are missing or have contradictory names.");
        if (details.Tracks.GroupBy(t => t.Id).Any(g => g.Count() > 1))
            blockers.Add("The tracklist repeats a SonoVault Recording identity; repeated appearances need separate review.");
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var imports = unit.ReleaseImports!;
        var foundation = unit.CatalogFoundations!;
        var stamp = await imports.GetCatalogStampAsync(ct);
        var releaseOwners = Owners(await foundation.ListReferenceOwnersAsync(CatalogEntityKind.Release, "sonovault", ct), candidate.CandidateId);
        if (releaseOwners.Length > 1) blockers.Add("Multiple Catalog Releases own this SonoVault identity.");
        var releaseCandidates = await imports.FindCandidatesAsync(CatalogEntityKind.Release,
            Normalize(edition?.Title ?? candidate.Metadata.Name), cancellationToken: ct);
        if (releaseCandidates.Count > 100) blockers.Add("Too many Release candidates; resolve the Release identity before importing.");
        var labels = new List<LabelListItem>();
        if (candidate.Metadata.LabelName is { } labelName)
        {
            var found = await unit.Labels.QueryAsync(new LabelQuery { Search = labelName, PageSize = 100 }, ct);
            if (found.Total > found.Items.Count) blockers.Add("Too many Label candidates; resolve the Label before importing.");
            labels.AddRange(found.Items.Where(item => Normalize(item.Name) == Normalize(labelName)));
        }
        var artistOwners = await foundation.ListReferenceOwnersAsync(CatalogEntityKind.Artist, "sonovault", ct);
        var recordingOwners = await foundation.ListReferenceOwnersAsync(CatalogEntityKind.Recording, "sonovault", ct);
        var artists = new List<SonoImportIdentity>();
        foreach (var group in groups.Where(g => g.Key is not null && PositiveId(g.Key)))
        {
            var matches = await imports.FindCandidatesAsync(CatalogEntityKind.Artist, Normalize(group.First().Name), cancellationToken: ct);
            var owners = Owners(artistOwners, group.Key!);
            if (matches.Count > 100 || owners.Length > 1) blockers.Add($"Artist {group.Key} has too many or conflicting Catalog matches.");
            artists.Add(new(group.Key!, group.First().Name, owners.Length == 1 ? owners[0] : null, matches));
        }
        if (artists.Count > 200) blockers.Add("Too many Artist identities to review in one admission.");
        var recordings = new List<SonoImportIdentity>();
        foreach (var track in details.Tracks.DistinctBy(t => t.Id))
        {
            var isrc = MediaService.Isrc(track.Isrc) ? track.Isrc!.Trim().ToUpperInvariant() : null;
            var matches = await imports.FindCandidatesAsync(CatalogEntityKind.Recording, Normalize(track.Title), isrc, ct);
            var owners = Owners(recordingOwners, track.Id);
            if (matches.Count > 100 || owners.Length > 1) blockers.Add($"Recording {track.Id} has too many or conflicting Catalog matches.");
            recordings.Add(new(track.Id, track.Title, owners.Length == 1 ? owners[0] : null, matches));
        }
        var now = clock.GetUtcNow();
        var review = new SonoImportPreview(NewId(), "", now, stamp, sourceVersion, candidate, labels, artists,
            recordings, releaseOwners.Length == 1 ? releaseOwners[0] : null, releaseCandidates, blockers, warnings);
        review = review with { Version = Hash(review) };
        var payload = new Payload("SonoVaultImport", review, NewId());
        await unit.Enrichment!.SaveReviewAsync(new(review.Id, CatalogEntityKind.Release, payload.ReleaseId, review.Version, Json(payload), now), ct);
        await unit.CommitAsync(ct);
        return review;
    }

    private static async Task<Payload> Load(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await unit.Enrichment!.GetReviewAsync(id, ct)
            ?? throw Error(404, "sonovault_import_not_found", "SonoVault import review was not found.");
        using var json = JsonDocument.Parse(stored.PayloadJson);
        if (!json.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "SonoVaultImport")
            throw Error(400, "sonovault_import_invalid", "Choose a SonoVault import review.");
        return JsonSerializer.Deserialize<Payload>(stored.PayloadJson) ?? throw new InvalidDataException("Invalid SonoVault import review.");
    }

    public async Task<SonoImportPreview> GetAsync(string id, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var payload = await Load(unit, id, ct);
        var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
        var result = audit is null ? null : JsonSerializer.Deserialize<Applied>(audit.AfterJson)
            ?? throw new InvalidDataException("Invalid SonoVault import outcome.");
        return payload.Preview with { Outcome = result?.Outcome };
    }

    public async Task<SonoImportOutcome> ApplyAsync(string id, string version, ApplySonoImportRequest request, CancellationToken ct)
    {
        await Ready(ct);
        if (request.Artists is null || request.Recordings is null || request.Artists.Any(d => d is null) || request.Recordings.Any(d => d is null))
            throw Error(400, "validation_failed", "Choose an identity action for each Artist and Recording.");
        Payload payload;
        async Task<SonoImportOutcome?> Replay(IUnitOfWork unit)
        {
            var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
            if (audit is null) return null;
            var prior = JsonSerializer.Deserialize<Applied>(audit.AfterJson) ?? throw new InvalidDataException("Invalid SonoVault import outcome.");
            if (Json(prior.Request) != Json(request))
                throw Error(409, "sonovault_import_conflict", "This review was applied with different identity choices.");
            return prior.Outcome;
        }
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
        {
            payload = await Load(read, id, ct);
            if (payload.Preview.Version != version) throw Error(412, "enrichment_state_changed", "Use the original import review version.");
            if (await Replay(read) is { } saved) return saved;
        }
        var preview = payload.Preview;
        if (clock.GetUtcNow() - preview.CreatedAt >= TimeSpan.FromHours(24))
            throw Error(410, "enrichment_review_expired", "SonoVault import review expired.");
        if (preview.Blockers.Count != 0 || preview.ExistingReleaseId is not null)
            throw Error(409, "sonovault_import_blocked", "Resolve identity blockers or use the existing Catalog Release.");
        if (preview.Warnings.Count != 0 && !request.AllowIncomplete)
            throw Error(409, "sonovault_tracklist_incomplete", "Explicitly accept the incomplete or group-level tracklist.");
        if (preview.ReleaseCandidates.Count != 0 && !request.ConfirmDistinctRelease)
            throw Error(409, "sonovault_release_ambiguous", "Review the existing Release candidates and confirm a distinct Release.");
        static Dictionary<string, string?> Choices(IReadOnlyList<SonoImportDecision> decisions, IReadOnlyList<SonoImportIdentity> identities)
        {
            if (decisions.Count != identities.Count || decisions.Any(d => !PositiveId(d.ProviderId) ||
                d.CanonicalId is { Length: 0 or > 200 }) ||
                decisions.Select(d => d.ProviderId).Distinct(StringComparer.Ordinal).Count() != decisions.Count ||
                !decisions.Select(d => d.ProviderId).ToHashSet(StringComparer.Ordinal).SetEquals(identities.Select(i => i.Id)))
                throw Error(400, "validation_failed", "Choose each provider identity exactly once.");
            return decisions.ToDictionary(d => d.ProviderId, d => d.CanonicalId, StringComparer.Ordinal);
        }
        var artistChoices = Choices(request.Artists, preview.Artists);
        var recordingChoices = Choices(request.Recordings, preview.Recordings);
        var refreshed = await MediaProviderLookup.FindSonoVaultReleaseAsync(preview.Candidate.CandidateId, preview.Candidate.EditionId, providers, ct);
        if (refreshed.Candidates.Count != 1 || refreshed.SourceVersion != preview.SourceVersion)
            throw Error(412, "enrichment_state_changed", "SonoVault Release evidence changed; inspect it again.");
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if (await Replay(unit) is { } completed) return completed;
        if (await unit.ReleaseImports!.GetCatalogStampAsync(ct) != preview.CatalogStamp)
            throw Error(412, "enrichment_state_changed", "Catalog identity candidates changed; prepare a new review.");
        var now = clock.GetUtcNow();
        var labelName = preview.Candidate.Metadata.LabelName!;
        string labelId;
        if (request.LabelId is { } selectedLabel)
        {
            if (!preview.Labels.Any(l => l.Id == selectedLabel))
                throw Error(400, "validation_failed", "Choose one of the reviewed Label candidates.");
            if ((await unit.Labels.ResolveIdentityAsync(selectedLabel, ct)).CanonicalId != selectedLabel)
                throw Error(409, "sonovault_label_retired", "The chosen Label is retired.");
            labelId = selectedLabel;
        }
        else
        {
            if (preview.Labels.Count != 0)
                throw Error(409, "sonovault_label_ambiguous", "Choose an existing Label candidate before importing.");
            labelId = NewId();
            if (!await unit.Labels.TryCreateAsync(new Label { Id = labelId, Name = labelName,
                NormalizedName = Normalize(labelName), CreatedUtc = now, UpdatedUtc = now }, ct))
                throw Error(409, "sonovault_label_conflict", "Could not create the provider Label.");
        }
        var foundation = unit.CatalogFoundations!;
        var artistIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in preview.Artists)
        {
            var selected = artistChoices[identity.Id];
            if (identity.OwnerId is not null && selected != identity.OwnerId)
                throw Error(409, "sonovault_identity_conflict", $"Artist {identity.Id} is already bound to another Catalog Artist.");
            Artist artist;
            if (selected is null)
            {
                if (identity.Candidates.Count > 0) throw Error(409, "sonovault_artist_ambiguous", $"Choose an existing Artist for {identity.Name}.");
                artist = new Artist { Id = NewId(), Name = identity.Name, NormalizedName = Normalize(identity.Name),
                    Kind = "unknown", IdentityStatus = identity.Name.Contains(',') || identity.Name.Contains(" & ", StringComparison.Ordinal)
                        ? "unresolved_compound" : "accepted", CreatedUtc = now, UpdatedUtc = now };
            }
            else
            {
                if (identity.OwnerId != selected && !identity.Candidates.Any(c => c.Id == selected))
                    throw Error(400, "validation_failed", "Choose a reviewed Artist candidate.");
                artist = await unit.Artists.GetByIdAsync(selected, ct) ?? throw Error(404, "artist_not_found", "Chosen Artist disappeared.");
                if (artist.ExternalReferences.Any(r => r.Source == "sonovault" && r.ExternalId != identity.Id))
                    throw Error(409, "sonovault_identity_conflict", "The selected Artist has a different SonoVault identity.");
            }
            var beforeVersion = selected is null ? null : artist.Version;
            if (!artist.ExternalReferences.Any(r => r.Source == "sonovault" && r.ExternalId == identity.Id))
                await foundation.SaveEntityAsync(artist with { ExternalReferences = artist.ExternalReferences.Append(
                    new EntityReference { Source = "sonovault", ExternalId = identity.Id, LastSeenUtc = now }).ToArray() }, beforeVersion, ct);
            artistIds.Add(identity.Id, artist.Id);
        }
        var details = preview.Candidate.Metadata.ReleaseDetails!;
        var edition = details.SelectedEditionId is null ? null : details.Editions.Single(e => e.Id == details.SelectedEditionId);
        string? publish = edition?.DatePrecision == "day" && DateOnly.TryParseExact(edition.ReleaseDate, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        CatalogCredit Credit(EnrichmentCredit c) => new(NewId(), artistIds[c.Reference!.ExternalId], c.Role, null, c.Name,
            "sonovault", null, c.Name, Json(new { ProviderArtistId = c.Reference.ExternalId }));
        var releaseTitle = edition?.Title ?? preview.Candidate.Metadata.Name!;
        var release = new Release { Id = payload.ReleaseId, Title = releaseTitle, Name = releaseTitle, NormalizedName = Normalize(releaseTitle),
            CatalogNumber = edition?.CatalogNumber, Upc = edition?.Barcode, PublishDate = publish, CreatedUtc = now, UpdatedUtc = now,
            LabelLinks = [new() { LabelId = labelId, IsPrimary = true, Role = "issuing" }],
            ExternalReferences = [new() { Source = "sonovault", ExternalId = preview.Candidate.CandidateId, LastSeenUtc = now,
                PayloadJson = Json(new { preview.Candidate.EditionId }) }],
            ArtistCredits = (preview.Candidate.Metadata.Credits ?? []).Select(c => {
                var detail = Credit(c);
                return new ReleaseArtistCredit { ArtistId = detail.ArtistId, CreditName = detail.CreditedName,
                    Position = detail.Position, Detail = detail };
            }).ToArray() };
        await foundation.SaveEntityAsync(release, null, ct);
        var created = 0;
        var position = 0;
        foreach (var track in details.Tracks)
        {
            var identity = preview.Recordings.Single(r => r.Id == track.Id);
            var selected = recordingChoices[identity.Id];
            if (identity.OwnerId is not null && selected != identity.OwnerId)
                throw Error(409, "sonovault_identity_conflict", $"Recording {identity.Id} is already bound to another Catalog Recording.");
            var proposedIsrc = MediaService.Isrc(track.Isrc) ? track.Isrc!.Trim().ToUpperInvariant() : null;
            Recording recording;
            if (selected is null)
            {
                if (identity.Candidates.Count > 0)
                    throw Error(409, "sonovault_recording_ambiguous", $"Choose a reviewed Recording for {identity.Name}.");
                if (proposedIsrc is not null && await unit.Recordings.GetByIsrcAsync(proposedIsrc, ct) is not null)
                    throw Error(409, "sonovault_recording_ambiguous", "The provider ISRC is already used by another Recording.");
                recording = new Recording { Id = NewId(), Name = track.Title, Title = track.Title,
                    NormalizedName = Normalize(track.Title), Isrc = proposedIsrc,
                    MixName = track.RecordingCandidate?.Metadata.MixName, DurationMs = track.RecordingCandidate?.Metadata.DurationMs,
                    CreatedUtc = now, UpdatedUtc = now,
                    ArtistCredits = track.Credits.Select(c => {
                        var detail = Credit(c);
                        return new RecordingArtistCredit { ArtistId = detail.ArtistId, CreditName = detail.CreditedName,
                            Role = detail.Role, Position = detail.Position, Detail = detail };
                    }).ToArray() };
                created++;
            }
            else
            {
                if (identity.OwnerId != selected && !identity.Candidates.Any(c => c.Id == selected))
                    throw Error(400, "validation_failed", "Choose a reviewed Recording candidate.");
                recording = await unit.Recordings.GetByIdAsync(selected, ct) ?? throw Error(404, "recording_not_found", "Chosen Recording disappeared.");
                if (recording.Isrc is not null && proposedIsrc is not null && !recording.Isrc.Equals(proposedIsrc, StringComparison.OrdinalIgnoreCase) ||
                    recording.ExternalReferences.Any(r => r.Source == "sonovault" && r.ExternalId != identity.Id))
                    throw Error(409, "sonovault_identity_conflict", "The selected Recording has a conflicting ISRC or SonoVault identity.");
            }
            var beforeVersion = selected is null ? null : recording.Version;
            if (!recording.ExternalReferences.Any(r => r.Source == "sonovault" && r.ExternalId == identity.Id))
                await foundation.SaveEntityAsync(recording with { ExternalReferences = recording.ExternalReferences.Append(
                    new EntityReference { Source = "sonovault", ExternalId = identity.Id, LastSeenUtc = now }).ToArray() }, beforeVersion, ct);
            if (selected is null && track.RecordingCandidate?.Metadata is { } musical)
                await unit.Enrichment!.SaveMetadataAsync(CatalogEntityKind.Recording, recording.Id,
                    new(musical.Bpm, musical.MusicalKey?.Value, musical.MusicalKey?.Notation, musical.Genres, musical.Subgenres), ct);
            var releaseVersion = (await foundation.GetMetadataAsync(CatalogEntityKind.Release, payload.ReleaseId, ct))!.Version;
            await foundation.SavePlacementAsync(new(NewId(), payload.ReleaseId, recording.Id, "", ++position, position,
                "source", track.DiscNumber, track.TrackNumber, track.Credits.Select(Credit).ToArray(), []), releaseVersion, ct);
        }
        var outcome = new SonoImportOutcome(id, payload.ReleaseId, created, position);
        await unit.Enrichment!.SaveAuditAsync(new(id, Json(new[] { "SonoVaultImport" }),
            "User selected the SonoVault Release and reviewed the Catalog identities.", Json(payload),
            Json(new Applied(request, outcome)), now), ct);
        await unit.CommitAsync(ct);
        return outcome;
    }
}
