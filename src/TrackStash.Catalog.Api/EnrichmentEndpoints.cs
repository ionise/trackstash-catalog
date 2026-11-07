using TrackStash.Catalog.Application;
using TrackStash.Catalog.Api.Middleware;
using System.Text.Json;

namespace TrackStash.Catalog.Api;

/// <summary>Fetch and freeze a chosen candidate against the original lookup's catalog version.</summary>
public sealed record CreateEnrichmentReviewRequest(string CandidateId, string? EditionId, string CatalogVersion);
/// <summary>Explicit field selections, optional preferred classification and confirmation of a frozen review.</summary>
public sealed record ApplyEnrichmentReviewRequest(IReadOnlyList<string> Fields, string? PreferredGenre, string Reason, bool Confirmed);
/// <summary>Explicit per-Recording field intent for atomic batch apply.</summary>
public sealed record ApplyEnrichmentBatchRequest(IReadOnlyList<EnrichmentBatchSelection> Selections, string Reason, bool Confirmed);
/// <summary>Fetch a selected Beatport Release for track matching against the original catalog snapshot.</summary>
public sealed record CreateEnrichmentBatchRequest(string CandidateId, string CatalogVersion);

internal static class EnrichmentEndpoints
{
    private static void MapReviews(WebApplication app)
    {
        app.MapPost("/api/v1/releases/{id}/enrichment/beatport/track-reviews",
            async (string id, HttpContext context, IEnrichmentApplyService service, CancellationToken ct) =>
            {
                var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["candidateId", "catalogVersion"]);
                if (!fields.TryGetValue("candidateId", out var candidate) || candidate.ValueKind != JsonValueKind.String ||
                    !fields.TryGetValue("catalogVersion", out var version) || version.ValueKind != JsonValueKind.String)
                    throw new ApiRequestException(400, "validation_failed", "candidateId and original catalogVersion are required.");
                var review = await service.PrepareBatchAsync(id, candidate.GetString()!, version.GetString()!, ct);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
                return TypedResults.Created($"/api/v1/enrichment-track-reviews/{review.Id}", review);
            }).WithName("CreateBeatportTrackReview").WithTags("Enrichment")
            .WithSummary("Fetch Beatport Release and track evidence and freeze conservative Recording matches")
            .WithDescription("Two broker calls, at most 100 tracks. No canonical writes. Ambiguous/unmatched tracks are not writable. Requires original Release catalogVersion and schema 13.")
            .Accepts<CreateEnrichmentBatchRequest>("application/json").Produces<EnrichmentBatchReview>(201)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412)
            .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        app.MapGet("/api/v1/enrichment-track-reviews/{id}",
            async (string id, HttpContext context, IEnrichmentApplyService service, CancellationToken ct) =>
            {
                var review = await service.GetBatchAsync(id, ct);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
                return TypedResults.Ok(review);
            }).WithName("GetEnrichmentTrackReview").WithTags("Enrichment")
            .WithSummary("Read frozen track matches, field evidence and atomic batch outcome")
            .Produces<EnrichmentBatchReview>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/enrichment-track-reviews/{id}/apply",
            async (string id, HttpContext context, IEnrichmentApplyService service, CancellationToken ct) =>
            {
                var version = LabelRequests.ExpectedVersion(context.Request);
                var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["selections", "reason", "confirmed"]);
                if (!fields.TryGetValue("selections", out var selections) || selections.ValueKind != JsonValueKind.Array ||
                    selections.GetArrayLength() is 0 or > 100 || !fields.TryGetValue("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                    !fields.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ApiRequestException(400, "validation_failed", "Bounded selections, reason and boolean confirmation are required.");
                var intent = new List<EnrichmentBatchSelection>();
                foreach (var selection in selections.EnumerateArray())
                {
                    if (selection.ValueKind != JsonValueKind.Object ||
                        selection.EnumerateObject().Any(p => p.Name is not ("reviewId" or "fields" or "preferredGenre")) ||
                        selection.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                        !selection.TryGetProperty("reviewId", out var reviewId) || reviewId.ValueKind != JsonValueKind.String ||
                        !selection.TryGetProperty("fields", out var chosen) || chosen.ValueKind != JsonValueKind.Array ||
                        chosen.GetArrayLength() is 0 or > 20 || chosen.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.String))
                        throw new ApiRequestException(400, "validation_failed", "Each selection requires a reviewId and bounded field names only.");
                    var preferred = selection.TryGetProperty("preferredGenre", out var genre) ? LabelRequests.NullableString(genre, "preferredGenre") : null;
                    intent.Add(new(reviewId.GetString()!, chosen.EnumerateArray().Select(f => f.GetString()!).ToArray(), preferred));
                }
                var result = await service.ApplyBatchAsync(id, version, intent, reason.GetString()!, confirmed.GetBoolean(), ct);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(result);
            }).WithName("ApplyEnrichmentTrackReview").WithTags("Enrichment")
            .WithSummary("Apply selected matched Recordings atomically using frozen evidence and original If-Match")
            .WithDescription("No provider calls. All selected Recording changes and audits succeed together or roll back. Release context and selected Recording snapshots are rechecked; identical retries return the saved outcome.")
            .Accepts<ApplyEnrichmentBatchRequest>("application/json").Produces<EnrichmentBatchOutcome>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428);
        foreach (var (resource, kind) in new[] { ("recordings", EnrichmentEntityKind.Recording), ("releases", EnrichmentEntityKind.Release) })
        foreach (var provider in new[] { "sonovault", "beatport" })
        {
            app.MapPost($"/api/v1/{resource}/{{id}}/enrichment/{provider}/reviews",
                async (string id, HttpContext context, IEnrichmentApplyService service, CancellationToken ct) =>
                {
                    var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["candidateId", "editionId", "catalogVersion"]);
                    string Required(string field) => fields.TryGetValue(field, out var value) &&
                        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                        ? value.GetString()! : throw new ApiRequestException(400, "validation_failed", $"{field} is required.");
                    var editionId = fields.TryGetValue("editionId", out var edition) ? LabelRequests.NullableString(edition, "editionId") : null;
                    var review = await service.PrepareAsync(kind, id, Required("candidateId"), editionId, Required("catalogVersion"), ct, provider);
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
                    return TypedResults.Created($"/api/v1/enrichment-reviews/{review.Id}", review);
                })
                .WithName(provider == "sonovault" ? $"Create{kind}EnrichmentReview" : $"Create{kind}BeatportEnrichmentReview").WithTags("Enrichment")
                .WithSummary($"Fetch and freeze the chosen {provider} candidate for selective reviewed apply")
                .WithDescription("SonoVault uses one provider request; Beatport uses one broker call for Recordings or two for Releases. Requires schema 13. Stores a draft review only; no client-supplied proposal values are accepted.")
                .Accepts<CreateEnrichmentReviewRequest>("application/json").Produces<EnrichmentReview>(201)
                .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412)
                .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        }
        app.MapGet("/api/v1/enrichment-reviews/{id}",
            async (string id, HttpContext context, IEnrichmentApplyService service, CancellationToken ct) =>
            {
                var review = await service.GetAsync(id, ct);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
                return TypedResults.Ok(review);
            }).WithName("GetEnrichmentReview").WithTags("Enrichment")
            .WithSummary("Read the frozen enrichment review and saved outcome")
            .Produces<EnrichmentReview>().ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/enrichment-reviews/{id}/apply",
            async (string id, HttpContext context, IEnrichmentApplyService service, CancellationToken ct) =>
            {
                var version = LabelRequests.ExpectedVersion(context.Request);
                var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["fields", "preferredGenre", "reason", "confirmed"]);
                if (!fields.TryGetValue("fields", out var selection) || selection.ValueKind != JsonValueKind.Array ||
                    selection.GetArrayLength() > 20 || selection.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String) ||
                    !fields.TryGetValue("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                    !fields.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ApiRequestException(400, "validation_failed", "fields, reason and boolean confirmed are required.");
                var preferred = fields.TryGetValue("preferredGenre", out var genre) ? LabelRequests.NullableString(genre, "preferredGenre") : null;
                var result = await service.ApplyAsync(id, version, selection.EnumerateArray().Select(v => v.GetString()!).ToArray(),
                    preferred, reason.GetString()!, confirmed.GetBoolean(), ct);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(result);
            }).WithName("ApplyEnrichmentReview").WithTags("Enrichment")
            .WithSummary("Atomically apply explicitly selected fields with original If-Match and immutable audit")
            .WithDescription("Requires confirmation and reason. Rechecks complete catalog state, identity ownership and classification choices. Identical retries return the saved outcome; changed intent conflicts.")
            .Accepts<ApplyEnrichmentReviewRequest>("application/json").Produces<EnrichmentApplyOutcome>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428);
    }
    public static void MapEnrichment(this WebApplication app)
    {
        MapReviews(app);
        app.MapPost("/api/v1/recordings/{id}/enrichment/beatport",
            async (string id, HttpContext context, IRecordingEnrichmentService service, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await service.FindAsync(id, "beatport", ct));
            }).WithName("LookupRecordingBeatportEnrichment").WithTags("Enrichment")
            .WithSummary("Compare Beatport track metadata with a canonical Recording without applying it")
            .WithDescription("One bounded broker call. Existing Beatport identity first, then ISRC, then title/mix search with known primary Artists. No pagination, identity acceptance or media writes.")
            .Produces<EnrichmentLookup>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        app.MapPost("/api/v1/releases/{id}/enrichment/beatport",
            async (string id, HttpContext context, INamedEntityEnrichmentService service, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await service.FindAsync(EnrichmentEntityKind.Release, id, "beatport", ct));
            })
            .WithName("LookupReleaseBeatportEnrichment").WithTags("Enrichment")
            .WithSummary("Compare Beatport Release metadata without applying it")
            .Produces<EnrichmentLookup>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        app.MapPost("/api/v1/releases/{id}/enrichment/beatport/candidates/{candidateId}",
            async (string id, string candidateId, HttpContext context, INamedEntityEnrichmentService service, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await service.FindAsync(EnrichmentEntityKind.Release, id, "beatport", ct, candidateId));
            })
            .WithName("LookupReleaseBeatportCandidate").WithTags("Enrichment")
            .WithSummary("Explicitly fetch Beatport Release and track details for comparison")
            .Produces<EnrichmentLookup>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        foreach (var (resource, kind) in new[] { ("artists", EnrichmentEntityKind.Artist), ("labels", EnrichmentEntityKind.Label), ("releases", EnrichmentEntityKind.Release) })
        {
            app.MapPost($"/api/v1/{resource}/{{id}}/enrichment/sonovault",
                async (string id, HttpContext context, INamedEntityEnrichmentService service, CancellationToken ct) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return TypedResults.Ok(await service.FindAsync(kind, id, "sonovault", ct));
                })
                .WithName($"Lookup{kind}SonoVaultEnrichment").WithTags("Enrichment")
                .WithSummary($"Explicitly look up SonoVault metadata for a canonical {kind} without modifying it")
                .WithDescription("Uses the catalog name and provider identities, never private profile notes. At most one provider request; no accepted links or saved metadata.")
                .Produces<EnrichmentLookup>().ProducesProblem(400).ProducesProblem(404)
                .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        }
        app.MapPost("/api/v1/releases/{id}/enrichment/sonovault/candidates/{candidateId}",
            async (string id, string candidateId, string? editionId, HttpContext context,
                INamedEntityEnrichmentService service, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await service.FindAsync(EnrichmentEntityKind.Release, id, "sonovault", ct, candidateId, editionId));
            })
            .WithName("LookupReleaseSonoVaultCandidate").WithTags("Enrichment")
            .WithSummary("Explicitly fetch a SonoVault Release candidate or selected edition for comparison")
            .WithDescription("One bounded provider request; candidate and edition IDs are numeric provider identities, never accepted canonical links. Optional editionId selects track numbering.")
            .Produces<EnrichmentLookup>().ProducesProblem(400).ProducesProblem(404)
            .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        app.MapPost("/api/v1/recordings/{id}/enrichment/sonovault",
            async (string id, HttpContext context, IRecordingEnrichmentService service, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await service.FindAsync(id, "sonovault", ct));
            })
            .WithName("LookupRecordingSonoVaultEnrichment").WithTags("Enrichment")
            .WithSummary("Explicitly look up SonoVault metadata for a canonical Recording without modifying it")
            .WithDescription("Consumes at most one provider request. Returns candidates for comparison, not a saved import or accepted metadata.")
            .Produces<EnrichmentLookup>().ProducesProblem(400).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
    }
}
