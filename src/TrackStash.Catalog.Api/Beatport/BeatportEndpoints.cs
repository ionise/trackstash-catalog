using System.Globalization;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api.Beatport;

/// <summary>Beatport account credentials, forwarded once to the local broker and never stored by the catalog.</summary>
public sealed record BeatportSignInRequest(string Username, string Password);

internal static class BeatportEndpoints
{
    public static void MapBeatport(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/beatport").WithTags("Beatport");
        group.MapGet("/connection", async (HttpContext context, IBeatportBrokerClient broker, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await broker.StatusAsync(ct));
        }).WithName("GetBeatportConnection").WithSummary("Beatport broker and sign-in status")
            .WithDescription("Reports whether the broker is configured and running, who is signed in, and cache counters. Never returns tokens.")
            .Produces<BeatportConnectionResponse>();

        group.MapPost("/sign-in", async (HttpContext context, IBeatportBrokerClient broker, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["username", "password"]);
            string Field(string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 256 } text ? text
                : throw new ApiRequestException(400, "validation_failed", $"{name} is required.", name);
            return TypedResults.Ok(await broker.SignInAsync(Field("username"), Field("password"), ct));
        }).WithName("SignInToBeatport").WithSummary("Sign the broker in to Beatport")
            .WithDescription("The password is forwarded to the local broker only; the broker keeps a refresh token in the macOS Keychain.")
            .Accepts<BeatportSignInRequest>("application/json").Produces<BeatportConnectionResponse>()
            .ProducesProblem(400).ProducesProblem(409).ProducesProblem(503);

        group.MapPost("/sign-out", async (IBeatportBrokerClient broker, CancellationToken ct) =>
        {
            await broker.SignOutAsync(ct);
            return TypedResults.NoContent();
        }).WithName("SignOutOfBeatport").WithSummary("Revoke and forget the broker's Beatport session").ProducesProblem(503);

        group.MapGet("/releases", async (HttpContext context, IBeatportBrowseService browse, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var sort = Text(context, "sort", 20) ?? "newest";
            if (!BeatportBrowseService.Sorts.ContainsKey(sort))
                throw new ApiRequestException(400, "validation_failed", "sort must be newest, oldest or name.", "sort");
            return TypedResults.Ok(await browse.SearchReleasesAsync(new(Text(context, "q", 200), Id(context, "labelId"), Id(context, "artistId"), sort,
                Number(context, "page", 1, 1, 10_000), Number(context, "pageSize", 25, 1, 50)), ct));
        }).WithName("SearchBeatportReleases").WithSummary("Search or browse Beatport releases through the broker")
            .WithDescription("Query: q (release name), labelId, artistId, sort (newest|oldest|name), page, pageSize (1-50). Each release reports catalog Releases and Labels that already reference it and whether it is in the purchase history. Read-only; responses come from the broker's cache when fresh.")
            .Produces<BeatportReleasePage>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(429).ProducesProblem(502).ProducesProblem(503);

        group.MapGet("/releases/{id}", async (string id, HttpContext context, IBeatportBrowseService browse, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await browse.GetReleaseAsync(PositiveId(id, "Release ID"), ct));
        }).WithName("GetBeatportRelease").WithSummary("A Beatport release with its tracks and catalog status")
            .Produces<BeatportReleaseDetail>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(502).ProducesProblem(503);

        group.MapGet("/labels", async (HttpContext context, IBeatportBrowseService browse, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var q = Text(context, "q", 200) ?? throw new ApiRequestException(400, "validation_failed", "q is required.", "q");
            return TypedResults.Ok(await browse.SearchLabelsAsync(q, Number(context, "page", 1, 1, 10_000), Number(context, "pageSize", 25, 1, 50), ct));
        }).WithName("SearchBeatportLabels").WithSummary("Search Beatport labels by name")
            .WithDescription("Each label lists the catalog Labels that already carry its Beatport ID.")
            .Produces<BeatportLabelPage>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(502).ProducesProblem(503);

        group.MapGet("/labels/{id}", async (string id, HttpContext context, IBeatportBrowseService browse, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await browse.GetLabelAsync(PositiveId(id, "Label ID"), ct));
        }).WithName("GetBeatportLabel").WithSummary("A Beatport label and the catalog Labels that reference it")
            .Produces<BeatportLabelItem>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(502).ProducesProblem(503);

        app.MapPost("/api/v1/purchase-release-candidates/{releaseId}/beatport-backfill",
            async (string releaseId, HttpContext context, IBeatportBackfillService service, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!long.TryParse(releaseId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    throw new ApiRequestException(400, "validation_failed", "Release ID must be a positive Beatport ID.");
                return TypedResults.Ok(await service.BackfillReleaseAsync(id, ct));
            }).WithTags("Beatport").WithName("BackfillPurchaseReleaseFromBeatport")
            .WithSummary("Fetch this release's missing or invalid Beatport data")
            .WithDescription("Fetches the release (when its payload is missing or invalid) and each incomplete track by ID through the broker, then re-inspects. Writes only the broker's cache tables in the scanner database, never the catalog.")
            .Produces<BeatportBackfillResult>().ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(502).ProducesProblem(503);

        app.MapPost("/api/v1/beatport-backfill-jobs", (BeatportBackfillJobs jobs, IBeatportBrokerClient broker) =>
        {
            if (!broker.Configured)
                throw new ApiRequestException(503, "beatport_broker_not_configured", "The Beatport broker is not configured. Start TrackStash with start-prod.sh.");
            var job = jobs.Start();
            return TypedResults.Accepted($"/api/v1/beatport-backfill-jobs/{job.Id}", job);
        }).WithTags("Beatport").WithName("StartBeatportBackfillJob").WithSummary("Backfill every incomplete purchase release in the background")
            .WithDescription("Returns 202 with a status Location. Processes releases one at a time through the broker's 2-requests-per-second limit. Stops on sign-in, broker or rate-limit failures.")
            .Produces<BeatportBackfillJobResponse>(202).ProducesProblem(409).ProducesProblem(503);

        app.MapGet("/api/v1/beatport-backfill-jobs/{id}", (string id, HttpContext context, BeatportBackfillJobs jobs) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(jobs.Get(id) ?? throw new ApiRequestException(404, "beatport_backfill_job_not_found",
                "Backfill job not found. Job history resets when the server restarts."));
        }).WithTags("Beatport").WithName("GetBeatportBackfillJob").WithSummary("Get bulk backfill progress")
            .Produces<BeatportBackfillJobResponse>().ProducesProblem(404);

        app.MapPost("/api/v1/beatport-backfill-jobs/{id}/cancel", (string id, BeatportBackfillJobs jobs) => TypedResults.Ok(jobs.Cancel(id)))
            .WithTags("Beatport").WithName("CancelBeatportBackfillJob").WithSummary("Stop a running bulk backfill after the current request")
            .Produces<BeatportBackfillJobResponse>().ProducesProblem(404);
    }

    private static string? Raw(HttpContext context, string name) =>
        context.Request.Query.TryGetValue(name, out var values) ? values.Count == 1 ? values[0]
            : throw new ApiRequestException(400, "validation_failed", $"{name} may be given once.", name) : null;

    private static string? Text(HttpContext context, string name, int max) => Raw(context, name)?.Trim() switch
    {
        null or "" => null,
        { Length: var length } when length > max => throw new ApiRequestException(400, "validation_failed", $"{name} must be at most {max} characters.", name),
        var text => text,
    };

    private static long PositiveId(string? value, string name) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id
            : throw new ApiRequestException(400, "validation_failed", $"{name} must be a positive Beatport ID.");

    private static long? Id(HttpContext context, string name) => Raw(context, name) is { Length: > 0 } value
        ? long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id
            : throw new ApiRequestException(400, "validation_failed", $"{name} must be a positive Beatport ID.", name)
        : null;

    private static int Number(HttpContext context, string name, int fallback, int min, int max) => Raw(context, name) is { Length: > 0 } value
        ? int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n
            : throw new ApiRequestException(400, "validation_failed", $"{name} must be between {min} and {max}.", name)
        : fallback;
}
