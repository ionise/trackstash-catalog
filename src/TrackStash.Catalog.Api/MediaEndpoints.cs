using System.Text.Json;
using TrackStash.Catalog.Application;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api;

/// <summary>Select a server-derived Recording candidate; no filesystem paths or proposal values are accepted.</summary>
public sealed record PrepareMediaLinkRequest(string RecordingId);
/// <summary>Explicit version identity confirmation and audit reason.</summary>
public sealed record ApplyMediaLinkRequest(string Reason, bool Confirmed);
/// <summary>Select a server-derived embedded Release group and a canonical Release.</summary>
public sealed record PrepareMediaReleaseRequest(string GroupKey, string ReleaseId);
/// <summary>Explicit file/Recording selections within the chosen whole Release group.</summary>
public sealed record PrepareMediaReconciliationRequest(string GroupKey, string ReleaseId, IReadOnlyList<MediaTrackSelection> Selections);

internal static class MediaEndpoints
{
    public static void MapMedia(this WebApplication app)
    {
        app.MapGet("/api/v1/media-reconciliation", async (HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            foreach (var field in context.Request.Query)
                if (field.Value.Count != 1 || field.Key is not ("groupKey" or "releaseId"))
                    throw new ApiRequestException(400, "validation_failed", "Unknown or repeated reconciliation query field.");
            string Text(string name) => context.Request.Query[name].FirstOrDefault() is { Length: > 0 and <= 200 } value ? value :
                throw new ApiRequestException(400, "validation_failed", "Choose a media group and Catalog Release.");
            var plan = await service.ReconcileAsync(Text("groupKey"), Text("releaseId"), ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(plan.Version);
            return TypedResults.Ok(plan);
        }).WithName("PreviewMediaReconciliation").WithTags("Media").WithSummary("Propose existing Release tracks and their media copies without reading audio")
          .Produces<MediaReconciliationPlan>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/media-reconciliation-reviews", async (HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["groupKey", "releaseId", "selections"]);
            string Text(string name) => fields.TryGetValue(name, out var field) && field.ValueKind == JsonValueKind.String &&
                field.GetString() is { Length: > 0 and <= 200 } value ? value :
                throw new ApiRequestException(400, "validation_failed", "Choose a bounded group and Release identity.");
            if (!fields.TryGetValue("selections", out var selections) || selections.ValueKind != JsonValueKind.Array ||
                selections.GetArrayLength() is < 1 or > 100)
                throw new ApiRequestException(400, "validation_failed", "Select between 1 and 100 files.");
            var choices = new List<MediaTrackSelection>();
            foreach (var selection in selections.EnumerateArray())
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (selection.ValueKind != JsonValueKind.Object ||
                    selection.EnumerateObject().Any(p => !keys.Add(p.Name) || p.Name is not ("mediaId" or "recordingId")) ||
                    !selection.TryGetProperty("mediaId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) ||
                    !selection.TryGetProperty("recordingId", out var recording) || recording.ValueKind != JsonValueKind.String ||
                    recording.GetString() is not { Length: > 0 and <= 200 } recordingId)
                    throw new ApiRequestException(400, "validation_failed", "Choose a file and existing Recording; paths and metadata are not accepted.");
                choices.Add(new(number, recordingId));
            }
            var review = await service.PrepareReconciliationAsync(Text("groupKey"), Text("releaseId"), version, choices, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Created($"/api/v1/media-reconciliation-reviews/{review.Id}", review);
        }).WithName("PrepareMediaReconciliation").WithTags("Media").WithSummary("Verify selected files and fingerprint-supporting copies, then freeze an audited batch")
          .Accepts<PrepareMediaReconciliationRequest>("application/json").Produces<MediaReconciliationReview>(201)
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/media-reconciliation-reviews/{id}", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var review = await service.GetReconciliationAsync(id, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Ok(review);
        }).WithName("GetMediaReconciliation").WithTags("Media").WithSummary("Read a frozen media reconciliation and its saved outcome")
          .Produces<MediaReconciliationReview>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/media-reconciliation-reviews/{id}/apply", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["reason", "confirmed"]);
            if (!fields.TryGetValue("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                !fields.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ApiRequestException(400, "validation_failed", "A reason and explicit boolean confirmation are required.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.ApplyReconciliationAsync(id, version, reason.GetString()!, confirmed.GetBoolean(), ct));
        }).WithName("ApplyMediaReconciliation").WithTags("Media").WithSummary("Atomically associate the Release group and link selected media copies to existing Recordings")
          .WithDescription("Reverifies bytes and sidecars, rejects stale context, retains immutable per-file and batch audit, and never overwrites existing associations.")
          .Accepts<ApplyMediaLinkRequest>("application/json").Produces<MediaReconciliationOutcome>()
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428);
        app.MapPost("/api/v1/media-release-reviews", async (HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["groupKey", "releaseId"]);
            string Text(string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 and <= 200 } text ? text :
                throw new ApiRequestException(400, "validation_failed", "Choose a bounded media group and Catalog Release identity.");
            var review = await service.PrepareReleaseAsync(Text("groupKey"), Text("releaseId"), ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Created($"/api/v1/media-release-reviews/{review.Id}", review);
        }).WithName("PrepareMediaReleaseAssociation").WithTags("Media").WithSummary("Freeze an embedded media release-group association review")
          .Accepts<PrepareMediaReleaseRequest>("application/json").Produces<MediaReleaseReview>(201).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412);
        app.MapGet("/api/v1/media-release-reviews/{id}", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var review = await service.GetReleaseReviewAsync(id, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(review.Version);
            return TypedResults.Ok(review);
        }).WithName("GetMediaReleaseReview").WithTags("Media").WithSummary("Read frozen release-group evidence and saved outcome")
          .Produces<MediaReleaseReview>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/media-release-reviews/{id}/apply", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["reason", "confirmed"]);
            if (!fields.TryGetValue("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                !fields.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ApiRequestException(400, "validation_failed", "A reason and explicit boolean confirmation are required.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.ApplyReleaseAsync(id, version, reason.GetString()!, confirmed.GetBoolean(), ct));
        }).WithName("ApplyMediaReleaseAssociation").WithTags("Media").WithSummary("Associate a media group with a Release using original review ETag and permanent audit")
          .WithDescription("Does not link Recordings, modify placements, read audio or assert backup/ownership. Requires schema 13.")
          .Accepts<ApplyMediaLinkRequest>("application/json").Produces<MediaReleaseAssociation>()
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/releases/{id}/media-groups", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.ReleaseAssociationsAsync(id, ct));
        }).WithName("GetReleaseMediaGroups").WithTags("Media").WithSummary("Read explicitly accepted media release-group associations")
          .Produces<IReadOnlyList<MediaReleaseAssociation>>().ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/recordings/{id}/media", async (string id, HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.LinkedAsync(id, ct));
        }).WithName("GetRecordingMedia").WithTags("Media").WithSummary("Read accepted media associations, not fresh presence/backup verification")
          .Produces<IReadOnlyList<LinkedRecordingMedia>>().ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/media", async (HttpContext context, IMediaService service, CancellationToken ct) =>
        {
            foreach (var field in context.Request.Query)
                if (field.Value.Count != 1 || field.Key is not ("library" or "q" or "page" or "label" or "release" or "artist" or "track" or "sort" or "direction" or "groupBy" or "groupKey"))
                    throw new ApiRequestException(400, "validation_failed", "Unknown or repeated media query field.");
            var page = !context.Request.Query.TryGetValue("page", out var text) ? 1 : int.TryParse(text, out var value) ? value :
                throw new ApiRequestException(400, "validation_failed", "Invalid media page.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.QueryAsync(context.Request.Query["library"].FirstOrDefault(),
                context.Request.Query["q"].FirstOrDefault(), page, ct, new MediaBrowseOptions(
                    context.Request.Query["label"].FirstOrDefault(), context.Request.Query["release"].FirstOrDefault(),
                    context.Request.Query["artist"].FirstOrDefault(), context.Request.Query["track"].FirstOrDefault(),
                    context.Request.Query["sort"].FirstOrDefault() ?? "title", context.Request.Query["direction"].FirstOrDefault() ?? "asc",
                    context.Request.Query["groupBy"].FirstOrDefault() ?? "none", context.Request.Query["groupKey"].FirstOrDefault())));
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
