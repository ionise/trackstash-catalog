using System.Globalization;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>A canonical catalog release listing, independent of purchases and media files.</summary>
public sealed record ReleaseSummaryResponse(string Id, string? Title, string? CatalogNumber, string? PublishDate,
    string? LabelId, string? LabelName, int PlacementCount);
/// <summary>A stable title/ID ordered release page, optionally restricted to a linked Label.</summary>
public sealed record ReleaseListResponse(IReadOnlyList<ReleaseSummaryResponse> Items, int Total, int Page, int PageSize);
/// <summary>A canonical label linked to a release.</summary>
public sealed record ReleaseLabelResponse(string Id, string? Name, string? Role, bool IsPrimary);
/// <summary>A credited Artist in a release or placement scope; credited spelling may differ from the canonical name.</summary>
public sealed record ReleaseCreditResponse(string ArtistId, string? ArtistName, string? CreditedName, string Role,
    int? Position, string IdentityStatus);
/// <summary>A public provider identity; no cached payload is exposed.</summary>
public sealed record ReleaseReferenceResponse(string Source, string ExternalId, bool IsPrimary);
/// <summary>Canonical release metadata, label links and release-level credits.</summary>
public sealed record ReleaseResponse(string Id, string? Title, string Version, string? CatalogNumber, string? Upc, string? PublishDate,
    string? NewReleaseDate, IReadOnlyList<ReleaseLabelResponse> Labels, IReadOnlyList<ReleaseCreditResponse> Credits,
    IReadOnlyList<ReleaseReferenceResponse> ExternalReferences);
/// <summary>A stable release placement of a Recording, including listing credits and separate display/source/official numbering.</summary>
public sealed record ReleaseTrackResponse(string PlacementId, string RecordingId, string? Title, string? MixName,
    string? Isrc, long? DurationMs, int DisplayOrder, int? SourcePosition, string OrderSource, int? DiscNumber, int? TrackNumber,
    IReadOnlyList<ReleaseCreditResponse> Credits, IReadOnlyList<ReleaseReferenceResponse> ExternalReferences);
/// <summary>A page of release placements in catalog display order, not a purchased-track list.</summary>
public sealed record ReleaseTracksResponse(IReadOnlyList<ReleaseTrackResponse> Items, int Total, int Page, int PageSize);

internal static class ReleaseEndpoints
{
    public static void MapReleases(this WebApplication app)
    {
        app.MapGet("/api/v1/releases", List).WithName("ListCatalogReleases").WithTags("Releases")
            .WithSummary("Search canonical releases by title or catalog number, optionally within a Label")
            .Produces<ReleaseListResponse>().ProducesProblem(400).ProducesProblem(409);
        app.MapGet("/api/v1/releases/{id}", Get).WithName("GetCatalogRelease").WithTags("Releases")
            .WithSummary("Read canonical release metadata, labels and release-level credits")
            .Produces<ReleaseResponse>().ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/releases/{id}/tracks", Tracks).WithName("GetCatalogReleaseTracks").WithTags("Releases")
            .WithSummary("Page a release's Recording placements and listing credits in display order")
            .Produces<ReleaseTracksResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
    }
    private static ReleaseQuery Query(HttpRequest request, bool tracks = false)
    {
        foreach (var field in request.Query)
            if (field.Value.Count != 1 || !(field.Key is "page" or "pageSize" || !tracks && (field.Key is "q" or "labelId")))
                throw new ApiRequestException(400, "validation_failed", "Unknown or repeated release query field.");
        int Number(string field, int fallback) => !request.Query.TryGetValue(field, out var value) ? fallback :
            int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number :
            throw new ApiRequestException(400, "validation_failed", "Invalid release pagination.");
        var query = new ReleaseQuery(Number("page", 1), Number("pageSize", 25),
            request.Query.TryGetValue("q", out var search) ? search[0] : null,
            request.Query.TryGetValue("labelId", out var label) ? label[0] : null);
        query.Validate(); return query;
    }
    private static void Headers(HttpContext context) => context.Response.Headers.CacheControl = "no-store";
    private static async Task<IResult> List(HttpContext context, IReleaseService service, CancellationToken ct)
    {
        var result = await service.QueryAsync(Query(context.Request), ct); Headers(context);
        return TypedResults.Ok(new ReleaseListResponse(result.Items.Select(r =>
            new ReleaseSummaryResponse(r.Id, r.Title, r.CatalogNumber, r.PublishDate, r.LabelId, r.LabelName, r.PlacementCount)).ToArray(),
            result.Total, result.Page, result.PageSize));
    }
    private static ReleaseCreditResponse Credit(ReleaseContributorDetail c) => new(c.ArtistId, c.ArtistName, c.CreditedName, c.Role, c.Position, c.IdentityStatus);
    private static ReleaseReferenceResponse Reference(ReleaseReferenceDetail r) => new(r.Source, r.ExternalId, r.IsPrimary);
    private static async Task<IResult> Get(string id, HttpContext context, IReleaseService service, CancellationToken ct)
    {
        var result = await service.GetAsync(id, ct) ?? throw new ApiRequestException(404, "release_not_found", "Catalog release was not found.");
        Headers(context); context.Response.Headers.ETag = LabelRequests.ETag(result.Version);
        return TypedResults.Ok(new ReleaseResponse(result.Id, result.Title, result.Version, result.CatalogNumber, result.Upc, result.PublishDate,
            result.NewReleaseDate, result.Labels.Select(l => new ReleaseLabelResponse(l.Id, l.Name, l.Role, l.IsPrimary)).ToArray(),
            result.Credits.Select(Credit).ToArray(), result.ExternalReferences.Select(Reference).ToArray()));
    }
    private static async Task<IResult> Tracks(string id, HttpContext context, IReleaseService service, CancellationToken ct)
    {
        var result = await service.TracksAsync(id, Query(context.Request, true), ct)
            ?? throw new ApiRequestException(404, "release_not_found", "Catalog release was not found.");
        Headers(context);
        return TypedResults.Ok(new ReleaseTracksResponse(result.Items.Select(p =>
            new ReleaseTrackResponse(p.PlacementId, p.RecordingId, p.Title, p.MixName, p.Isrc, p.DurationMs, p.DisplayOrder,
                p.SourcePosition, p.OrderSource, p.DiscNumber, p.TrackNumber, p.Credits.Select(Credit).ToArray(),
                p.ExternalReferences.Select(Reference).ToArray())).ToArray(), result.Total, result.Page, result.PageSize));
    }
}
