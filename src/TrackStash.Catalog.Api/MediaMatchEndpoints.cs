using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api;

/// <summary>Read proposals for bounded scanner identities without reading audio.</summary>
public sealed record MediaMatchPlanRequest(IReadOnlyList<long> MediaIds);
/// <summary>Freeze explicitly selected mappings/classifications against the original proposal ETag.</summary>
public sealed record PrepareMediaMatchesRequest(IReadOnlyList<long> MediaIds, IReadOnlyList<MediaMatchSelection> Selections);
/// <summary>The acceptance action is the audit reason; no typed justification is required.</summary>
public sealed record AcceptMediaMatchesRequest(bool Confirmed);

internal static class MediaMatchEndpoints
{
    private static long[] Ids(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("mediaIds", out var values) || values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() is < 1 or > 100)
            throw new ApiRequestException(400, "validation_failed", "Choose between 1 and 100 scanner identities.");
        return values.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var id) ? id :
            throw new ApiRequestException(400, "validation_failed", "Media identities must be integers.")).ToArray();
    }

    public static void MapMediaMatches(this WebApplication app)
    {
        app.MapPost("/api/v1/media-match-proposals", async (HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["mediaIds"]);
            var plan = await service.MatchPlanAsync(Ids(fields), ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(plan.Version);
            return TypedResults.Ok(plan);
        }).WithName("PreviewMediaMatches").WithTags("Media").WithSummary("Propose existing Recordings using tags and exact stored fingerprint copies")
          .Accepts<MediaMatchPlanRequest>("application/json").Produces<MediaMatchPlan>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/media-match-reviews", async (HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["mediaIds", "selections"]);
            var ids = Ids(fields);
            if (!fields.TryGetValue("selections", out var values) || values.ValueKind != JsonValueKind.Array ||
                values.GetArrayLength() is < 1 or > 100)
                throw new ApiRequestException(400, "validation_failed", "Select between 1 and 100 proposals.");
            var selections = new List<MediaMatchSelection>();
            foreach (var value in values.EnumerateArray())
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (value.ValueKind != JsonValueKind.Object ||
                    value.EnumerateObject().Any(p => !keys.Add(p.Name) || p.Name is not ("mediaId" or "recordingId" or "classification")) ||
                    !value.TryGetProperty("mediaId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number))
                    throw new ApiRequestException(400, "validation_failed", "Choose only scanner identity, Recording and classification.");
                string? Text(string name) => !value.TryGetProperty(name, out var text) || text.ValueKind == JsonValueKind.Null ? null :
                    text.ValueKind == JsonValueKind.String ? text.GetString() :
                    throw new ApiRequestException(400, "validation_failed", "Recording/classification must be text or null.");
                selections.Add(new(number, Text("recordingId"), Text("classification")));
            }
            var review = await service.PrepareMatchesAsync(ids, selections, version, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Created($"/api/v1/media-match-reviews/{review.Id}", review);
        }).WithName("PrepareMediaMatches").WithTags("Media").WithSummary("Verify selected and supporting files and freeze an atomic matching batch")
          .Accepts<PrepareMediaMatchesRequest>("application/json").Produces<MediaMatchReview>(201)
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428).ProducesProblem(504);
        app.MapGet("/api/v1/media-match-reviews/{id}", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var review = await service.GetMatchesAsync(id, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Ok(review);
        }).WithName("GetMediaMatches").WithTags("Media").WithSummary("Recover a matching batch and any saved outcome")
          .Produces<MediaMatchReview>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/media-match-reviews/{id}/apply", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["confirmed"]);
            if (!fields.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ApiRequestException(400, "validation_failed", "Explicit boolean acceptance is required.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.ApplyMatchesAsync(id, version, confirmed.GetBoolean(), ct));
        }).WithName("AcceptMediaMatches").WithTags("Media").WithSummary("Atomically accept selected links/classifications with a system-recorded judgement audit")
          .WithDescription("No typed reason, new Catalog entities or whole-group Release association. Existing links are never replaced. Same-review retry returns the saved outcome.")
          .Accepts<AcceptMediaMatchesRequest>("application/json").Produces<MediaMatchOutcome>()
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428).ProducesProblem(504);
    }
}
