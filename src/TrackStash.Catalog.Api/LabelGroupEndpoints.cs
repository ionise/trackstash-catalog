using System.Globalization;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>A family/brand or organisation, distinct from a release label.</summary>
public sealed record LabelGroupResponse(string Id, string Kind, string Name, string? SortName, string? Description,
    string? Website, string Version, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, int CurrentMembers);
/// <summary>Creates a group without implying ownership of its members.</summary>
public sealed record CreateLabelGroupRequest(string Kind, string Name, string? SortName, string? Description, string? Website);
/// <summary>Optional scalar group fields; omission preserves existing values.</summary>
public sealed record UpdateLabelGroupRequest(string? Name, string? SortName, string? Description, string? Website);
/// <summary>A reviewed affiliation, latest known period, evidence and endpoint versions.</summary>
public sealed record LabelGroupMembershipResponse(string Id, RelatedLabelResponse Label, LabelGroupResponse Group,
    string Status, string? Since, string? Until, string Source, string? SourceUrl, string? Notes,
    string Version, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
/// <summary>Creates a sourced affiliation using last-read label and group versions.</summary>
public sealed record CreateGroupMembershipRequest(string LabelId, string GroupId, string LabelVersion, string GroupVersion,
    string Status, string? Since, string? Until, string Source, string? SourceUrl, string? Notes, bool ConfirmRejoin = false);
/// <summary>Replaces all membership metadata, retaining immutable endpoints and requiring original If-Match.</summary>
public sealed record UpdateGroupMembershipRequest(string LabelVersion, string GroupVersion, string Status,
    string? Since, string? Until, string Source, string? SourceUrl, string? Notes, bool ConfirmRejoin = false);
/// <summary>A bounded page with total count before pagination.</summary>
public sealed record GroupPageResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize, IReadOnlyDictionary<string, int>? Counts = null);
/// <summary>A shared parent, family or organisation supporting a related-label result.</summary>
public sealed record RelatedLabelReasonResponse(string Kind, string Id, string Name);
/// <summary>A distinct related label and every matching supporting reason.</summary>
public sealed record RelatedLabelItemResponse(RelatedLabelResponse Label, IReadOnlyList<RelatedLabelReasonResponse> Reasons);

internal static class LabelGroupEndpoints
{
    private static readonly string[] Metadata = ["status", "since", "until", "source", "sourceUrl", "notes", "labelVersion", "groupVersion", "confirmRejoin"];
    public static void MapLabelGroups(this WebApplication app)
    {
        app.MapGet("/api/v1/label-groups", Query).WithName("QueryLabelGroups").WithTags("Label groups")
            .WithSummary("Search distinct family and organisation groups").Produces<GroupPageResponse<LabelGroupResponse>>().ProducesProblem(400);
        app.MapPost("/api/v1/label-groups", Create).WithName("CreateLabelGroup").WithTags("Label groups")
            .WithSummary("Create a family or organisation group").Accepts<CreateLabelGroupRequest>("application/json")
            .Produces<LabelGroupResponse>(201).ProducesProblem(400).ProducesProblem(415);
        app.MapGet("/api/v1/label-groups/{id}", Get).WithName("GetLabelGroup").WithTags("Label groups")
            .WithSummary("Read a group and its aggregate ETag").Produces<LabelGroupResponse>().ProducesProblem(404);
        app.MapPatch("/api/v1/label-groups/{id}", Update).WithName("UpdateLabelGroup").WithTags("Label groups")
            .WithSummary("Conditionally edit group scalar fields; kind is immutable").Accepts<UpdateLabelGroupRequest>("application/merge-patch+json")
            .Produces<LabelGroupResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(412).ProducesProblem(415).ProducesProblem(428);
        app.MapGet("/api/v1/label-groups/{id}/members", GroupMembers).WithName("GetGroupMembers").WithTags("Label groups")
            .WithSummary("Page group members by current, former, unknown or all status").Produces<GroupPageResponse<LabelGroupMembershipResponse>>().ProducesProblem(400).ProducesProblem(404);
        app.MapGet("/api/v1/labels/{id}/group-memberships", LabelMembers).WithName("GetLabelGroupMemberships").WithTags("Label groups")
            .WithSummary("Page a label's family and organisation memberships").Produces<GroupPageResponse<LabelGroupMembershipResponse>>().Produces(308).ProducesProblem(400).ProducesProblem(404);
        app.MapGet("/api/v1/label-group-memberships/{id}", GetMembership).WithName("GetGroupMembership").WithTags("Label groups")
            .WithSummary("Read a membership with its original edit token").Produces<LabelGroupMembershipResponse>().ProducesProblem(404);
        app.MapPost("/api/v1/label-group-memberships", CreateMembership).WithName("CreateGroupMembership").WithTags("Label groups")
            .WithSummary("Add one reviewed affiliation from either endpoint").Accepts<CreateGroupMembershipRequest>("application/json")
            .Produces<LabelGroupMembershipResponse>(201).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(415).ProducesProblem(428);
        app.MapPatch("/api/v1/label-group-memberships/{id}", UpdateMembership).WithName("UpdateGroupMembership").WithTags("Label groups")
            .WithSummary("Conditionally replace membership metadata, never endpoints").Accepts<UpdateGroupMembershipRequest>("application/json")
            .Produces<LabelGroupMembershipResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(415).ProducesProblem(428);
        app.MapDelete("/api/v1/label-group-memberships/{id}", RemoveMembership).WithName("RemoveGroupMembership").WithTags("Label groups")
            .WithSummary("Remove one assertion while retaining permanent history; departure should be marked former instead")
            .Produces(204).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        app.MapGet("/api/v1/labels/{id}/related-labels", Related).WithName("GetRelatedLabels").WithTags("Label groups")
            .WithSummary("Page distinct labels sharing recorded parents or current groups, retaining all reasons")
            .Produces<GroupPageResponse<RelatedLabelItemResponse>>().Produces(308).ProducesProblem(400).ProducesProblem(404);
    }
    private static GroupQuery QueryFields(HttpRequest request, bool groups = false, bool related = false)
    {
        var allowed = groups ? new[] { "page", "pageSize", "q", "kind", "sort", "order" }
            : related ? ["page", "pageSize", "kind"] : ["page", "pageSize", "kind", "status"];
        foreach (var field in request.Query)
            if (!allowed.Contains(field.Key) || field.Value.Count != 1) throw new GroupValidationException("Unknown or repeated query field.");
        string? Text(string name) => request.Query.TryGetValue(name, out var v) ? v[0] : null;
        int Number(string name, int fallback) => Text(name) is not { } v ? fallback
            : int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new GroupValidationException("Invalid pagination.");
        var order = Text("order");
        if (order is not (null or "asc" or "desc")) throw new GroupValidationException("Use asc or desc.");
        var query = new GroupQuery(Number("page", 1), Number("pageSize", 25), Text("q"), Text("kind"), Text("status"), Text("sort") ?? "name", order == "desc");
        query.Validate(); return query;
    }
    private static async Task<IResult> Query(HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        c.Response.Headers.CacheControl = "no-store";
        var page = await service.QueryAsync(QueryFields(c.Request, groups: true), ct);
        return TypedResults.Ok(new GroupPageResponse<LabelGroupResponse>(page.Items.Select(ToResponse).ToArray(), page.Total, page.Page, page.PageSize));
    }
    private static async Task<IResult> Get(string id, HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        var group = await service.GetAsync(id, ct);
        return group is null ? Missing(c) : GroupResult(c, group);
    }
    private static string? Optional(Dictionary<string, JsonElement> fields, string name, bool required = false)
    {
        if (fields.TryGetValue(name, out var v)) return LabelRequests.NullableString(v, name);
        if (required) throw new GroupValidationException($"Supply {name}, using null to clear it.");
        return null;
    }
    private static async Task<IResult> Create(HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        var f = await LabelRequests.FieldsAsync(c.Request, "application/json", ct, ["kind", "name", "sortName", "description", "website"]);
        var result = await service.CreateAsync(LabelRequests.RequiredString(f, "kind"), new(LabelRequests.RequiredString(f, "name"),
            Optional(f, "sortName"), Optional(f, "description"), Optional(f, "website")), ct);
        return GroupResult(c, result.Group ?? throw new InvalidOperationException("Group creation returned no group."), true);
    }
    private static async Task<IResult> Update(string id, HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(c.Request);
        var f = await LabelRequests.FieldsAsync(c.Request, "application/merge-patch+json", ct, ["name", "sortName", "description", "website"]);
        var fields = f.ToDictionary(p => p.Key, p => LabelRequests.NullableString(p.Value, p.Key));
        var result = await service.UpdateAsync(id, version, fields, ct);
        return result.Group is null ? Failure(c, result) : GroupResult(c, result.Group);
    }
    private static IResult GroupResult(HttpContext c, LabelGroup group, bool created = false)
    {
        c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.ETag = LabelRequests.ETag(group.Version);
        return created ? TypedResults.Created($"/api/v1/label-groups/{group.Id}", ToResponse(group)) : TypedResults.Ok(ToResponse(group));
    }
    private static async Task<IResult> GroupMembers(string id, HttpContext c, ILabelGroupService service, CancellationToken ct) =>
        MembersResult(c, await service.MembershipsAsync(null, id, QueryFields(c.Request), ct));
    private static async Task<IResult?> Redirect(string id, string child, ILabelMergeService merges, CancellationToken ct)
    {
        var identity = await merges.ResolveAsync(id, ct);
        return identity.IsRetired ? TypedResults.Redirect($"/api/v1/labels/{Uri.EscapeDataString(identity.CanonicalId!)}/{child}", permanent: true, preserveMethod: true) : null;
    }
    private static async Task<IResult> LabelMembers(string id, HttpContext c, ILabelGroupService service, ILabelMergeService merges, CancellationToken ct)
    {
        c.Response.Headers.CacheControl = "no-store";
        var redirect = await Redirect(id, "group-memberships", merges, ct);
        return redirect ?? MembersResult(c, await service.MembershipsAsync(id, null, QueryFields(c.Request), ct));
    }
    private static IResult MembersResult(HttpContext c, GroupPage<MembershipView>? page)
    {
        c.Response.Headers.CacheControl = "no-store";
        return page is null ? Missing(c) : TypedResults.Ok(new GroupPageResponse<LabelGroupMembershipResponse>(page.Items.Select(ToResponse).ToArray(), page.Total, page.Page, page.PageSize, page.Counts));
    }
    private static async Task<IResult> GetMembership(string id, HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        var item = await service.MembershipAsync(id, ct);
        return item is null ? Missing(c) : MembershipResult(c, item);
    }
    private static MembershipFields Fields(Dictionary<string, JsonElement> f) => new(LabelRequests.RequiredString(f, "status"), Optional(f, "since", true),
        Optional(f, "until", true), LabelRequests.RequiredString(f, "source"), Optional(f, "sourceUrl", true), Optional(f, "notes", true));
    private static bool Confirm(Dictionary<string, JsonElement> f) => !f.TryGetValue("confirmRejoin", out var value) ? false
        : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new GroupValidationException("confirmRejoin must be boolean.");
    private static string EndpointVersion(Dictionary<string, JsonElement> f, string name) =>
        !f.ContainsKey(name) ? throw new ApiRequestException(428, "precondition_required", $"Supply the last-read {name}.") : LabelRequests.RequiredString(f, name);
    private static async Task<IResult> CreateMembership(HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        var f = await LabelRequests.FieldsAsync(c.Request, "application/json", ct, [.. Metadata, "labelId", "groupId"]);
        var result = await service.SaveMembershipAsync(null, null, LabelRequests.RequiredString(f, "labelId"), LabelRequests.RequiredString(f, "groupId"),
            EndpointVersion(f, "labelVersion"), EndpointVersion(f, "groupVersion"), Fields(f), Confirm(f), ct);
        return result.Status == GroupWriteStatus.Updated ? MembershipResult(c, result.Membership!, true) : Failure(c, result);
    }
    private static async Task<IResult> UpdateMembership(string id, HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        var version = LabelRequests.ExpectedVersion(c.Request);
        var f = await LabelRequests.FieldsAsync(c.Request, "application/json", ct, Metadata);
        var item = await service.MembershipAsync(id, ct);
        if (item is null) return Missing(c);
        var result = await service.SaveMembershipAsync(id, version, item.Label.Id, item.Group.Id,
            EndpointVersion(f, "labelVersion"), EndpointVersion(f, "groupVersion"), Fields(f), Confirm(f), ct);
        return result.Status == GroupWriteStatus.Updated ? MembershipResult(c, result.Membership!) : Failure(c, result);
    }
    private static async Task<IResult> RemoveMembership(string id, HttpContext c, ILabelGroupService service, CancellationToken ct)
    {
        var result = await service.RemoveMembershipAsync(id, LabelRequests.ExpectedVersion(c.Request), ct);
        c.Response.Headers.CacheControl = "no-store";
        return result.Status == GroupWriteStatus.Updated ? TypedResults.NoContent() : Failure(c, result);
    }
    private static IResult MembershipResult(HttpContext c, MembershipView item, bool created = false)
    {
        c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.ETag = LabelRequests.ETag(item.Membership.Version);
        return created ? TypedResults.Created($"/api/v1/label-group-memberships/{item.Membership.Id}", ToResponse(item)) : TypedResults.Ok(ToResponse(item));
    }
    private static async Task<IResult> Related(string id, HttpContext c, ILabelGroupService service, ILabelMergeService merges, CancellationToken ct)
    {
        c.Response.Headers.CacheControl = "no-store";
        var redirect = await Redirect(id, "related-labels", merges, ct);
        if (redirect is not null) return redirect;
        var page = await service.RelatedAsync(id, QueryFields(c.Request, related: true), ct);
        return page is null ? Missing(c) : TypedResults.Ok(new GroupPageResponse<RelatedLabelItemResponse>(
            page.Items.Select(i => new RelatedLabelItemResponse(new(i.Label.Id, i.Label.Name, i.Label.Version),
                i.Reasons.Select(r => new RelatedLabelReasonResponse(r.Kind, r.Id, r.Name)).ToArray())).ToArray(), page.Total, page.Page, page.PageSize));
    }
    private static IResult Missing(HttpContext c) => TypedResults.Problem(ApiErrors.Create(c, 404, "group_resource_not_found", "Group, label or membership not found"));
    private static IResult Failure(HttpContext c, GroupWriteResult result)
    {
        c.Response.Headers.CacheControl = "no-store";
        if (result.Status == GroupWriteStatus.NotFound) return Missing(c);
        if (result.Status == GroupWriteStatus.Conflict)
        {
            c.Response.Headers.Location = $"/api/v1/label-group-memberships/{result.Membership!.Membership.Id}";
            return TypedResults.Problem(ApiErrors.Create(c, 409, "membership_exists", "This affiliation already exists", "Review the existing membership instead of creating another."));
        }
        if (result.Status == GroupWriteStatus.Stale) return TypedResults.Problem(ApiErrors.Create(c, 412, "group_state_changed",
            "Group or membership state changed", "No changes were saved. Reload the group or membership and review the current endpoints again."));
        throw new InvalidOperationException("Unexpected group write status.");
    }
    internal static LabelGroupResponse ToResponse(LabelGroup g) => new(g.Id, g.Kind, g.Name, g.SortName, g.Description, g.Website, g.Version, g.CreatedUtc, g.UpdatedUtc, g.CurrentMembers);
    internal static LabelGroupMembershipResponse ToResponse(MembershipView v) => new(v.Membership.Id, new(v.Label.Id, v.Label.Name, v.Label.Version), ToResponse(v.Group),
        v.Membership.Fields.Status, v.Membership.Fields.Since, v.Membership.Fields.Until, v.Membership.Fields.Source, v.Membership.Fields.SourceUrl, v.Membership.Fields.Notes,
        v.Membership.Version, v.Membership.CreatedUtc, v.Membership.UpdatedUtc);
}
