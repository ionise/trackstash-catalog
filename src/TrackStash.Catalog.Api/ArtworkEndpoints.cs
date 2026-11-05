using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Shared image metadata and entity-specific gallery presentation. Content URLs require bearer authentication.</summary>
public sealed record ArtworkItemResponse(
    string Id, string AssetId, string Role, string? Caption, bool IsPreferred,
    string MediaType, long ByteLength, int? Width, int? Height, string? OriginalFilename,
    string? SourceUrl, string? Attribution, string UsageRights, DateTimeOffset CreatedUtc,
    string OriginalUrl, string ThumbnailUrl);
/// <summary>An ordered label gallery protected by the label's aggregate ETag.</summary>
public sealed record LabelArtworkResponse(string LabelId, IReadOnlyList<ArtworkItemResponse> Items);
/// <summary>An ordered managed release gallery protected by the release ETag.</summary>
public sealed record ReleaseArtworkResponse(string ReleaseId, IReadOnlyList<ArtworkItemResponse> Items);
/// <summary>A managed Artist portrait/gallery protected by the Artist aggregate ETag.</summary>
public sealed record ArtistArtworkResponse(string ArtistId, IReadOnlyList<ArtworkItemResponse> Items);
/// <summary>A provider-supplied release artwork observation, not attached artwork or a permission grant.</summary>
public sealed record ReleaseArtworkCandidateResponse(string Provider, string CandidateId, string Revision, string Title, string SourceUrl, string? Attribution);
/// <summary>Configured artwork providers and their explicitly requested candidates.</summary>
public sealed record ReleaseArtworkCandidatesResponse(IReadOnlyList<string> Providers, IReadOnlyList<ReleaseArtworkCandidateResponse> Items);
/// <summary>Explicit attachment of an opaque, release-scoped provider candidate with original version and usage rights.</summary>
public sealed record AttachReleaseArtworkRequest(string Provider, string CandidateId, string Revision, string UsageRights, bool Confirmed);
/// <summary>Full ordered replacement of existing gallery entries; omission detaches an entry without deleting its asset.</summary>
public sealed record ReplaceLabelArtworkRequest(IReadOnlyList<ArtworkEntryRequest> Items);
/// <summary>Full ordered replacement of existing Release gallery entries; omission retains the asset and permanent history.</summary>
public sealed record ReplaceReleaseArtworkRequest(IReadOnlyList<ArtworkEntryRequest> Items);
/// <summary>The complete ordered Artist gallery; omitted associations are detached, not deleted.</summary>
public sealed record ReplaceArtistArtworkRequest(IReadOnlyList<ArtworkEntryRequest> Items);
/// <summary>An existing association whose role, caption, order and preference may be changed.</summary>
public sealed record ArtworkEntryRequest(string Id, string AssetId, string Role, string? Caption, bool IsPreferred);
/// <summary>A bounded JPEG/PNG upload with mandatory usage-rights text. Source and attribution are optional.</summary>
public sealed record UploadArtworkRequest(
    IFormFile File, string Role, string UsageRights, string? SourceUrl, string? Attribution, string? Caption, bool IsPreferred, string? ReplaceEntryId);

internal static class ArtworkEndpoints
{
    private const int UploadBodyLimit = ArtworkRules.MaximumBytes + 64 * 1024;
    public static void MapArtwork(this WebApplication app)
    {
        app.MapGet("/api/v1/artists/{id}/artwork", GetArtistAsync).WithTags("Artwork").WithName("GetArtistArtwork")
            .WithSummary("Read managed Artist portrait and gallery").Produces<ArtistArtworkResponse>().ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/artists/{id}/artwork", UploadArtistAsync).WithTags("Artwork").WithName("UploadArtistArtwork")
            .WithSummary("Upload or replace static Artist portrait/image with recorded rights and original Artist ETag")
            .Accepts<UploadArtworkRequest>("multipart/form-data").Produces<ArtistArtworkResponse>(201)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(413).ProducesProblem(415).ProducesProblem(428);
        app.MapPatch("/api/v1/artists/{id}/artwork", ReplaceArtistAsync).WithTags("Artwork").WithName("ReplaceArtistArtwork")
            .WithSummary("Conditionally edit, order, prefer or detach existing Artist artwork")
            .Accepts<ReplaceArtistArtworkRequest>("application/json").Produces<ArtistArtworkResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/releases/{id}/artwork", GetReleaseAsync).WithTags("Artwork").WithName("GetReleaseArtwork")
            .WithSummary("Read managed release covers and gallery").Produces<ReleaseArtworkResponse>().ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/releases/{id}/artwork", UploadReleaseAsync).WithTags("Artwork").WithName("UploadReleaseArtwork")
            .WithSummary("Upload a managed release cover or gallery image with provenance")
            .WithDescription("Multipart JPEG/PNG <=10 MiB, total body <=10 MiB +64 KiB, <=25 megapixels, static images only. Roles cover/image; usageRights and original Release ETag required. Optional replaceEntryId retains entry order and old asset history.")
            .Accepts<UploadArtworkRequest>("multipart/form-data").Produces<ReleaseArtworkResponse>(201)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(413).ProducesProblem(415).ProducesProblem(428);
        app.MapPatch("/api/v1/releases/{id}/artwork", ReplaceReleaseAsync).WithTags("Artwork").WithName("ReplaceReleaseArtwork")
            .WithSummary("Edit, order, prefer or detach existing release artwork; history and assets are retained")
            .Accepts<ReplaceReleaseArtworkRequest>("application/json").Produces<ReleaseArtworkResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/releases/{id}/artwork-candidates", CandidatesAsync).WithTags("Artwork").WithName("FindReleaseArtworkCandidates")
            .WithSummary("Explicitly query configured artwork adapters; none are enabled by default")
            .Produces<ReleaseArtworkCandidatesResponse>().ProducesProblem(404).ProducesProblem(409);
        app.MapPost("/api/v1/releases/{id}/artwork-candidates/attach", AttachAsync).WithTags("Artwork").WithName("AttachReleaseArtworkCandidate")
            .WithSummary("Explicitly materialize a provider candidate as managed artwork after rights confirmation")
            .Accepts<AttachReleaseArtworkRequest>("application/json").Produces<ReleaseArtworkResponse>(201)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/labels/{id}/artwork", GetAsync).WithTags("Artwork")
            .WithName("GetLabelArtwork").WithSummary("Read the ordered label gallery and aggregate ETag")
            .Produces<LabelArtworkResponse>().Produces(308).ProducesProblem(404);
        app.MapPost("/api/v1/labels/{id}/artwork", UploadAsync).WithTags("Artwork")
            .WithName("UploadLabelArtwork").WithSummary("Validate a local JPEG/PNG, create its asset and append to the gallery")
            .WithDescription("Multipart form: file, role (logo/image), usageRights, sourceUrl, attribution, caption, isPreferred (true/false), optional replaceEntryId for atomic in-place replacement. File <=10 MiB, total body <=10 MiB +64 KiB, <=25 megapixels, static images only. Requires original label ETag; source/rights are retained without remote fetching.")
            .Accepts<UploadArtworkRequest>("multipart/form-data").Produces<LabelArtworkResponse>(201)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(413).ProducesProblem(415).ProducesProblem(428);
        app.MapPatch("/api/v1/labels/{id}/artwork", ReplaceAsync).WithTags("Artwork")
            .WithName("ReplaceLabelArtwork").WithSummary("Reorder, edit or detach gallery associations conditionally")
            .WithDescription("Full ordered replacement of existing entries. At most one preferred image. Empty items detaches all; original assets and revision history are retained. Asset provenance is immutable; re-upload to replace.")
            .Accepts<ReplaceLabelArtworkRequest>("application/json").Produces<LabelArtworkResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/artwork-assets/{assetId}/original", OriginalAsync).WithTags("Artwork")
            .WithName("GetArtworkOriginal").WithSummary("Read authenticated original image bytes")
            .Produces(200, contentType: "image/png").Produces(200, contentType: "image/jpeg").ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/artwork-assets/{assetId}/thumbnail", ThumbnailAsync).WithTags("Artwork")
            .WithName("GetArtworkThumbnail").WithSummary("Read an authenticated JPEG thumbnail (maximum 512 pixels)")
            .Produces(200, contentType: "image/jpeg").ProducesProblem(404).ProducesProblem(409);
    }

    private static async Task<IResult> GetAsync(string id, HttpContext context, IArtworkService service, ILabelMergeService merges, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var identity = await merges.ResolveAsync(id, ct);
        if (identity.IsRetired)
            return TypedResults.Redirect($"/api/v1/labels/{Uri.EscapeDataString(identity.CanonicalId!)}/artwork", permanent: true, preserveMethod: true);
        var gallery = await service.GetLabelAsync(id, ct);
        if (gallery is null) return Missing(context);
        context.Response.Headers.ETag = LabelRequests.ETag(gallery.Version);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(ToResponse(gallery));
    }

    private static Task<IResult> UploadAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
        => UploadForAsync(id, context, service, false, ct);
    private static Task<IResult> UploadReleaseAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
        => UploadForAsync(id, context, service, true, ct);
    private static Task<IResult> UploadArtistAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
        => UploadForAsync(id, context, service, false, ct, true);
    private static async Task<IResult> UploadForAsync(string id, HttpContext context, IArtworkService service, bool release, CancellationToken ct, bool artist = false)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        if (!context.Request.HasFormContentType || !context.Request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ApiRequestException(415, "unsupported_media_type", "Use multipart/form-data with one file.");
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = UploadBodyLimit;
        if (context.Request.ContentLength > UploadBodyLimit)
            throw new ApiRequestException(413, "body_too_large", "Upload body exceeds 10 MiB plus 64 KiB.");
        using var buffer = await BoundedBodyAsync(context.Request.Body, UploadBodyLimit, ct);
        context.Request.Body = buffer;
        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = UploadBodyLimit, MemoryBufferThreshold = UploadBodyLimit,
                ValueCountLimit = 8, ValueLengthLimit = 2048, KeyLengthLimit = 64, MultipartHeadersLengthLimit = 8192,
            }, ct);
        }
        catch (InvalidDataException)
        {
            throw new ApiRequestException(400, "validation_failed", "Malformed multipart form or upload field limits exceeded.");
        }
        var allowed = new[] { "role", "usageRights", "sourceUrl", "attribution", "caption", "isPreferred", "replaceEntryId" };
        if (form.Any(f => !allowed.Contains(f.Key, StringComparer.Ordinal) || f.Value.Count != 1)
            || form.Files.Count != 1 || form.Files[0].Name != "file")
            throw new ApiRequestException(400, "validation_failed", "Supply exactly one file and only documented, unrepeated fields.");
        var file = form.Files[0];
        if (file.Length > ArtworkRules.MaximumBytes)
            throw new ApiRequestException(413, "body_too_large", "Image file exceeds 10 MiB.");
        using var content = new MemoryStream();
        await file.CopyToAsync(content, ct);
        var preferred = Text("isPreferred") switch { "true" => true, "false" or null => false,
            _ => throw new ApiRequestException(400, "validation_failed", "isPreferred must be true or false.") };
        var upload = new ArtworkUpload(content.ToArray(), file.FileName,
            Text("sourceUrl"), Text("attribution"), Text("usageRights") ?? "", Text("role") ?? (artist ? "portrait" : release ? "cover" : "image"), Text("caption"), preferred, Text("replaceEntryId"));
        var result = artist ? await service.UploadArtistAsync(id, version, upload, ct) : release ? await service.UploadReleaseAsync(id, version, upload, ct) : await service.UploadLabelAsync(id, version, upload, ct);
        return WriteResult(context, result, created: true, release, artist);
        string? Text(string key) => form.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value[0]) ? value[0] : null;
    }

    private static async Task<MemoryStream> BoundedBodyAsync(Stream body, int maximum, CancellationToken ct)
    {
        var result = new MemoryStream();
        try
        {
            var chunk = new byte[64 * 1024];
            int count;
            while ((count = await body.ReadAsync(chunk, ct)) != 0)
            {
                if (result.Length + count > maximum)
                    throw new ApiRequestException(413, "body_too_large", "Artwork request exceeds its size limit.");
                await result.WriteAsync(chunk.AsMemory(0, count), ct);
            }
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static async Task<IResult> ReplaceAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var entries = await ReadEntriesAsync(context, ct);
        return WriteResult(context, await service.ReplaceLabelAsync(id, version, entries, ct), created: false);
    }
    private static async Task<ArtworkEntry[]> ReadEntriesAsync(HttpContext context, CancellationToken ct)
    {
        using var body = await BoundedBodyAsync(context.Request.Body, 16 * 1024, ct);
        context.Request.Body = body;
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["items"]);
        if (fields.Count != 1 || fields["items"].ValueKind != JsonValueKind.Array)
            throw new ApiRequestException(400, "validation_failed", "Supply an items array.");
        var entries = fields["items"].EnumerateArray().Select(item =>
        {
            var properties = item.ValueKind == JsonValueKind.Object ? item.EnumerateObject().ToArray() : [];
            var keys = new[] { "id", "assetId", "role", "caption", "isPreferred" };
            if (properties.Length != keys.Length || keys.Any(key => properties.Count(p => p.Name == key) != 1))
                throw new ApiRequestException(400, "validation_failed", "Each item requires exactly id, assetId, role, caption and isPreferred.");
            var values = properties.ToDictionary(p => p.Name, p => p.Value);
            return new ArtworkEntry(LabelRequests.RequiredString(values, "id"), LabelRequests.RequiredString(values, "assetId"),
                LabelRequests.RequiredString(values, "role"), LabelRequests.NullableString(values["caption"], "caption"),
                values["isPreferred"].ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false,
                    _ => throw new ApiRequestException(400, "validation_failed", "isPreferred must be a boolean.") });
        }).ToArray();
        return entries;
    }

    private static IResult WriteResult(HttpContext context, ArtworkWriteResult result, bool created, bool release = false, bool artist = false)
    {
        if (result.Status == LabelUpdateStatus.NotFound) return artist ? ArtistMissing(context) : release ? ReleaseMissing(context) : Missing(context);
        if (result.Status == LabelUpdateStatus.Stale)
            return TypedResults.Problem(ApiErrors.Create(context, 412, "stale_version", "Catalog entity changed. Reload artwork before editing again."));
        var gallery = result.Gallery ?? throw new InvalidOperationException("Artwork write did not return a gallery.");
        context.Response.Headers.ETag = LabelRequests.ETag(gallery.Version);
        context.Response.Headers.CacheControl = "no-store";
        if (artist)
            return created ? TypedResults.Created($"/api/v1/artwork-assets/{result.CreatedAssetId ?? throw new InvalidOperationException("Created asset ID is missing.")}/original", new ArtistArtworkResponse(gallery.EntityId, Items(gallery)))
                : TypedResults.Ok(new ArtistArtworkResponse(gallery.EntityId, Items(gallery)));
        if (release)
            return created ? TypedResults.Created($"/api/v1/artwork-assets/{result.CreatedAssetId ?? throw new InvalidOperationException("Created asset ID is missing.")}/original", new ReleaseArtworkResponse(gallery.EntityId, Items(gallery)))
                : TypedResults.Ok(new ReleaseArtworkResponse(gallery.EntityId, Items(gallery)));
        return created ? TypedResults.Created($"/api/v1/artwork-assets/{result.CreatedAssetId ?? throw new InvalidOperationException("Created asset ID is missing.")}/original", ToResponse(gallery))
            : TypedResults.Ok(ToResponse(gallery));
    }

    private static Task<IResult> OriginalAsync(string assetId, HttpContext context, IArtworkService service, CancellationToken ct)
        => ContentAsync(assetId, false, context, service, ct);
    private static Task<IResult> ThumbnailAsync(string assetId, HttpContext context, IArtworkService service, CancellationToken ct)
        => ContentAsync(assetId, true, context, service, ct);
    private static async Task<IResult> ContentAsync(string assetId, bool thumbnail, HttpContext context, IArtworkService service, CancellationToken ct)
    {
        var content = await service.ReadAsync(assetId, thumbnail, ct);
        if (content is null) return TypedResults.Problem(ApiErrors.Create(context, 404, "artwork_not_found", "Artwork not found"));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return TypedResults.Bytes(content.Bytes, content.MediaType);
    }

    private static IResult Missing(HttpContext context) => TypedResults.Problem(ApiErrors.Create(context, 404, "label_not_found", "Label not found"));
    private static LabelArtworkResponse ToResponse(ArtworkDetail gallery) => new(gallery.EntityId, Items(gallery));
    private static ArtworkItemResponse[] Items(ArtworkDetail gallery) => gallery.Items.Select(item => new ArtworkItemResponse(
        item.Entry.Id, item.Asset.Id, item.Entry.Role, item.Entry.Caption, item.Entry.IsPreferred, item.Asset.MediaType,
        item.Asset.ByteLength, item.Asset.Width, item.Asset.Height, item.Asset.OriginalFilename, item.Asset.SourceUrl,
        item.Asset.Attribution, item.Asset.UsageRights, item.Asset.CreatedUtc,
        $"/api/v1/artwork-assets/{item.Asset.Id}/original", $"/api/v1/artwork-assets/{item.Asset.Id}/thumbnail")).ToArray();
    private static IResult ReleaseMissing(HttpContext context) => TypedResults.Problem(ApiErrors.Create(context, 404, "release_not_found", "Release not found"));
    private static IResult ArtistMissing(HttpContext context) => TypedResults.Problem(ApiErrors.Create(context, 404, "artist_not_found", "Artist not found"));
    private static async Task<IResult> GetArtistAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var gallery = await service.GetArtistAsync(id, ct);
        if (gallery is null) return ArtistMissing(context);
        context.Response.Headers.ETag = LabelRequests.ETag(gallery.Version);
        return TypedResults.Ok(new ArtistArtworkResponse(id, Items(gallery)));
    }
    private static async Task<IResult> ReplaceArtistAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var items = await ReadEntriesAsync(context, ct);
        return WriteResult(context, await service.ReplaceArtistAsync(id, version, items, ct), false, artist: true);
    }
    private static async Task<IResult> GetReleaseAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var gallery = await service.GetReleaseAsync(id, ct);
        if (gallery is null) return ReleaseMissing(context);
        context.Response.Headers.ETag = LabelRequests.ETag(gallery.Version);
        return TypedResults.Ok(new ReleaseArtworkResponse(id, Items(gallery)));
    }
    private static async Task<IResult> CandidatesAsync(string id, HttpContext context, IReleaseArtworkSourceService service, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await service.FindAsync(id, ct);
        return TypedResults.Ok(new ReleaseArtworkCandidatesResponse(result.Providers, result.Items.Select(c =>
            new ReleaseArtworkCandidateResponse(c.Provider, c.CandidateId, c.Revision, c.Title, c.SourceUrl, c.Attribution)).ToArray()));
    }
    private static async Task<IResult> AttachAsync(string id, HttpContext context, IReleaseArtworkSourceService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["provider", "candidateId", "revision", "usageRights", "confirmed"]);
        var result = await service.AttachAsync(id, version, LabelRequests.RequiredString(fields, "provider"),
            LabelRequests.RequiredString(fields, "candidateId"), LabelRequests.RequiredString(fields, "revision"),
            LabelRequests.RequiredString(fields, "usageRights"), fields.TryGetValue("confirmed", out var value) && value.ValueKind == JsonValueKind.True, ct);
        return WriteResult(context, result, true, true);
    }
    private static async Task<IResult> ReplaceReleaseAsync(string id, HttpContext context, IArtworkService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var items = await ReadEntriesAsync(context, ct);
        return WriteResult(context, await service.ReplaceReleaseAsync(id, version, items, ct), false, true);
    }
}
