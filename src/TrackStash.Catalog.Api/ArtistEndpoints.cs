using System.Globalization;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>A canonical Artist with roles drawn from actual credits, optionally within a Label's releases.</summary>
public sealed record ArtistSummaryResponse(string Id, string? Name, string Kind, string IdentityStatus, IReadOnlyList<string> Roles);
/// <summary>A stable name/ID ordered Artist page.</summary>
public sealed record ArtistListResponse(IReadOnlyList<ArtistSummaryResponse> Items, int Total, int Page, int PageSize);
/// <summary>Safe Artist metadata and identities, excluding private source payloads.</summary>
public sealed record ArtistResponse(string Id, string? Name, string? SortName, string Version, string Kind, string IdentityStatus,
    IReadOnlyList<string> Aliases, IReadOnlyList<ReleaseReferenceResponse> ExternalReferences);
/// <summary>An explicit Artist credit with its release, placement or Recording scope; no label affiliation is inferred.</summary>
public sealed record ArtistCreditListingResponse(string CreditId, string? ReleaseId, string? ReleaseTitle, string? RecordingId,
    string? PlacementId, string? TrackTitle, string? MixName, string Role, string Scope, string? CreditedName, int? Position);
/// <summary>A page of explicit credited appearances, preserving repeated placements and separate scopes.</summary>
public sealed record ArtistCreditsResponse(IReadOnlyList<ArtistCreditListingResponse> Items, int Total, int Page, int PageSize);
/// <summary>Editable manual Artist profile; private notes are excluded from Featured and canonical text documents.</summary>
public sealed record ArtistProfileResponse(string? Biography, string? BiographySourceUrl, bool Favourite, string? Notes,
    string ReviewStatus, IReadOnlyList<ArtistWebLinkResponse> Links);
/// <summary>All six manual profile fields; use null for optional empty text and the original Artist If-Match.</summary>
public sealed record ReplaceArtistProfileRequest(string? Biography, string? BiographySourceUrl, bool Favourite, string? Notes,
    string ReviewStatus, IReadOnlyList<ArtistWebLinkResponse> Links);
/// <summary>A curated web link with optional attribution.</summary>
public sealed record ArtistWebLinkResponse(string Kind, string Url, string? Title, string? SourceUrl);
/// <summary>Credit roles and their evidence locations, without duplicate appearance rows.</summary>
public sealed record ArtistAppearanceCreditResponse(string Role, string Scope, string? CreditedName);
/// <summary>A Label explicitly linked to this release, not an Artist signing affiliation.</summary>
public sealed record ArtistAppearanceLabelResponse(string Id, string? Name, bool IsPrimary);
/// <summary>One distinct release or credited placement; unplaced Recordings retain null release/placement IDs.</summary>
public sealed record ArtistAppearanceResponse(string? ReleaseId, string? ReleaseTitle, string? RecordingId,
    string? PlacementId, string? TrackTitle, string? MixName, string? CoverAssetId,
    IReadOnlyList<ArtistAppearanceLabelResponse> Labels, IReadOnlyList<ArtistAppearanceCreditResponse> Credits);
/// <summary>A server-paged list counting appearances rather than individual credit scopes.</summary>
public sealed record ArtistAppearancesResponse(IReadOnlyList<ArtistAppearanceResponse> Items, int Total, int Page, int PageSize);

internal static class ArtistEndpoints
{
    public static void MapArtists(this WebApplication app)
    {
        app.MapGet("/api/v1/artists/{id}/track-appearances", TrackAppearances).WithName("GetArtistTrackAppearances").WithTags("Artists")
            .WithSummary("Page distinct credited placements, aggregating Recording and placement evidence")
            .Produces<ArtistAppearancesResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/artists/{id}/release-appearances", ReleaseAppearances).WithName("GetArtistReleaseAppearances").WithTags("Artists")
            .WithSummary("Page distinct releases with credit evidence, linked Labels and preferred managed cover")
            .Produces<ArtistAppearancesResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/artists/{id}/profile", Profile).WithName("GetArtistProfile").WithTags("Artists")
            .WithSummary("Read manual Artist profile and original aggregate ETag")
            .Produces<ArtistProfileResponse>().ProducesProblem(404).ProducesProblem(409);
        app.MapPatch("/api/v1/artists/{id}/profile", SaveProfile).WithName("SaveArtistProfile").WithTags("Artists")
            .WithSummary("Conditionally replace the manual profile with permanent before/after history")
            .Accepts<ReplaceArtistProfileRequest>("application/json").Produces<ArtistProfileResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/artists", List).WithName("ListCatalogArtists").WithTags("Artists")
            .WithSummary("Search Artists by name, sort name or alias, optionally by credited Label context and role")
            .Produces<ArtistListResponse>().ProducesProblem(400).ProducesProblem(409);
        app.MapGet("/api/v1/artists/{id}", Get).WithName("GetCatalogArtist").WithTags("Artists")
            .WithSummary("Read canonical Artist identity, aliases and provider references")
            .Produces<ArtistResponse>().ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/artists/{id}/releases", Releases).WithName("GetArtistReleaseCredits").WithTags("Artists")
            .WithSummary("Page explicit credits on releases, retaining original credit scopes")
            .Produces<ArtistCreditsResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/artists/{id}/tracks", Tracks).WithName("GetArtistTrackCredits").WithTags("Artists")
            .WithSummary("Page placement and Recording credits, including unplaced Recordings")
            .Produces<ArtistCreditsResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
    }
    private static ArtistQuery Query(HttpRequest request, bool credits = false)
    {
        foreach (var field in request.Query)
            if (field.Value.Count != 1 || !(field.Key is "page" or "pageSize" || !credits && (field.Key is "q" or "labelId" or "role")))
                throw new ApiRequestException(400, "validation_failed", "Unknown or repeated Artist query field.");
        int Number(string key, int fallback) => !request.Query.TryGetValue(key, out var value) ? fallback :
            int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number :
            throw new ApiRequestException(400, "validation_failed", "Invalid Artist pagination.");
        var query = new ArtistQuery(Number("page", 1), Number("pageSize", 25),
            request.Query["q"].FirstOrDefault(), request.Query["labelId"].FirstOrDefault(), request.Query["role"].FirstOrDefault());
        query.Validate(); return query;
    }
    private static async Task<IResult> List(HttpContext context, IArtistService service, CancellationToken ct)
    {
        var result = await service.QueryAsync(Query(context.Request), ct);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new ArtistListResponse(result.Items.Select(a => new ArtistSummaryResponse(a.Id, a.Name, a.Kind, a.IdentityStatus, a.Roles)).ToArray(),
            result.Total, result.Page, result.PageSize));
    }
    private static async Task<IResult> Get(string id, HttpContext context, IArtistService service, CancellationToken ct)
    {
        var artist = await service.GetAsync(id, ct) ?? throw Missing();
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.ETag = LabelRequests.ETag(artist.Version);
        return TypedResults.Ok(new ArtistResponse(artist.Id, artist.Name, artist.SortName, artist.Version, artist.Kind, artist.IdentityStatus,
            artist.Aliases, artist.ExternalReferences.Select(r => new ReleaseReferenceResponse(r.Source, r.ExternalId, r.IsPrimary)).ToArray()));
    }
    private static Task<IResult> Releases(string id, HttpContext context, IArtistService service, CancellationToken ct) => Credits(id, false, context, service, ct);
    private static Task<IResult> Tracks(string id, HttpContext context, IArtistService service, CancellationToken ct) => Credits(id, true, context, service, ct);
    private static async Task<IResult> Credits(string id, bool tracks, HttpContext context, IArtistService service, CancellationToken ct)
    {
        var query = Query(context.Request, true);
        var result = await service.CreditsAsync(new(id, tracks, query.Page, query.PageSize), ct) ?? throw Missing();
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new ArtistCreditsResponse(result.Items.Select(c => new ArtistCreditListingResponse(c.CreditId, c.ReleaseId,
            c.ReleaseTitle, c.RecordingId, c.PlacementId, c.TrackTitle, c.MixName, c.Role, c.Scope, c.CreditedName, c.Position)).ToArray(),
            result.Total, result.Page, result.PageSize));
    }
    private static ApiRequestException Missing() => new(404, "artist_not_found", "Catalog Artist was not found.");
    private static Task<IResult> TrackAppearances(string id, HttpContext context, IArtistService service, CancellationToken ct)
        => Appearances(id, true, context, service, ct);
    private static Task<IResult> ReleaseAppearances(string id, HttpContext context, IArtistService service, CancellationToken ct)
        => Appearances(id, false, context, service, ct);
    private static async Task<IResult> Appearances(string id, bool tracks, HttpContext context, IArtistService service, CancellationToken ct)
    {
        var query = Query(context.Request, true);
        var result = await service.AppearancesAsync(new(id, tracks, query.Page, query.PageSize), ct) ?? throw Missing();
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new ArtistAppearancesResponse(result.Items.Select(item => new ArtistAppearanceResponse(
            item.ReleaseId, item.ReleaseTitle, item.RecordingId, item.PlacementId, item.TrackTitle, item.MixName, item.CoverAssetId,
            item.Labels.Select(l => new ArtistAppearanceLabelResponse(l.Id, l.Name, l.IsPrimary)).ToArray(),
            item.Credits.Select(c => new ArtistAppearanceCreditResponse(c.Role, c.Scope, c.CreditedName)).ToArray())).ToArray(),
            result.Total, result.Page, result.PageSize));
    }
    private static ArtistProfileResponse ProfileResponse(ArtistProfile profile) => new(profile.Biography, profile.BiographySourceUrl,
        profile.Favourite, profile.Notes, profile.ReviewStatus, profile.Links.Select(l => new ArtistWebLinkResponse(l.Kind, l.Url, l.Title, l.SourceUrl)).ToArray());
    private static async Task<IResult> Profile(string id, HttpContext context, IArtistService service, CancellationToken ct)
    {
        var result = await service.GetProfileAsync(id, ct) ?? throw Missing();
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.ETag = LabelRequests.ETag(result.Version);
        return TypedResults.Ok(ProfileResponse(result.Profile));
    }
    private static async Task<IResult> SaveProfile(string id, HttpContext context, IArtistService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct,
            ["biography", "biographySourceUrl", "favourite", "notes", "reviewStatus", "links"]);
        var missing = new[] { "biography", "biographySourceUrl", "favourite", "notes", "reviewStatus", "links" }.Where(field => !fields.ContainsKey(field)).ToArray();
        if (missing.Length > 0) throw new ApiRequestException(400, "validation_failed",
            $"Missing Artist profile fields: {string.Join(", ", missing)}. Use null for empty optional text.", missing[0]);
        var profile = new ArtistProfile {
            Biography = LabelRequests.NullableString(fields["biography"], "biography"),
            BiographySourceUrl = LabelRequests.NullableString(fields["biographySourceUrl"], "biographySourceUrl"),
            Notes = LabelRequests.NullableString(fields["notes"], "notes"),
            Favourite = fields["favourite"].ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false,
                _ => throw new ApiRequestException(400, "validation_failed", "favourite must be a boolean.") },
            ReviewStatus = LabelRequests.RequiredString(fields, "reviewStatus"), Links = LabelProfileRequests.ReadLinks(fields["links"]),
        };
        var result = await service.SaveProfileAsync(id, version, profile, ct);
        if (result.Status == LabelUpdateStatus.NotFound) throw Missing();
        if (result.Status == LabelUpdateStatus.Stale) return TypedResults.Problem(ApiErrors.Create(context, 412, "stale_version", "Artist changed. Reload the profile before editing again."));
        var saved = result.Snapshot ?? throw new InvalidOperationException("Profile write returned no snapshot.");
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.ETag = LabelRequests.ETag(saved.Version);
        return TypedResults.Ok(ProfileResponse(saved.Profile));
    }
}
