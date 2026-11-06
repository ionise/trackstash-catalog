using System.Globalization;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Creates a fresh immutable review; optionally copies prior decisions and changes one bounded decision.</summary>
public sealed record CreateReleaseImportPreviewRequest(long ReleaseId, string? BasePreviewId = null, ReleaseImportDecisionRequest? Decision = null);
/// <summary>An identity decision; create, reuse, or artist-only unresolved compound. Evidence is an optional note; a standard note is recorded when omitted.</summary>
public sealed record ReleaseImportDecisionRequest(string Kind, long ProviderId, string Action, string? CanonicalId, string? Evidence);
/// <summary>Deliberate confirmation of the original review. Reason is optional (a standard reason is recorded) and must match when replaying.</summary>
public sealed record ApplyReleaseImportRequest(string? Reason, bool Confirmed);
/// <summary>Payload-free candidate identity; similarity is not identity proof.</summary>
public sealed record ImportCandidateResponse(string Id, string? Name, string? MixName, string? Isrc);
/// <summary>A source identity and its reviewed action, with existing candidate identities.</summary>
public sealed record ImportIdentityResponse(string Kind, long ProviderId, string Name, string Action, string? CanonicalId,
    bool CompoundReview, IReadOnlyList<ImportCandidateResponse> Candidates, string? MixName, string? Isrc,
    string? Evidence);
/// <summary>A frozen review summary. Identity and source track pages are read separately using the same immutable plan.</summary>
public sealed record ReleaseImportPreviewResponse(string OperationId, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc,
    long SourceReleaseId, string? Title, string ReleaseId, string LabelId, string SourceDigest, string? CatalogNumber,
    string? Upc, string? PublishDate, string? NewReleaseDate, bool NoOp, int IdentityCount, int TrackCount,
    int CreatedArtists, int CreatedRecordings, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings,
    ReleaseImportOutcomeResponse? Outcome, string? SupersededBy, IReadOnlyList<SourceArtistCredit> ReleaseCredits);
/// <summary>A stable result saved with the catalog transaction; counts describe this operation, not current totals.</summary>
public sealed record ReleaseImportOutcomeResponse(string OperationId, string ReleaseId, DateTimeOffset AppliedUtc,
    bool NoOp, int CreatedArtists, int CreatedRecordings, int CreatedPlacements, string AuditId);

internal static class ReleaseImportEndpoints
{
    public static void MapReleaseImports(this WebApplication app)
    {
        app.MapPost("/api/v1/release-import-previews", Create).WithName("CreateReleaseImportPreview").WithTags("Catalog imports")
            .WithSummary("Create an immutable complete-release review without canonical writes")
            .Accepts<CreateReleaseImportPreviewRequest>("application/json").Produces<ReleaseImportPreviewResponse>(201)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(503);
        app.MapGet("/api/v1/release-import-previews/{id}", Get).WithName("GetReleaseImportPreview").WithTags("Catalog imports")
            .WithSummary("Read a frozen import review and saved outcome").Produces<ReleaseImportPreviewResponse>().ProducesProblem(404).ProducesProblem(409);
        app.MapGet("/api/v1/release-import-previews/{id}/identities", Identities).WithName("GetReleaseImportIdentities").WithTags("Catalog imports")
            .WithSummary("Page frozen identity decisions and candidates").Produces<PurchaseSourcePage<ImportIdentityResponse>>().ProducesProblem(400).ProducesProblem(404);
        app.MapGet("/api/v1/release-import-previews/{id}/tracks", Tracks).WithName("GetReleaseImportTracks").WithTags("Catalog imports")
            .WithSummary("Page frozen source tracks and individual primary/remixer credits").Produces<PurchaseSourcePage<SourceReleaseTrack>>().ProducesProblem(400).ProducesProblem(404);
        app.MapPost("/api/v1/release-import-previews/{id}/apply", Apply).WithName("ApplyReleaseImport").WithTags("Catalog imports")
            .WithSummary("Apply a confirmed review atomically or return its original outcome")
            .WithDescription("Original If-Match and deliberate confirmation required. Rechecks source before transaction and catalog candidate state inside it. Changed intent conflicts; no automatic overwrite/retry.")
            .Accepts<ApplyReleaseImportRequest>("application/json").Produces<ReleaseImportOutcomeResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(412).ProducesProblem(428).ProducesProblem(503);
    }
    private static async Task<IResult> Create(HttpContext context, IReleaseImportService service, CancellationToken ct)
    {
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["releaseId", "basePreviewId", "decision"]);
        if (!fields.TryGetValue("releaseId", out var release) || release.ValueKind != JsonValueKind.Number || !release.TryGetInt64(out var releaseId))
            throw new ReleaseImportException(400, "validation_failed", "A positive numeric releaseId is required.");
        ReleaseImportDecision? choice = null;
        if (fields.TryGetValue("decision", out var decision) && decision.ValueKind != JsonValueKind.Null)
        {
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Any(p => p.Name is not ("kind" or "providerId" or "action" or "canonicalId" or "evidence")) ||
                decision.EnumerateObject().Select(p => p.Name).Distinct().Count() != decision.EnumerateObject().Count())
                throw new ReleaseImportException(400, "validation_failed", "Invalid decision fields.");
            string Required(string key) => decision.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()! : throw new ReleaseImportException(400, "validation_failed", $"Decision {key} is required.");
            if (!decision.TryGetProperty("providerId", out var providerId) || providerId.ValueKind != JsonValueKind.Number || !providerId.TryGetInt64(out var number))
                throw new ReleaseImportException(400, "validation_failed", "Decision providerId is required.");
            choice = new(Required("kind"), number, Required("action"),
                decision.TryGetProperty("canonicalId", out var canonical) ? LabelRequests.NullableString(canonical, "canonicalId") : null,
                decision.TryGetProperty("evidence", out var evidence) ? LabelRequests.NullableString(evidence, "evidence") ?? "" : "");
        }
        var prior = fields.TryGetValue("basePreviewId", out var baseId) ? LabelRequests.NullableString(baseId, "basePreviewId") : null;
        var result = await service.PreviewAsync(releaseId, prior, choice, ct);
        Headers(context, result.Plan);
        return TypedResults.Created($"/api/v1/release-import-previews/{result.Plan.OperationId}", Response(result));
    }
    private static async Task<IResult> Get(string id, HttpContext context, IReleaseImportService service, CancellationToken ct)
    {
        var result = await service.GetAsync(id, ct); Headers(context, result.Plan); return TypedResults.Ok(Response(result));
    }
    private static (int Page, int Size) Page(HttpRequest request)
    {
        foreach (var field in request.Query)
            if (field.Key is not ("page" or "pageSize") || field.Value.Count != 1)
                throw new ReleaseImportException(400, "validation_failed", "Unknown or repeated pagination field.");
        int Number(string name, int fallback) => !request.Query.TryGetValue(name, out var value) ? fallback :
            int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n :
            throw new ReleaseImportException(400, "validation_failed", "Invalid pagination.");
        var page = Number("page", 1); var size = Number("pageSize", 25);
        if (page < 1 || size is < 1 or > 100) throw new ReleaseImportException(400, "validation_failed", "Use page >=1 and pageSize 1-100.");
        return (page, size);
    }
    private static PurchaseSourcePage<T> Slice<T>(IReadOnlyList<T> items, (int Page, int Size) page) =>
        new(items.Skip((int)Math.Min(((long)page.Page - 1) * page.Size, items.Count)).Take(page.Size).ToArray(), items.Count, page.Page, page.Size);
    private static async Task<IResult> Identities(string id, HttpContext context, IReleaseImportService service, CancellationToken ct)
    {
        var page = Page(context.Request);
        var result = await service.GetAsync(id, ct); Headers(context, result.Plan);
        return TypedResults.Ok(Slice(result.Plan.Identities.Select(i => new ImportIdentityResponse(i.Kind, i.ProviderId, i.Name, i.Action, i.CanonicalId,
            i.CompoundReview, i.Candidates.Select(c => new ImportCandidateResponse(c.Id, c.Name, c.MixName, c.Isrc)).ToArray(), i.MixName, i.Isrc,
            result.Plan.Decisions.SingleOrDefault(d => d.Kind == i.Kind && d.ProviderId == i.ProviderId)?.Evidence)).ToArray(), page));
    }
    private static async Task<IResult> Tracks(string id, HttpContext context, IReleaseImportService service, CancellationToken ct)
    {
        var page = Page(context.Request); var result = await service.GetAsync(id, ct); Headers(context, result.Plan);
        return TypedResults.Ok(Slice(result.Plan.Source.Tracks.Items, page));
    }
    private static async Task<IResult> Apply(string id, HttpContext context, IReleaseImportService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["reason", "confirmed"]);
        var reason = fields.TryGetValue("reason", out var reasonValue) ? LabelRequests.NullableString(reasonValue, "reason") ?? "" : "";
        var confirmed = fields.TryGetValue("confirmed", out var value) && value.ValueKind == JsonValueKind.True;
        var result = await service.ApplyAsync(id, version, reason, confirmed, ct);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(Outcome(result));
    }
    private static void Headers(HttpContext context, ReleaseImportPlan plan)
    {
        context.Response.Headers.ETag = LabelRequests.ETag(plan.Version); context.Response.Headers.CacheControl = "no-store";
    }
    private static ReleaseImportOutcomeResponse Outcome(ReleaseImportOutcome outcome) =>
        new(outcome.OperationId, outcome.ReleaseId, outcome.AppliedUtc, outcome.NoOp, outcome.CreatedArtists, outcome.CreatedRecordings, outcome.CreatedPlacements, outcome.AuditId);
    private static ReleaseImportPreviewResponse Response(StoredReleaseImport result)
    {
        var p = result.Plan;
        return new(p.OperationId, p.CreatedUtc, p.ExpiresUtc, p.Source.ReleaseId, p.Source.Title, p.ProposedRelease.Id, p.LabelId, p.Source.BundleDigest,
            p.Source.CatalogNumber, p.Source.Upc, p.Source.PublishDate, p.Source.NewReleaseDate, p.NoOp, p.Identities.Count, p.Source.Tracks.Total,
            p.Artists.Count(a => a.Before is null), p.Recordings.Count(r => r.Before is null), p.Blockers, p.Warnings,
            result.Outcome is null ? null : Outcome(result.Outcome), result.SupersededBy, p.Source.Credits);
    }
}
