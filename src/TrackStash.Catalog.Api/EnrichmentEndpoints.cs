using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Api;

internal static class EnrichmentEndpoints
{
    public static void MapEnrichment(this WebApplication app)
    {
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
