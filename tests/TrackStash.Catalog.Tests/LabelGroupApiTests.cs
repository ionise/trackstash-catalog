using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TrackStash.Catalog.Api;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task<(LabelGroupResponse Group, string ETag)> CreateGroup(string kind = "family", string name = "Anjuna")
    {
        var response = await _client.PostAsJsonAsync("/api/v1/label-groups", new { kind, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var group = (await response.Content.ReadFromJsonAsync<LabelGroupResponse>())!;
        Assert.Equal($"/api/v1/label-groups/{group.Id}", response.Headers.Location!.OriginalString);
        return (group, response.Headers.ETag!.ToString());
    }
    private async Task<HttpResponseMessage> GroupPatch(string path, object fields, string? etag, string media = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = new StringContent(JsonSerializer.Serialize(fields, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, media) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }
    private static CreateGroupMembershipRequest NewMembership(LabelResponse label, LabelGroupResponse group, string status = "current") =>
        new(label.Id, group.Id, label.Version, group.Version, status, "2020", null, "Official site", "https://example.test", "Reviewed evidence");
    [Fact]
    public async Task GroupsApi_CreatesDistinctKindsAndConditionallyEditsScalarsWithoutChangingKind()
    {
        var (family, tag) = await CreateGroup(); var (org, _) = await CreateGroup("organisation");
        Assert.NotEqual(family.Id, org.Id);
        var list = (await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupResponse>>("/api/v1/label-groups?q=anjuna&pageSize=1"))!;
        Assert.Equal(2, list.Total); Assert.Single(list.Items);
        Assert.Single((await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupResponse>>("/api/v1/label-groups?kind=organisation"))!.Items);
        var changed = await GroupPatch($"/api/v1/label-groups/{family.Id}", new { description = "Umbrella brand", website = "https://example.test" }, tag, "application/merge-patch+json");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var result = (await changed.Content.ReadFromJsonAsync<LabelGroupResponse>())!;
        Assert.Equal(family.Name, result.Name); Assert.Equal("Umbrella brand", result.Description);
        await ProblemAsync(await GroupPatch($"/api/v1/label-groups/{family.Id}", new { name = "stale" }, tag, "application/merge-patch+json"), 412, "group_state_changed");
        await ProblemAsync(await GroupPatch($"/api/v1/label-groups/{family.Id}", new { kind = "organisation" }, changed.Headers.ETag!.ToString(), "application/merge-patch+json"), 400, "validation_failed");
        await ProblemAsync(await _client.GetAsync("/api/v1/label-groups?page=0"), 400, "validation_failed");
        await ProblemAsync(await _client.GetAsync("/api/v1/label-groups?kind=parent"), 400, "validation_failed");
        await ProblemAsync(await _client.GetAsync("/api/v1/label-groups?q=a&q=b"), 400, "validation_failed");
    }
    [Fact]
    public async Task GroupsApi_OneMembershipFromEitherEndpointSupportsEvidenceStatusCountsRejoinAndRemoval()
    {
        var (label, _) = await CreateAsync("Anjunadeep"); var (group, _) = await CreateGroup();
        var created = await _client.PostAsJsonAsync("/api/v1/label-group-memberships", NewMembership(label, group));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var member = (await created.Content.ReadFromJsonAsync<LabelGroupMembershipResponse>())!;
        var path = created.Headers.Location!.OriginalString;
        var tag = created.Headers.ETag!.ToString();
        Assert.NotEqual(label.Version, member.Label.Version); Assert.NotEqual(group.Version, member.Group.Version);
        var fromLabel = (await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/labels/{label.Id}/group-memberships"))!;
        var fromGroup = (await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/label-groups/{group.Id}/members"))!;
        Assert.Equal(Assert.Single(fromLabel.Items).Id, Assert.Single(fromGroup.Items).Id);
        Assert.Equal(1, fromGroup.Counts!["current"]);
        var duplicate = NewMembership(label, group) with { LabelVersion = member.Label.Version, GroupVersion = member.Group.Version };
        var duplicateResponse = await _client.PostAsJsonAsync("/api/v1/label-group-memberships", duplicate);
        await ProblemAsync(duplicateResponse, 409, "membership_exists");
        Assert.Equal(path, duplicateResponse.Headers.Location!.OriginalString);
        var former = new UpdateGroupMembershipRequest(member.Label.Version, member.Group.Version, "former", "2020", "2022-03", member.Source, member.SourceUrl, "Left family");
        await ProblemAsync(await GroupPatch(path, former, null), 428, "precondition_required");
        var edited = await GroupPatch(path, former, tag);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var updated = (await edited.Content.ReadFromJsonAsync<LabelGroupMembershipResponse>())!;
        Assert.Equal(member.Id, updated.Id); Assert.Equal(member.CreatedUtc, updated.CreatedUtc);
        Assert.Empty((await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/label-groups/{group.Id}/members"))!.Items);
        Assert.Single((await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/label-groups/{group.Id}/members?status=former"))!.Items);
        var rejoin = former with { LabelVersion = updated.Label.Version, GroupVersion = updated.Group.Version, Status = "current", Since = "2024", Until = null };
        await ProblemAsync(await GroupPatch(path, rejoin, edited.Headers.ETag!.ToString()), 400, "validation_failed");
        var rejoined = await GroupPatch(path, rejoin with { ConfirmRejoin = true }, edited.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, rejoined.StatusCode);
        await ProblemAsync(await GroupPatch(path, former, tag), 412, "group_state_changed");
        using var remove = new HttpRequestMessage(HttpMethod.Delete, path);
        remove.Headers.TryAddWithoutValidation("If-Match", rejoined.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(remove)).StatusCode);
        using var repeat = new HttpRequestMessage(HttpMethod.Delete, path);
        repeat.Headers.TryAddWithoutValidation("If-Match", rejoined.Headers.ETag!.ToString());
        await ProblemAsync(await _client.SendAsync(repeat), 404, "group_resource_not_found");
    }
    [Fact]
    public async Task GroupsApi_StaleEndpointEvidenceAndInvalidInputsNeverCreateMemberships()
    {
        var (label, labelTag) = await CreateAsync(); var (group, _) = await CreateGroup();
        var path = "/api/v1/label-group-memberships";
        await ProblemAsync(await _client.PostAsJsonAsync(path, new { labelId = label.Id, groupId = group.Id }), 428, "precondition_required");
        await ProblemAsync(await _client.PostAsJsonAsync(path, NewMembership(label, group) with { SourceUrl = "javascript:alert(1)" }), 400, "validation_failed");
        await ProblemAsync(await _client.PostAsJsonAsync(path, NewMembership(label, group) with { Since = "2020-02-31" }), 400, "validation_failed");
        await ProblemAsync(await _client.PostAsJsonAsync(path, NewMembership(label, group) with { Status = "former", Since = "2022", Until = "2021" }), 400, "validation_failed");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(label.Id, """{"name":"Renamed"}""", labelTag)).StatusCode);
        await ProblemAsync(await _client.PostAsJsonAsync(path, NewMembership(label, group)), 412, "group_state_changed");
        var members = (await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/label-groups/{group.Id}/members?status=all"))!;
        Assert.Empty(members.Items);
        await ProblemAsync(await _client.GetAsync("/api/v1/labels/missing/group-memberships"), 404, "group_resource_not_found");
        await ProblemAsync(await _client.GetAsync("/api/v1/labels/missing/related-labels"), 404, "group_resource_not_found");
    }
    [Fact]
    public async Task GroupsApi_RelatedLabelsRetainReasonsAndMergePreviewTransfersMembershipsWithRetiredRedirects()
    {
        var (a, _) = await CreateAsync("A"); var (b, _) = await CreateAsync("B"); var (group, _) = await CreateGroup();
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/label-group-memberships", NewMembership(a, group))).StatusCode);
        group = (await _client.GetFromJsonAsync<LabelGroupResponse>($"/api/v1/label-groups/{group.Id}"))!;
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/label-group-memberships", NewMembership(b, group))).StatusCode);
        var related = (await _client.GetFromJsonAsync<GroupPageResponse<RelatedLabelItemResponse>>($"/api/v1/labels/{a.Id}/related-labels?pageSize=1"))!;
        Assert.Equal(1, related.Total); Assert.Equal(b.Id, Assert.Single(related.Items).Label.Id);
        Assert.Equal("family", Assert.Single(related.Items[0].Reasons).Kind);
        var previewResponse = await _client.PostAsJsonAsync($"/api/v1/labels/{a.Id}/merge-previews", new { retiredLabelId = b.Id });
        var preview = (await previewResponse.Content.ReadFromJsonAsync<LabelMergePreviewResponse>())!;
        Assert.Empty(preview.Blockers); Assert.Equal(group.Id, Assert.Single(preview.Memberships).Group.Id);
        using var apply = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/label-merge-previews/{preview.OperationId}/apply") { Content = JsonContent.Create(new { reason = "Confirmed duplicate" }) };
        apply.Headers.TryAddWithoutValidation("If-Match", previewResponse.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(apply)).StatusCode);
        Assert.Single((await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/labels/{a.Id}/group-memberships"))!.Items);
        Assert.Equal(HttpStatusCode.PermanentRedirect, (await _client.GetAsync($"/api/v1/labels/{b.Id}/group-memberships")).StatusCode);
        Assert.Equal(HttpStatusCode.PermanentRedirect, (await _client.GetAsync($"/api/v1/labels/{b.Id}/related-labels")).StatusCode);
        await ProblemAsync(await _client.PostAsJsonAsync("/api/v1/label-group-memberships", NewMembership(b, group)), 409, "label_retired");
    }
    [Fact]
    public async Task GroupsApi_AuthenticationOriginBodyLimitsDeleteCorsAndOpenApiAreConsistent()
    {
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync("/api/v1/label-groups"), 401, "unauthorized");
        var (group, tag) = await CreateGroup();
        using var origin = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/label-group-memberships/missing");
        origin.Headers.TryAddWithoutValidation("Origin", "https://example.test"); origin.Headers.TryAddWithoutValidation("If-Match", tag);
        await ProblemAsync(await _client.SendAsync(origin), 403, "origin_not_allowed");
        await ProblemAsync(await _client.PostAsync("/api/v1/label-groups", new StringContent("{\"name\":\"a\",\"name\":\"b\"}", Encoding.UTF8, "application/json")), 400, "validation_failed");
        await ProblemAsync(await _client.PostAsync("/api/v1/label-groups", new StringContent("{}", Encoding.UTF8, "text/plain")), 415, "unsupported_media_type");
        await ProblemAsync(await _client.PostAsJsonAsync("/api/v1/label-groups", new { name = new string('x', 17000), kind = "family" }), 413, "body_too_large");
        var doc = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = doc.GetProperty("paths");
        foreach (var route in new[] { "/api/v1/label-groups", "/api/v1/label-groups/{id}", "/api/v1/label-groups/{id}/members",
            "/api/v1/label-group-memberships", "/api/v1/label-group-memberships/{id}", "/api/v1/labels/{id}/group-memberships",
            "/api/v1/labels/{id}/related-labels", "/api/v1/labels/{id}/relationship-edges" }) Assert.True(paths.TryGetProperty(route, out _));
        var delete = paths.GetProperty("/api/v1/label-group-memberships/{id}").GetProperty("delete");
        Assert.Contains(delete.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "If-Match");
        Assert.True(paths.GetProperty("/api/v1/label-groups/{id}").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        var query = paths.GetProperty("/api/v1/label-groups").GetProperty("get").GetProperty("parameters");
        Assert.Contains(query.EnumerateArray(), p => p.GetProperty("name").GetString() == "pageSize" && p.GetProperty("schema").GetProperty("maximum").GetInt32() == 100);
        var schemas = doc.GetProperty("components").GetProperty("schemas");
        Assert.False(schemas.GetProperty("UpdateLabelGroupRequest").TryGetProperty("required", out _));
        Assert.Contains(schemas.GetProperty("CreateGroupMembershipRequest").GetProperty("required").EnumerateArray(), p => p.GetString() == "groupVersion");
        await using var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:AllowedOrigins:0"] = "http://localhost:5173" }));
        await app.StartAsync(); using var cors = app.GetTestClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/label-group-memberships/missing");
        preflight.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "DELETE");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization,if-match");
        var response = await cors.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
    }
    [Fact]
    public async Task GroupsApi_IncomingImprintEditsRequireBothVersionsAndDoNotCreateReciprocalEdges()
    {
        var (child, _) = await CreateAsync("Child"); var (parent, _) = await CreateAsync("Parent");
        var graph = await RelationshipsAsync(parent.Id);
        var fields = new SaveLabelEdgeRequest(null, child.Id, "imprint_of", true, "manual", null, "Added from parent", child.Version, false);
        var path = $"/api/v1/labels/{parent.Id}/relationship-edges";
        var created = await GroupPatch(path, fields, graph.ETag);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var edge = Assert.Single((await created.Content.ReadFromJsonAsync<LabelRelationshipsResponse>())!.Items);
        Assert.Equal(child.Id, edge.From.Id); Assert.Equal(parent.Id, edge.To.Id);
        Assert.Equal(edge.Id, Assert.Single((await RelationshipsAsync(child.Id)).Data.Items).Id);
        await ProblemAsync(await GroupPatch(path, fields, graph.ETag), 412, "relationship_state_changed");
        var updated = await GroupPatch(path, fields with { Id = edge.Id, Source = "Reviewed", TargetVersion = edge.From.Version }, created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var latest = Assert.Single((await updated.Content.ReadFromJsonAsync<LabelRelationshipsResponse>())!.Items);
        var removed = await GroupPatch(path, fields with { Id = latest.Id, TargetVersion = latest.From.Version, Remove = true }, updated.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty((await RelationshipsAsync(child.Id)).Data.Items);
    }
    [Fact]
    public async Task GroupsApi_PagesMoreThanOneHundredMembersWithFullCountsAndCurrentGroupSummaries()
    {
        var (group, _) = await CreateGroup();
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            for (var i = 0; i < 101; i++)
            {
                var id = $"paged-member-{i:D3}";
                await unit.Labels.UpsertAsync(new Label { Id = id, Name = id });
                var currentGroup = (await unit.LabelGroups.GetAsync(group.Id))!;
                var label = (await unit.Labels.GetByIdAsync(id))!;
                await unit.LabelGroups.SaveMembershipAsync(null, null, id, group.Id, label.Version, currentGroup.Version,
                    new("current", null, null, "manual", null, null), false);
            }
            await unit.CommitAsync();
        }
        var first = (await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/label-groups/{group.Id}/members?page=1&pageSize=100"))!;
        var second = (await _client.GetFromJsonAsync<GroupPageResponse<LabelGroupMembershipResponse>>($"/api/v1/label-groups/{group.Id}/members?page=2&pageSize=100"))!;
        Assert.Equal(101, first.Total); Assert.Equal(100, first.Items.Count); Assert.Single(second.Items);
        Assert.Equal(101, first.Counts!["current"]);
        Assert.Equal(101, first.Items[0].Group.CurrentMembers);
        Assert.DoesNotContain(second.Items[0].Id, first.Items.Select(i => i.Id));
        await ProblemAsync(await _client.GetAsync($"/api/v1/label-groups/{group.Id}/members?pageSize=101"), 400, "validation_failed");
    }
}
