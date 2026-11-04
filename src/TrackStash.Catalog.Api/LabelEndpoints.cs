using Microsoft.AspNetCore.Http.HttpResults;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Api;

internal static class LabelEndpoints
{
    public static void MapLabels(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/labels").WithTags("Labels");
        group.MapGet("", ListAsync).WithName("ListLabels")
            .WithSummary("Search, filter, sort, and page label candidates")
            .WithDescription("Search uses strict-normalized literal substrings of names and aliases. Same-name and shared-provider matches remain separate candidates.")
            .ProducesProblem(400);
        group.MapGet("/{id}", GetAsync).WithName("GetLabel")
            .WithSummary("Read a canonical label")
            .WithDescription("Returns aliases and provider references without provider payloads, and an aggregate ETag for editing.")
            .ProducesProblem(404).Produces(308);
        group.MapPost("", CreateAsync).WithName("CreateLabel")
            .WithSummary("Create a label with a server-assigned GUID")
            .WithDescription("Does not merge same-name labels. Returns Location and ETag. POST retries are not idempotent.")
            .Accepts<CreateLabelRequest>("application/json").ProducesProblem(400).ProducesProblem(409).ProducesProblem(415);
        group.MapPatch("/{id}", PatchAsync).WithName("UpdateLabel")
            .WithSummary("Conditionally edit name, sort name, or the manual profile")
            .WithDescription("JSON Merge Patch. Omitted top-level fields remain; profile, if supplied, is a complete replacement with every field required. Supply one strong last-read If-Match ETag.")
            .Accepts<UpdateLabelRequest>("application/merge-patch+json")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(415).ProducesProblem(428);
        group.MapGet("/{id}/aliases", AliasesAsync).WithName("GetLabelAliases")
            .WithSummary("Read alternate label names")
            .WithDescription("Read-only alternate names for this label identity. No alias mutation is exposed.")
            .ProducesProblem(404).Produces(308);
        group.MapGet("/{id}/external-references", ReferencesAsync).WithName("GetLabelExternalReferences")
            .WithSummary("Read provider identities for a label")
            .WithDescription("Returns provider IDs and last-seen timestamps, excluding raw provider payloads.")
            .ProducesProblem(404).Produces(308);
    }

    private static async Task<Ok<LabelListResponse>> ListAsync(HttpContext context, ILabelService service, CancellationToken cancellationToken)
    {
        var page = await service.QueryAsync(LabelRequests.Query(context.Request), cancellationToken);
        return TypedResults.Ok(new LabelListResponse(
            page.Items.Select(x => new LabelListItemResponse(x.Id, x.Name, x.SortName, x.Version)).ToArray(),
            page.Total, page.Page, page.PageSize));
    }

    private static async Task<Results<Ok<LabelResponse>, ProblemHttpResult, RedirectHttpResult>> GetAsync(
        string id, HttpContext context, ILabelService service, ILabelMergeService merges, CancellationToken cancellationToken)
    {
        var redirect = await RetiredReadAsync(id, "", context, merges, cancellationToken);
        if (redirect is not null) return redirect;
        var label = await service.GetAsync(id, cancellationToken);
        if (label is null) return Missing(context);
        context.Response.Headers.ETag = LabelRequests.ETag(label.Version);
        return TypedResults.Ok(ToResponse(label));
    }

    private static async Task<Results<Created<LabelResponse>, ProblemHttpResult>> CreateAsync(
        HttpContext context, ILabelService service, CancellationToken cancellationToken)
    {
        var request = await LabelRequests.CreateAsync(context.Request, cancellationToken);
        var result = await service.CreateAsync(request.Name, request.SortName, cancellationToken);
        if (result.Status == LabelWriteStatus.Conflict)
            return TypedResults.Problem(ApiErrors.Create(context, 409, "identity_conflict", "Label identity conflict"));
        var label = result.Label ?? throw new InvalidOperationException("Created label is missing from the application result.");
        context.Response.Headers.ETag = LabelRequests.ETag(label.Version);
        return TypedResults.Created($"/api/v1/labels/{Uri.EscapeDataString(label.Id)}", ToResponse(label));
    }

    private static async Task<Results<Ok<LabelResponse>, ProblemHttpResult>> PatchAsync(
        string id, HttpContext context, ILabelService service, CancellationToken cancellationToken)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var patch = await LabelRequests.PatchAsync(context.Request, cancellationToken);
        var result = await service.UpdateAsync(id, version, patch, cancellationToken);
        if (result.Status == LabelWriteStatus.NotFound) return Missing(context);
        if (result.Status == LabelWriteStatus.Stale)
            return TypedResults.Problem(ApiErrors.Create(context, 412, "stale_version", "Label has changed",
                "Read the label again before applying your edit."));
        var label = result.Label ?? throw new InvalidOperationException("Updated label is missing from the application result.");
        context.Response.Headers.ETag = LabelRequests.ETag(label.Version);
        return TypedResults.Ok(ToResponse(label));
    }

    private static async Task<Results<Ok<IReadOnlyList<LabelAliasResponse>>, ProblemHttpResult, RedirectHttpResult>> AliasesAsync(
        string id, HttpContext context, ILabelService service, ILabelMergeService merges, CancellationToken cancellationToken)
    {
        var redirect = await RetiredReadAsync(id, "/aliases", context, merges, cancellationToken);
        if (redirect is not null) return redirect;
        var label = await service.GetAsync(id, cancellationToken);
        return label is null ? Missing(context) : TypedResults.Ok(ToResponse(label).Aliases);
    }

    private static async Task<Results<Ok<IReadOnlyList<LabelReferenceResponse>>, ProblemHttpResult, RedirectHttpResult>> ReferencesAsync(
        string id, HttpContext context, ILabelService service, ILabelMergeService merges, CancellationToken cancellationToken)
    {
        var redirect = await RetiredReadAsync(id, "/external-references", context, merges, cancellationToken);
        if (redirect is not null) return redirect;
        var label = await service.GetAsync(id, cancellationToken);
        return label is null ? Missing(context) : TypedResults.Ok(ToResponse(label).ExternalReferences);
    }

    private static ProblemHttpResult Missing(HttpContext context)
        => TypedResults.Problem(ApiErrors.Create(context, 404, "label_not_found", "Label not found"));

    private static async Task<RedirectHttpResult?> RetiredReadAsync(string id, string suffix, HttpContext context,
        ILabelMergeService merges, CancellationToken cancellationToken)
    {
        var identity = await merges.ResolveAsync(id, cancellationToken);
        if (!identity.IsRetired) return null;
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Redirect($"/api/v1/labels/{Uri.EscapeDataString(identity.CanonicalId!)}{suffix}", permanent: true, preserveMethod: true);
    }

    private static LabelResponse ToResponse(LabelDetail label) => new(
        label.Id, label.Name, label.SortName, label.Version, label.CreatedUtc, label.UpdatedUtc,
        label.Aliases.Select(a => new LabelAliasResponse(a.Value, a.IsPrimary)).ToArray(),
        label.ExternalReferences.Select(r => new LabelReferenceResponse(r.Source, r.ExternalId, r.IsPrimary, r.LastSeenUtc)).ToArray(),
        ToProfileResponse(label.Profile));

    internal static LabelProfileResponse ToProfileResponse(TrackStash.Core.Storage.LabelProfile profile) => new(
        profile.Description, profile.CountryCode, profile.City, profile.FoundedYear, profile.LifecycleStatus,
        profile.Notes, profile.Favourite, profile.ReviewStatus,
        profile.Links.Select(l => new LabelWebLinkResponse(l.Kind, l.Url, l.Title, l.SourceUrl)).ToArray());
}
