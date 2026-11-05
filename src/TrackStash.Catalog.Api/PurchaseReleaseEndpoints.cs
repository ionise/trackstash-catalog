using System.Globalization;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

internal static class PurchaseReleaseEndpoints
{
    public static void MapPurchaseReleases(this WebApplication app)
    {
        app.MapGet("/api/v1/purchase-release-candidates", Query).WithName("QueryPurchaseReleaseCandidates")
            .WithTags("Catalog imports").WithSummary("Page read-only purchase-corpus release candidates")
            .WithDescription("Derived corpus flags are entry-point hints, not receipt or media ownership proof. No fetch or catalog write.")
            .Produces<PurchaseSourcePage<PurchaseReleaseCandidate>>().ProducesProblem(400).ProducesProblem(503);
        app.MapGet("/api/v1/purchase-release-candidates/{releaseId}", Inspect).WithName("InspectPurchaseReleaseCandidate")
            .WithTags("Catalog imports").WithSummary("Inspect a cached release, structured credits and source completeness")
            .WithDescription("Tracks are paged but completeness diagnostics and digest cover the whole source bundle. SourceComplete does not certify canonical identity. No import occurs.")
            .Produces<PurchaseReleaseInspectorResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(503);
    }

    private static PurchaseReleaseQuery Fields(HttpRequest request, bool inspect = false)
    {
        foreach (var field in request.Query)
            if (field.Value.Count != 1 || field.Key is not ("page" or "pageSize") && (inspect || field.Key != "q"))
                throw new PurchaseSourceValidationException("Unknown or repeated query field.");
        int Number(string name, int fallback) => !request.Query.TryGetValue(name, out var value) ? fallback :
            int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number :
            throw new PurchaseSourceValidationException("Invalid pagination.");
        var query = new PurchaseReleaseQuery(Number("page", 1), Number("pageSize", 25),
            request.Query.TryGetValue("q", out var search) ? search[0] : null);
        query.Validate();
        return query;
    }

    private static async Task<IResult> Query(HttpContext context, IPurchaseReleaseInspector service, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(await service.QueryAsync(Fields(context.Request), ct));
    }

    private static async Task<IResult> Inspect(string releaseId, HttpContext context, IPurchaseReleaseInspector service, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!long.TryParse(releaseId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new PurchaseSourceValidationException("Release ID must be a positive provider ID.");
        var response = await service.InspectAsync(id, Fields(context.Request, inspect: true), ct);
        if (response is null) return TypedResults.Problem(ApiErrors.Create(context, 404, "purchase_candidate_not_found",
            "Purchase candidate not found", "This release is not in the configured purchase corpus."));
        return TypedResults.Ok(response);
    }
}
