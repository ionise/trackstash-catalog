using System.Globalization;
using System.Text.Json;
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
            .WithDescription("Derived corpus flags are entry-point hints, not receipt or media ownership proof. Each item reports catalog Releases already owning its Beatport release ID; hideAdmitted=true omits them before paging (schema 9+, otherwise 409). Each item also reports import readiness (complete cached source and exactly one catalog label); readyOnly=true keeps only ready releases before paging. Source summaries are cached until the purchase source file changes. No fetch or catalog write.")
            .Produces<PurchaseReleaseCandidatePage>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(503);
        app.MapGet("/api/v1/purchase-release-candidates/{releaseId}", Inspect).WithName("InspectPurchaseReleaseCandidate")
            .WithTags("Catalog imports").WithSummary("Inspect a cached release, structured credits and source completeness")
            .WithDescription("Tracks are paged but completeness diagnostics and digest cover the whole source bundle. SourceComplete does not certify canonical identity. No import occurs.")
            .Produces<PurchaseReleaseInspectorResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(503);
        app.MapGet("/api/v1/purchase-release-admissions/ready", Ready).WithName("ListReadyPurchaseReleases")
            .WithTags("Catalog imports").WithSummary("List ready purchase releases not yet admitted")
            .WithDescription("Release IDs with complete cached source and exactly one catalog label whose Beatport release ID no catalog Release owns. Read-only.")
            .Produces<ReadyPurchaseReleasesResponse>().ProducesProblem(409).ProducesProblem(503);
        app.MapPost("/api/v1/purchase-release-admissions", Admit).WithName("AdmitPurchaseReleases")
            .WithTags("Catalog imports").WithSummary("Batch-admit purchase releases whose identities are all clear-cut")
            .WithDescription("Body: releaseIds (1-25), dryRun, confirmed (required when dryRun is false). Each release gets its own immutable plan and atomic apply. New Beatport identities are created, compound artist names are created unresolved, and a Recording with the same ISRC and mix is reused. Releases needing judgement are reported as needs_review with reasons and left untouched. A dry run stores nothing.")
            .Accepts<AdmitPurchaseReleasesRequest>("application/json").Produces<PurchaseBatchAdmissionReport>()
            .ProducesProblem(400).ProducesProblem(409).ProducesProblem(503);
    }

    private static async Task<IResult> Ready(HttpContext context, IPurchaseBatchAdmissionService service, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Query.Count > 0) throw new PurchaseSourceValidationException("No query fields are accepted.");
        return TypedResults.Ok(new ReadyPurchaseReleasesResponse(await service.ReadyReleaseIdsAsync(ct)));
    }

    private static async Task<IResult> Admit(HttpContext context, IPurchaseBatchAdmissionService service, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["releaseIds", "dryRun", "confirmed"]);
        if (!fields.TryGetValue("releaseIds", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out _)))
            throw new ReleaseImportException(400, "validation_failed", "releaseIds must be an array of numeric Beatport release IDs.");
        bool Flag(string name) => !fields.TryGetValue(name, out var value) ? false : value.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new ReleaseImportException(400, "validation_failed", $"{name} must be true or false."),
        };
        var dryRun = Flag("dryRun");
        if (!dryRun && !Flag("confirmed")) throw new ReleaseImportException(400, "confirmation_required", "Deliberate confirmation is required to admit releases.");
        return TypedResults.Ok(await service.RunAsync(list.EnumerateArray().Select(v => v.GetInt64()).ToArray(), dryRun, ct));
    }

    private static PurchaseReleaseQuery Fields(HttpRequest request, bool inspect = false)
    {
        foreach (var field in request.Query)
            if (field.Value.Count != 1 || field.Key is not ("page" or "pageSize") && (inspect || field.Key is not ("q" or "hideAdmitted" or "readyOnly")))
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
        bool Flag(string name) => context.Request.Query.TryGetValue(name, out var value) ? value[0] switch
        {
            "true" => true, "false" => false,
            _ => throw new PurchaseSourceValidationException($"{name} must be true or false."),
        } : false;
        return TypedResults.Ok(await service.QueryAsync(Fields(context.Request), Flag("hideAdmitted"), Flag("readyOnly"), ct));
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

/// <summary>Batch admission request: up to 25 Beatport release IDs; confirmed must be true unless dryRun.</summary>
public sealed record AdmitPurchaseReleasesRequest(IReadOnlyList<long> ReleaseIds, bool DryRun, bool Confirmed);

/// <summary>Ready purchase releases not yet admitted to the catalog.</summary>
public sealed record ReadyPurchaseReleasesResponse(IReadOnlyList<long> ReleaseIds);
