using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

/// <summary>A track row with everything a track list can show: release context, labels, cover, credits and references.</summary>
public sealed record TrackDetail(TrackListing Track, string? CoverAssetId, IReadOnlyList<ReleaseLabelDetail> Labels,
    IReadOnlyList<ReleaseContributorDetail> Credits, IReadOnlyList<ReleaseContributorDetail> RecordingCredits,
    IReadOnlyList<ReleaseReferenceDetail> ExternalReferences, IReadOnlyList<ArtistAppearanceCredit> ArtistCredits);
public sealed record TrackDetailPage(IReadOnlyList<TrackDetail> Items, int Total, int Page, int PageSize);
public sealed record CatalogRecordingDetail(string Id, string? Title, string? MixName, string? Isrc, long? DurationMs, string Version,
    IReadOnlyList<ReleaseContributorDetail> Credits, IReadOnlyList<ReleaseReferenceDetail> ExternalReferences, int ReleaseCount);
public sealed class TrackBrowseUnavailableException : Exception;
/// <summary>A track list filter names an entity that does not exist.</summary>
public sealed class TrackFilterNotFoundException(string kind) : Exception($"Catalog {kind} was not found.")
{
    public string Kind { get; } = kind;
}

public interface ITrackService
{
    ValueTask<TrackDetailPage> QueryAsync(TrackQuery query, CancellationToken ct = default);
    ValueTask<CatalogRecordingDetail?> GetRecordingAsync(string id, CancellationToken ct = default);
}

public sealed class TrackService(IStorageProvider storage) : ITrackService
{
    private async ValueTask<int> Ready(CancellationToken ct)
    {
        var version = await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false);
        return version < 9 ? throw new TrackBrowseUnavailableException() : version;
    }

    public async ValueTask<TrackDetailPage> QueryAsync(TrackQuery query, CancellationToken ct = default)
    {
        query.Validate();
        var schema = await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        if (query.ReleaseId is not null && await unit.Releases.GetByIdAsync(query.ReleaseId, ct).ConfigureAwait(false) is null)
            throw new TrackFilterNotFoundException("release");
        if (query.LabelId is not null && await unit.Labels.GetByIdAsync(query.LabelId, ct).ConfigureAwait(false) is null)
            throw new TrackFilterNotFoundException("label");
        if (query.ArtistId is not null && await unit.Artists.GetByIdAsync(query.ArtistId, ct).ConfigureAwait(false) is null)
            throw new TrackFilterNotFoundException("artist");
        if (query.RecordingId is not null && await unit.Recordings.GetByIdAsync(query.RecordingId, ct).ConfigureAwait(false) is null)
            throw new TrackFilterNotFoundException("recording");
        var page = await unit.Recordings.QueryTracksAsync(query, ct).ConfigureAwait(false);
        var foundations = unit.CatalogFoundations!;
        var artists = new Dictionary<string, Artist>(StringComparer.Ordinal);
        var placements = new Dictionary<string, IReadOnlyList<ReleasePlacement>>(StringComparer.Ordinal);
        var releases = new Dictionary<string, (IReadOnlyList<ReleaseLabelDetail> Labels, string? Cover)>(StringComparer.Ordinal);
        var labels = new Dictionary<string, Label?>(StringComparer.Ordinal);
        var items = new List<TrackDetail>();
        foreach (var track in page.Items)
        {
            ReleasePlacement? placement = null;
            (IReadOnlyList<ReleaseLabelDetail> Labels, string? Cover) release = ([], null);
            if (track.ReleaseId is not null)
            {
                if (!placements.TryGetValue(track.ReleaseId, out var onRelease))
                    placements.Add(track.ReleaseId, onRelease = await foundations.GetPlacementsAsync(track.ReleaseId, ct).ConfigureAwait(false));
                placement = onRelease.Single(p => p.Id == track.PlacementId);
                if (!releases.TryGetValue(track.ReleaseId, out release))
                {
                    var entity = await unit.Releases.GetByIdAsync(track.ReleaseId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Track refers to a missing Release.");
                    var linked = new List<ReleaseLabelDetail>();
                    foreach (var link in entity.LabelLinks.OrderByDescending(l => l.IsPrimary))
                    {
                        if (!labels.TryGetValue(link.LabelId, out var label))
                            labels.Add(link.LabelId, label = await unit.Labels.GetByIdAsync(link.LabelId, ct).ConfigureAwait(false));
                        linked.Add(new(link.LabelId, label?.Name, link.Role, link.IsPrimary));
                    }
                    var gallery = schema >= 11 ? await unit.Artwork.GetReleaseGalleryAsync(entity.Id, ct).ConfigureAwait(false) : null;
                    releases.Add(entity.Id, release = (linked, gallery?.Items.SingleOrDefault(i => i.IsPreferred && i.Role == "cover")?.AssetId));
                }
            }
            var recording = await unit.Recordings.GetByIdAsync(track.RecordingId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Track refers to a missing Recording.");
            var recordingCredits = await foundations.GetCreditsAsync(CatalogEntityKind.Recording, track.RecordingId, ct).ConfigureAwait(false);
            var placementCredits = placement?.Credits ?? [];
            var references = (placement?.ExternalReferences ?? []).Concat(recording.ExternalReferences)
                .DistinctBy(r => (r.Source, r.ExternalId)).Select(r => new ReleaseReferenceDetail(r.Source, r.ExternalId, r.IsPrimary)).ToArray();
            var evidence = query.ArtistId is null ? [] : placementCredits.Where(c => c.ArtistId == query.ArtistId).Select(c => new ArtistAppearanceCredit(c.Role, "placement", c.CreditedName))
                .Concat(recordingCredits.Where(c => c.ArtistId == query.ArtistId).Select(c => new ArtistAppearanceCredit(c.Role, "recording", c.CreditedName)))
                .Distinct().ToArray();
            items.Add(new(track, release.Cover, release.Labels,
                await ReleaseService.Credits(unit, placementCredits, artists, ct).ConfigureAwait(false),
                await ReleaseService.Credits(unit, recordingCredits, artists, ct).ConfigureAwait(false),
                references, evidence));
        }
        return new(items, page.Total, page.Page, page.PageSize);
    }

    public async ValueTask<CatalogRecordingDetail?> GetRecordingAsync(string id, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var recording = await unit.Recordings.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (recording is null) return null;
        var foundations = unit.CatalogFoundations!;
        var metadata = await foundations.GetMetadataAsync(CatalogEntityKind.Recording, id, ct).ConfigureAwait(false);
        var credits = await foundations.GetCreditsAsync(CatalogEntityKind.Recording, id, ct).ConfigureAwait(false);
        var appearances = await unit.Recordings.QueryTracksAsync(new TrackQuery(PageSize: 1, RecordingId: id, GroupByRecording: true), ct).ConfigureAwait(false);
        return new(recording.Id, recording.Title ?? recording.Name, recording.MixName, recording.Isrc, metadata?.DurationMs ?? recording.DurationMs,
            metadata?.Version ?? recording.Version, await ReleaseService.Credits(unit, credits, [], ct).ConfigureAwait(false),
            recording.ExternalReferences.Select(r => new ReleaseReferenceDetail(r.Source, r.ExternalId, r.IsPrimary)).ToArray(),
            appearances.Items.SingleOrDefault()?.ReleaseCount ?? 0);
    }
}
