using System.Text.Json;
using TrackStash.Catalog.Application;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api;

/// <summary>Select a server-derived Recording candidate; no filesystem paths or proposal values are accepted.</summary>
public sealed record PrepareMediaLinkRequest(string RecordingId);
/// <summary>Explicit version identity confirmation and audit reason.</summary>
public sealed record ApplyMediaLinkRequest(string Reason, bool Confirmed);

internal static class MediaEndpoints
{
    public static void MapMedia(this WebApplication app)
    {
        app.MapGet("/api/v1/recordings/{id}/media", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.LinkedAsync(id, ct));
        }).WithName("GetRecordingMedia").WithTags("Media").WithSummary("Read accepted media associations, not fresh presence/backup verification")
          .Produces<IReadOnlyList<LinkedRecordingMedia>>().ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/media", async (HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            foreach (var field in context.Request.Query)
                if (field.Value.Count != 1 || field.Key is not ("library" or "q" or "page"))
                    throw new ApiRequestException(400, "validation_failed", "Unknown or repeated media query field.");
            var page = !context.Request.Query.TryGetValue("page", out var text) ? 1 : int.TryParse(text, out var value) ? value :
                throw new ApiRequestException(400, "validation_failed", "Invalid media page.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.QueryAsync(context.Request.Query["library"].FirstOrDefault(),
                context.Request.Query["q"].FirstOrDefault(), page, ct));
        }).WithName("ListMediaInventory").WithTags("Media")
          .WithSummary("Browse historical scanner tags in named approved libraries without reading audio")
          .Produces<MediaInventoryPage>().ProducesProblem(400).ProducesProblem(409);
        app.MapGet("/api/v1/media/{id:long}/candidates", async (long id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.CandidatesAsync(id, ct));
        }).WithName("GetMediaRecordingCandidates").WithTags("Media")
          .WithSummary("Propose Catalog Recording matches from tagged ISRC/title, mix, Artists and duration")
          .Produces<MediaCandidates>().ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/media/{id:long}/link-reviews", async (long id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["recordingId"]);
            if (!fields.TryGetValue("recordingId", out var recording) || recording.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(recording.GetString()))
                throw new ApiRequestException(400, "validation_failed", "Choose a Recording candidate.");
            var review = await service.PrepareAsync(id, recording.GetString()!, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Created($"/api/v1/media-link-reviews/{review.Id}", review);
        }).WithName("PrepareMediaLink").WithTags("Media")
          .WithSummary("Read and verify media SHA-256 and scoped sidecar before freezing a link review")
          .WithDescription("May hydrate cloud-only media. No links, tags or purchase assertions are written. Requires schema 13; symlinks/conflicting evidence are rejected.")
          .Accepts<PrepareMediaLinkRequest>("application/json").Produces<MediaLinkReview>(201)
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(504);
        app.MapGet("/api/v1/media-link-reviews/{id}", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var review = await service.GetReviewAsync(id, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Ok(review);
        }).WithName("GetMediaLinkReview").WithTags("Media").WithSummary("Read frozen link evidence and saved outcome")
          .Produces<MediaLinkReview>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/media-link-reviews/{id}/apply", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["reason", "confirmed"]);
            if (!fields.TryGetValue("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                !fields.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ApiRequestException(400, "validation_failed", "Reason and explicit boolean confirmation are required.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.ApplyAsync(id, version, reason.GetString()!, confirmed.GetBoolean(), ct));
        }).WithName("ApplyMediaLink").WithTags("Media").WithSummary("Reverify media and Catalog state, then atomically link with permanent audit")
          .WithDescription("Original If-Match, reason and explicit identity confirmation required. No existing links are replaced; retries return the saved outcome. No acquisition, ownership, backup or tag assertions.")
          .Accepts<ApplyMediaLinkRequest>("application/json").Produces<MediaLinkOutcome>()
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428).ProducesProblem(504);
    }
}
