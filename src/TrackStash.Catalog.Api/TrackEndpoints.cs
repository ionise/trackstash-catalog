using System.Globalization;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>
/// One track: a Recording placed on a release, or an unplaced Recording (null placement and release fields).
/// When grouped by Recording, the release fields describe its newest release and <c>ReleaseCount</c> counts all its releases.
/// </summary>
public sealed record TrackResponse(string? PlacementId, string RecordingId, string? Title, string? MixName, string? Isrc, long? DurationMs,
    string? ReleaseId, string? ReleaseTitle, string? CoverAssetId, string? PublishDate, string? NewReleaseDate,
    int? DisplayOrder, int? SourcePosition, string? OrderSource, int? DiscNumber, int? TrackNumber, int ReleaseCount,
    IReadOnlyList<ReleaseLabelResponse> Labels, IReadOnlyList<ReleaseCreditResponse> Credits, IReadOnlyList<ReleaseCreditResponse> RecordingCredits,
    IReadOnlyList<ReleaseReferenceResponse> ExternalReferences, IReadOnlyList<ArtistAppearanceCreditResponse> ArtistCredits,
    CatalogMusicalMetadata? MusicalMetadata = null, int LinkedCopyCount = 0);
/// <summary>A page of tracks matching the combined filters.</summary>
public sealed record TrackListResponse(IReadOnlyList<TrackResponse> Items, int Total, int Page, int PageSize);
/// <summary>A canonical Recording with its own credits and provider identities.</summary>
public sealed record RecordingResponse(string Id, string? Title, string? MixName, string? Isrc, long? DurationMs, string Version,
    IReadOnlyList<ReleaseCreditResponse> Credits, IReadOnlyList<ReleaseReferenceResponse> ExternalReferences, int ReleaseCount,
    CatalogMusicalMetadata? MusicalMetadata = null, int LinkedCopyCount = 0);

internal static class TrackEndpoints
{
    private static readonly string[] Fields = ["page", "pageSize", "q", "releaseId", "labelId", "artistId", "role", "recordingId", "groupBy", "sort", "order"];

    public static void MapTracks(this WebApplication app)
    {
        app.MapGet("/api/v1/tracks", List).WithName("ListCatalogTracks").WithTags("Tracks")
            .WithSummary("Search and page tracks, optionally filtered by release, Label, Artist or Recording and grouped by Recording")
            .Produces<TrackListResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/recordings/{id}", Get).WithName("GetCatalogRecording").WithTags("Tracks")
            .WithSummary("Read a canonical Recording with its credits and provider identities")
            .Produces<RecordingResponse>().ProducesProblem(404).ProducesProblem(409);
    }

    private static TrackQuery Query(HttpRequest request)
    {
        foreach (var field in request.Query)
            if (field.Value.Count != 1 || !Fields.Contains(field.Key))
                throw new ApiRequestException(400, "validation_failed", "Unknown or repeated track query field.");
        string? Text(string key) => request.Query.TryGetValue(key, out var value) ? value[0] : null;
        int Number(string key, int fallback) => Text(key) is not { } value ? fallback :
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number :
            throw new ApiRequestException(400, "validation_failed", "Invalid track pagination.");
        var groupBy = Text("groupBy");
        if (groupBy is not (null or "recording"))
            throw new ApiRequestException(400, "validation_failed", "Track groupBy must be recording.");
        var order = Text("order");
        if (order is not (null or "asc" or "desc"))
            throw new ApiRequestException(400, "validation_failed", "Track order must be asc or desc.");
        var query = new TrackQuery(Number("page", 1), Number("pageSize", 25), Text("q"), Text("releaseId"), Text("labelId"),
            Text("artistId"), Text("role"), Text("recordingId"), groupBy is not null, Text("sort"), order is null ? null : order == "desc");
        query.Validate(); return query;
    }

    private static ReleaseCreditResponse Credit(ReleaseContributorDetail c) => new(c.ArtistId, c.ArtistName, c.CreditedName, c.Role, c.Position, c.IdentityStatus);
    private static ReleaseReferenceResponse Reference(ReleaseReferenceDetail r) => new(r.Source, r.ExternalId, r.IsPrimary);

    private static async Task<IResult> List(HttpContext context, ITrackService service, CancellationToken ct)
    {
        TrackDetailPage result;
        try { result = await service.QueryAsync(Query(context.Request), ct); }
        catch (TrackFilterNotFoundException missing) { throw new ApiRequestException(404, $"{missing.Kind}_not_found", missing.Message); }
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new TrackListResponse(result.Items.Select(item =>
        {
            var t = item.Track;
            return new TrackResponse(t.PlacementId, t.RecordingId, t.Title, t.MixName, t.Isrc, t.DurationMs, t.ReleaseId, t.ReleaseTitle,
                item.CoverAssetId, t.PublishDate, t.NewReleaseDate, t.DisplayOrder, t.SourcePosition, t.OrderSource, t.DiscNumber, t.TrackNumber,
                t.ReleaseCount, item.Labels.Select(l => new ReleaseLabelResponse(l.Id, l.Name, l.Role, l.IsPrimary)).ToArray(),
                item.Credits.Select(Credit).ToArray(), item.RecordingCredits.Select(Credit).ToArray(),
                item.ExternalReferences.Select(Reference).ToArray(),
                item.ArtistCredits.Select(c => new ArtistAppearanceCreditResponse(c.Role, c.Scope, c.CreditedName)).ToArray(), item.MusicalMetadata, item.LinkedCopyCount);
        }).ToArray(), result.Total, result.Page, result.PageSize));
    }

    private static async Task<IResult> Get(string id, HttpContext context, ITrackService service, CancellationToken ct)
    {
        var r = await service.GetRecordingAsync(id, ct) ?? throw new ApiRequestException(404, "recording_not_found", "Catalog Recording was not found.");
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.ETag = LabelRequests.ETag(r.Version);
        return TypedResults.Ok(new RecordingResponse(r.Id, r.Title, r.MixName, r.Isrc, r.DurationMs, r.Version,
            r.Credits.Select(Credit).ToArray(), r.ExternalReferences.Select(Reference).ToArray(), r.ReleaseCount, r.MusicalMetadata, r.LinkedCopyCount));
    }
}
