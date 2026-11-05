using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Requests a reviewed pairwise merge into the label identified by the route.</summary>
public sealed record CreateLabelMergePreviewRequest(string RetiredLabelId);
/// <summary>Confirms a reviewed merge with a reason and optional unverified operator attribution.</summary>
public sealed record ApplyLabelMergeRequest(string Reason, string? Operator = null);
/// <summary>Payload-free participant identity shown in a merge review.</summary>
public sealed record MergeParticipantResponse(string Id, string? Name, string? SortName, string Version);
/// <summary>Immutable merge review; blockers prevent apply and expiry requires another preview.</summary>
public sealed record LabelMergePreviewResponse(
    string OperationId, DateTimeOffset ExpiresUtc, MergeParticipantResponse Survivor, MergeParticipantResponse Retired,
    IReadOnlyList<LabelAliasResponse> Aliases, IReadOnlyList<LabelReferenceResponse> ExternalReferences,
    IReadOnlyList<MergeReleaseLinkResponse> ReleaseLinks, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings,
    LabelProfileResponse? Profile, IReadOnlyList<ArtworkEntryRequest> Artwork, IReadOnlyList<MergeRelationshipResponse> Relationships);
/// <summary>A release association in the proposed resulting label.</summary>
public sealed record MergeReleaseLinkResponse(string ReleaseId, bool IsPrimary, string? Role);
/// <summary>Durable operation result, not a current survivor representation.</summary>
public sealed record LabelMergeOutcomeResponse(string OperationId, string AuditId, string CanonicalId, string CanonicalUri, DateTimeOffset AppliedUtc);

internal static class LabelMergeEndpoints
{
    public static void MapLabelMerges(this WebApplication app)
    {
        app.MapPost("/api/v1/labels/{survivorId}/merge-previews", PreviewAsync).WithTags("Label merges")
            .WithName("CreateLabelMergePreview").WithSummary("Preview retirement of a duplicate into this survivor")
            .WithDescription("Persists a 15-minute immutable review, without changing either label. Blockers must be resolved before apply.")
            .Accepts<CreateLabelMergePreviewRequest>("application/json").Produces<LabelMergePreviewResponse>(201)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(415);
        app.MapGet("/api/v1/label-merge-previews/{operationId}", GetAsync).WithTags("Label merges")
            .WithName("GetLabelMergePreview").WithSummary("Read a saved, payload-free merge review")
            .Produces<LabelMergePreviewResponse>().ProducesProblem(404).ProducesProblem(410);
        app.MapPost("/api/v1/label-merge-previews/{operationId}/apply", ApplyAsync).WithTags("Label merges")
            .WithName("ApplyLabelMerge").WithSummary("Apply a reviewed merge atomically")
            .WithDescription("Requires the review ETag. Validates both identities and dependencies. Identical retries return the saved outcome; writes to retired identities are rejected.")
            .Accepts<ApplyLabelMergeRequest>("application/json").Produces<LabelMergeOutcomeResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410)
            .ProducesProblem(412).ProducesProblem(415).ProducesProblem(428);
    }

    private static async Task<IResult> PreviewAsync(string survivorId, HttpContext context, ILabelMergeService service, CancellationToken ct)
    {
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["retiredLabelId"]);
        var retiredId = LabelRequests.RequiredString(fields, "retiredLabelId");
        var result = await service.PreviewAsync(survivorId, retiredId, ct);
        if (result.Preview is not { } preview) return Failure(context, result.Status);
        context.Response.Headers.ETag = LabelRequests.ETag(preview.Version);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Created($"/api/v1/label-merge-previews/{preview.OperationId}", ToResponse(preview));
    }

    private static async Task<IResult> GetAsync(string operationId, HttpContext context, ILabelMergeService service, TimeProvider clock, CancellationToken ct)
    {
        var result = await service.GetPreviewAsync(operationId, ct);
        if (result.Preview is not { } preview) return Failure(context, result.Status);
        if (preview.ExpiresUtc <= clock.GetUtcNow() && result.Outcome is null) return Failure(context, LabelMergeStatus.Expired);
        context.Response.Headers.ETag = LabelRequests.ETag(preview.Version);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(ToResponse(preview));
    }

    private static async Task<IResult> ApplyAsync(string operationId, HttpContext context, ILabelMergeService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(context.Request);
        var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["reason", "operator"]);
        var reason = LabelRequests.RequiredString(fields, "reason");
        var operatorName = fields.TryGetValue("operator", out var value) ? LabelRequests.NullableString(value, "operator") : null;
        var result = await service.ApplyAsync(operationId, version, reason, operatorName, ct);
        if (result.Outcome is not { } outcome) return Failure(context, result.Status);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new LabelMergeOutcomeResponse(outcome.OperationId, outcome.AuditId, outcome.CanonicalId,
            $"/api/v1/labels/{Uri.EscapeDataString(outcome.CanonicalId)}", outcome.AppliedUtc));
    }

    private static IResult Failure(HttpContext context, LabelMergeStatus status)
    {
        var (http, code, message) = status switch
        {
            LabelMergeStatus.NotFound => (404, "merge_not_found", "Merge review or participant not found."),
            LabelMergeStatus.Expired => (410, "merge_preview_expired", "This review expired. Create a fresh preview."),
            LabelMergeStatus.Stale => (412, "merge_state_changed", "The reviewed state changed. No merge was applied; create a fresh preview."),
            LabelMergeStatus.Blocked => (409, "merge_blocked", "Resolve the preview blockers before merging."),
            LabelMergeStatus.ReplayConflict => (409, "merge_replay_conflict", "This operation was already applied with different confirmation details."),
            _ => throw new InvalidOperationException("Unexpected merge result."),
        };
        return TypedResults.Problem(ApiErrors.Create(context, http, code, "Merge not applied", message));
    }

    private static LabelMergePreviewResponse ToResponse(LabelMergePreview preview) => new(
        preview.OperationId, preview.ExpiresUtc,
        new(preview.Survivor.Id, preview.Survivor.Name, preview.Survivor.SortName, preview.Survivor.Version),
        new(preview.Retired.Id, preview.Retired.Name, preview.Retired.SortName, preview.Retired.Version),
        preview.Aliases.Select(a => new LabelAliasResponse(a.Value, a.IsPrimary)).ToArray(),
        preview.ExternalReferences.Select(r => new LabelReferenceResponse(r.Source, r.ExternalId, r.IsPrimary, r.LastSeenUtc)).ToArray(),
        preview.ReleaseLinks.Select(l => new MergeReleaseLinkResponse(l.ReleaseId, l.IsPrimary, l.Role)).ToArray(),
        preview.Blockers, preview.Warnings, preview.Profile is null ? null : LabelEndpoints.ToProfileResponse(preview.Profile),
        (preview.Artwork ?? []).Select(item => new ArtworkEntryRequest(item.Id, item.AssetId, item.Role, item.Caption, item.IsPreferred)).ToArray(),
        (preview.Relationships ?? []).Select(LabelRelationshipEndpoints.ToMergeResponse).ToArray());
}
