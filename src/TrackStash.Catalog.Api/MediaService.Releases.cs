using System.Text.Json;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Frozen metadata release group and Catalog Release context; no audio or Recording match assertion.</summary>
public sealed record MediaReleaseReview(string Id, string Version, string GroupKey, string? Label, string ReleaseTitle,
    string ReleaseId, string CatalogTitle, IReadOnlyList<MediaInventoryItem> Files, IReadOnlyList<string> Evidence,
    DateTimeOffset CreatedAt, MediaReleaseAssociation? Outcome = null);
/// <summary>A reviewed release-group association, retained as immutable applied review history.</summary>
public sealed record MediaReleaseAssociation(string ReviewId, string GroupKey, string ReleaseId, string ReleaseTitle,
    string? Label, string MediaReleaseTitle, int FileCount, IReadOnlyList<string> Libraries, DateTimeOffset AppliedAt);

public sealed partial class MediaService
{
    private sealed record ReleaseState(Release Release, IReadOnlyList<ReleasePlacement> Placements,
        IReadOnlyList<CatalogCredit> Credits, IReadOnlyList<string?> Labels);
    private sealed record ReleasePayload(string Type, string SourceId, MediaReleaseReview Review, ReleaseState Catalog);
    private async Task<MediaInventoryItem[]> ReleaseGroup(string key, CancellationToken ct)
    {
        var files = (await Inventory(ct)).Where(f => GroupKey(f, "release") == key).OrderBy(f => f.Id).ToArray();
        if (files.Length == 0) throw Error(404, "media_group_not_found", "Media release group no longer exists; refresh the inventory.");
        if (files.Length > 100) throw Error(409, "media_group_limit", "Release association supports at most 100 files per group.");
        if (string.IsNullOrWhiteSpace(files[0].Release)) throw Error(409, "media_release_missing", "Embedded Release/album metadata is required for release association.");
        return files;
    }
    private static async Task<ReleaseState> ReadRelease(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var release = await unit.Releases.GetByIdAsync(id, ct) ?? throw Error(404, "release_not_found", "Catalog Release was not found.");
        var labels = new List<string?>();
        foreach (var link in release.LabelLinks)
            labels.Add((await unit.Labels.GetByIdAsync(link.LabelId, ct))?.Name);
        return new(release, await unit.CatalogFoundations!.GetPlacementsAsync(id, ct),
            await unit.CatalogFoundations.GetCreditsAsync(CatalogEntityKind.Release, id, ct), labels);
    }
    private static async Task<IReadOnlyList<ReleasePayload>> AppliedReleaseGroups(IUnitOfWork unit, CancellationToken ct)
    {
        var reviews = await unit.Enrichment!.ListAppliedReviewsAsync("MediaReleaseGroup", ct);
        if (reviews.Count > 1000) throw Error(409, "media_association_limit", "More than 1000 release associations; a paged workflow is required.");
        return reviews.Select(r => JsonSerializer.Deserialize<ReleasePayload>(r.PayloadJson)
            ?? throw new InvalidDataException("Invalid stored media release association.")).ToArray();
    }
    private static void CheckReleaseOwnership(IEnumerable<ReleasePayload> applied, string source, MediaInventoryItem[] files)
    {
        var ids = files.Select(f => f.Id).ToHashSet();
        if (applied.Any(p => p.SourceId == source && p.Review.Files.Any(f => ids.Contains(f.Id))))
            throw Error(409, "media_release_already_associated", "This group contains files already associated with a Release. Replacement is not offered.");
    }
    public async Task<MediaReleaseReview> PrepareReleaseAsync(string groupKey, string releaseId, CancellationToken ct)
    {
        await Ready(ct);
        var files = await ReleaseGroup(groupKey, ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        CheckReleaseOwnership(await AppliedReleaseGroups(unit, ct), SourceId, files);
        var catalog = await ReadRelease(unit, releaseId, ct);
        var evidence = new List<string> {
            "Release group is based on embedded Label and Release/album, not filesystem directory or canonical track identity.",
            "This association does not link files to Recordings, change track placements or verify audio/backups."
        };
        evidence.Add(Same(files[0].Release, catalog.Release.Title ?? catalog.Release.Name) ? "Release title agrees." : "Release title differs; explicit manual review is required.");
        if (!string.IsNullOrWhiteSpace(files[0].Label))
            evidence.Add(catalog.Labels.Any(l => Same(files[0].Label, l)) ? "Embedded Label agrees." : "Embedded Label differs or is not known in Catalog; explicit manual review is required.");
        var review = new MediaReleaseReview(Guid.NewGuid().ToString("N"), "", groupKey, files[0].Label, files[0].Release!,
            releaseId, catalog.Release.Title ?? catalog.Release.Name ?? releaseId, files, evidence, clock.GetUtcNow());
        review = review with { Version = Hash(new ReleasePayload("MediaReleaseGroup", SourceId, review, catalog)) };
        await unit.Enrichment!.SaveReviewAsync(new(review.Id, CatalogEntityKind.Release, releaseId, review.Version,
            Json(new ReleasePayload("MediaReleaseGroup", SourceId, review, catalog)), review.CreatedAt), ct);
        if (Json(await ReleaseGroup(groupKey, ct)) != Json(files)) throw Error(412, "enrichment_state_changed", "Scanner group changed during preparation.");
        await unit.CommitAsync(ct);
        return review;
    }
    private static async Task<ReleasePayload> LoadReleaseReview(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await unit.Enrichment!.GetReviewAsync(id, ct) ?? throw Error(404, "media_review_not_found", "Media release review was not found.");
        using var document = JsonDocument.Parse(stored.PayloadJson);
        if (!document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "MediaReleaseGroup")
            throw Error(400, "media_review_invalid", "This is not a media release-group review.");
        return JsonSerializer.Deserialize<ReleasePayload>(stored.PayloadJson) ?? throw new InvalidDataException("Invalid media release review.");
    }
    public async Task<MediaReleaseReview> GetReleaseReviewAsync(string id, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var payload = await LoadReleaseReview(unit, id, ct);
        var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
        return payload.Review with { Outcome = audit is null ? null : JsonSerializer.Deserialize<MediaReleaseAssociation>(audit.AfterJson) };
    }
    public async Task<MediaReleaseAssociation> ApplyReleaseAsync(string id, string version, string reason, bool confirmed, CancellationToken ct)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw Error(400, "validation_failed", "Explicit Release identity confirmation and a bounded reason are required.");
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var payload = await LoadReleaseReview(unit, id, ct);
        if (payload.Review.Version != version) throw Error(412, "enrichment_state_changed", "Use the original media release review ETag.");
        var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
        if (audit is not null)
        {
            if (audit.Reason != reason.Trim()) throw Error(409, "enrichment_already_applied", "Review was applied with a different reason.");
            return JsonSerializer.Deserialize<MediaReleaseAssociation>(audit.AfterJson)!;
        }
        if (clock.GetUtcNow() - payload.Review.CreatedAt >= TimeSpan.FromHours(24)) throw Error(410, "enrichment_review_expired", "Release review expired.");
        if (payload.SourceId != SourceId) throw Error(412, "enrichment_state_changed", "Scanner source changed.");
        var files = await ReleaseGroup(payload.Review.GroupKey, ct);
        if (Json(files) != Json(payload.Review.Files) || Json(await ReadRelease(unit, payload.Review.ReleaseId, ct)) != Json(payload.Catalog))
            throw Error(412, "enrichment_state_changed", "Scanner group or Catalog Release context changed; prepare a fresh review.");
        CheckReleaseOwnership(await AppliedReleaseGroups(unit, ct), SourceId, files);
        var outcome = new MediaReleaseAssociation(id, payload.Review.GroupKey, payload.Review.ReleaseId, payload.Review.CatalogTitle,
            payload.Review.Label, payload.Review.ReleaseTitle, files.Length, files.Select(f => f.Library).Distinct().ToArray(), clock.GetUtcNow());
        await unit.Enrichment.SaveAuditAsync(new(id, Json(new[] { "MediaReleaseAssociation" }), reason.Trim(), Json(payload), Json(outcome), outcome.AppliedAt), ct);
        if (Json(await ReleaseGroup(payload.Review.GroupKey, ct)) != Json(files)) throw Error(412, "enrichment_state_changed", "Scanner changed during apply.");
        await unit.CommitAsync(ct);
        return outcome;
    }
    public async Task<IReadOnlyList<MediaReleaseAssociation>> ReleaseAssociationsAsync(string releaseId, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if (await unit.Releases.GetByIdAsync(releaseId, ct) is null) throw Error(404, "release_not_found", "Release was not found.");
        var items = new List<MediaReleaseAssociation>();
        foreach (var payload in (await AppliedReleaseGroups(unit, ct)).Where(p => p.Review.ReleaseId == releaseId))
        {
            var audit = await unit.Enrichment!.GetAuditAsync(payload.Review.Id, ct) ?? throw new InvalidDataException("Missing association audit.");
            items.Add(JsonSerializer.Deserialize<MediaReleaseAssociation>(audit.AfterJson)!);
        }
        return items;
    }
}
