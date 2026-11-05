using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>All incoming and outgoing edges for a label, protected by its aggregate ETag.</summary>
public sealed record LabelRelationshipsResponse(string LabelId, IReadOnlyList<LabelRelationshipResponse> Items);
/// <summary>A directed label relationship with evidence and linked label identities.</summary>
public sealed record LabelRelationshipResponse(string Id, RelatedLabelResponse From, RelatedLabelResponse To,
    string Type, string Source, string? SourceUrl, string? Notes, DateTimeOffset CreatedUtc);
/// <summary>A related label and its edit version, used to reject stale relationship targets.</summary>
public sealed record RelatedLabelResponse(string Id, string? Name, string Version);
/// <summary>Full replacement of outgoing edges only. Incoming edges must be edited on their originating label.</summary>
public sealed record ReplaceLabelRelationshipsRequest(IReadOnlyList<LabelRelationshipRequest> Items);
/// <summary>An outgoing edge. Null ID creates a new edge; existing IDs must belong to this label.</summary>
public sealed record LabelRelationshipRequest(string? Id, string ToLabelId, string Type, string Source,
    string? SourceUrl, string? Notes, string TargetVersion);
/// <summary>A directed edge in the resulting merge graph, retaining its original identity and evidence.</summary>
public sealed record MergeRelationshipResponse(string Id, string FromLabelId, string ToLabelId, string Type,
    string Source, string? SourceUrl, string? Notes, DateTimeOffset CreatedUtc);

internal static class LabelRelationshipEndpoints
{
    public static void MapLabelRelationships(this WebApplication app)
    {
        app.MapGet("/api/v1/labels/{id}/relationships", GetAsync).WithTags("Label relationships")
            .WithName("GetLabelRelationships").WithSummary("Read parent/imprint and distribution edges in both directions")
            .Produces<LabelRelationshipsResponse>().Produces(308).ProducesProblem(404);
        app.MapPatch("/api/v1/labels/{id}/relationships", ReplaceAsync).WithTags("Label relationships")
            .WithName("ReplaceLabelRelationships").WithSummary("Conditionally replace outgoing label relationships")
            .WithDescription("Requires the label If-Match and target versions. At most 100 outgoing imprint_of/distributed_by edges. Rejects duplicates, self-links, retired labels and imprint cycles; records permanent history and invalidates both endpoint labels. Incoming edges are never replaced by this operation.")
            .Accepts<ReplaceLabelRelationshipsRequest>("application/json").Produces<LabelRelationshipsResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(413).ProducesProblem(415).ProducesProblem(428);
    }
    private static async Task<IResult> GetAsync(string id, HttpContext context, ILabelRelationshipService service, ILabelMergeService merges, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var identity = await merges.ResolveAsync(id, ct);
        if (identity.IsRetired)
            return TypedResults.Redirect($"/api/v1/labels/{Uri.EscapeDataString(identity.CanonicalId!)}/relationships", permanent: true, preserveMethod: true);
        var result = await service.GetAsync(id, ct);
        if (result is null) return Missing(context);
        context.Response.Headers.ETag = LabelRequests.ETag(result.Version);
        return TypedResults.Ok(ToResponse(result));
    }
    private static async Task<IResult> ReplaceAsync(string id, HttpContext context, ILabelRelationshipService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["items"]);
        if (!fields.TryGetValue("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100)
            throw new ApiRequestException(400, "validation_failed", "Supply items as an array of at most 100 outgoing relationships.");
        var inputs = new List<LabelRelationshipInput>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ApiRequestException(400, "validation_failed", "Each relationship must be an object.");
            var values = new Dictionary<string, JsonElement>();
            foreach (var property in item.EnumerateObject())
                if (!new[] { "id", "toLabelId", "type", "source", "sourceUrl", "notes", "targetVersion" }.Contains(property.Name) ||
                    !values.TryAdd(property.Name, property.Value))
                    throw new ApiRequestException(400, "validation_failed", "Unknown or duplicate relationship field.");
            string? Optional(string field) => values.TryGetValue(field, out var value) ? LabelRequests.NullableString(value, field) : null;
            inputs.Add(new(Optional("id"), LabelRequests.RequiredString(values, "toLabelId"),
                LabelRequests.RequiredString(values, "type"), LabelRequests.RequiredString(values, "source"),
                Optional("sourceUrl"), Optional("notes"), LabelRequests.RequiredString(values, "targetVersion")));
        }
        var result = await service.ReplaceAsync(id, version, inputs, ct);
        if (result.Status == LabelUpdateStatus.NotFound) return Missing(context);
        if (result.Status == LabelUpdateStatus.Stale)
            return TypedResults.Problem(ApiErrors.Create(context, 412, "relationship_state_changed", "Relationship state changed",
                "This label or a related label changed. No relationships were saved. Reload relationships and review again."));
        var saved = result.Relationships ?? throw new InvalidOperationException("Relationship write returned no result.");
        context.Response.Headers.ETag = LabelRequests.ETag(saved.Version);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(ToResponse(saved));
    }
    private static IResult Missing(HttpContext context) => TypedResults.Problem(ApiErrors.Create(context, 404, "label_not_found", "Label not found"));
    private static LabelRelationshipsResponse ToResponse(LabelRelationships result) => new(result.LabelId,
        result.Items.Select(v => new LabelRelationshipResponse(v.Relationship.Id,
            new(v.From.Id, v.From.Name, v.From.Version), new(v.To.Id, v.To.Name, v.To.Version),
            v.Relationship.Type, v.Relationship.Source, v.Relationship.SourceUrl, v.Relationship.Notes, v.Relationship.CreatedUtc)).ToArray());
    internal static MergeRelationshipResponse ToMergeResponse(LabelRelationship r) =>
        new(r.Id, r.FromLabelId, r.ToLabelId, r.Type, r.Source, r.SourceUrl, r.Notes, r.CreatedUtc);
}
