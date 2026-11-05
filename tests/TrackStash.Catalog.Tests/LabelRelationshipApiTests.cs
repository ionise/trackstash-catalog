using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task<(LabelRelationshipsResponse Data, string ETag)> RelationshipsAsync(string id)
    {
        var response = await _client.GetAsync($"/api/v1/labels/{id}/relationships");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        return ((await response.Content.ReadFromJsonAsync<LabelRelationshipsResponse>())!, response.Headers.ETag!.ToString());
    }
    private async Task<HttpResponseMessage> SaveRelationshipsAsync(string id, string etag, params LabelRelationshipRequest[] items)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/labels/{id}/relationships")
        {
            Content = JsonContent.Create(new ReplaceLabelRelationshipsRequest(items)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }
    [Fact]
    public async Task RelationshipsApi_PersistsDirectedEdgesEvidenceInverseViewsAndConditionalEdits()
    {
        var (child, _) = await CreateAsync("Child imprint");
        var (parent, _) = await CreateAsync("Parent label");
        var current = await RelationshipsAsync(child.Id);
        Assert.Empty(current.Data.Items);
        var request = new LabelRelationshipRequest(null, parent.Id, "imprint_of", "Reviewed official site",
            "https://example.test/labels", "Distinct identities, not duplicates.", parent.Version);
        var response = await SaveRelationshipsAsync(child.Id, current.ETag, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<LabelRelationshipsResponse>())!;
        var edge = Assert.Single(saved.Items);
        Assert.Equal(parent.Name, edge.To.Name);
        Assert.Equal(child.Id, edge.From.Id);
        Assert.Equal(request.SourceUrl, edge.SourceUrl);
        var inverse = await RelationshipsAsync(parent.Id);
        Assert.Equal(edge.Id, Assert.Single(inverse.Data.Items).Id);
        await ProblemAsync(await SaveRelationshipsAsync(child.Id, current.ETag), 412, "relationship_state_changed");
        var fresh = await RelationshipsAsync(child.Id);
        var changed = await SaveRelationshipsAsync(child.Id, fresh.ETag, request with { Id = edge.Id, TargetVersion = edge.To.Version, Notes = "Updated evidence" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var updated = Assert.Single((await changed.Content.ReadFromJsonAsync<LabelRelationshipsResponse>())!.Items);
        Assert.Equal(edge.Id, updated.Id);
        Assert.Equal(edge.CreatedUtc, updated.CreatedUtc);
        await ProblemAsync(await SaveRelationshipsAsync(parent.Id, inverse.ETag), 412, "relationship_state_changed");
        fresh = await RelationshipsAsync(child.Id);
        Assert.Equal(HttpStatusCode.OK, (await SaveRelationshipsAsync(child.Id, fresh.ETag)).StatusCode);
        Assert.Empty((await RelationshipsAsync(parent.Id)).Data.Items);
    }
    [Fact]
    public async Task RelationshipsApi_RejectsUnsafeStaleUnknownAndUnauthenticatedWrites()
    {
        var (a, _) = await CreateAsync("A label");
        var (b, _) = await CreateAsync("B label");
        var current = await RelationshipsAsync(a.Id);
        var edge = new LabelRelationshipRequest(null, b.Id, "imprint_of", "manual", null, null, b.Version);
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync($"/api/v1/labels/{a.Id}/relationships"), 401, "unauthorized");
        await ProblemAsync(await _client.PatchAsJsonAsync($"/api/v1/labels/{a.Id}/relationships", new { items = Array.Empty<object>() }), 428, "precondition_required");
        await ProblemAsync(await SaveRelationshipsAsync(a.Id, current.ETag, edge with { ToLabelId = a.Id }), 400, "validation_failed");
        await ProblemAsync(await SaveRelationshipsAsync(a.Id, current.ETag, edge with { SourceUrl = "javascript:alert(1)" }), 400, "validation_failed");
        await ProblemAsync(await SaveRelationshipsAsync(a.Id, current.ETag, edge, edge), 400, "validation_failed");
        await ProblemAsync(await SaveRelationshipsAsync(a.Id, current.ETag, edge with { TargetVersion = "outdated" }), 412, "relationship_state_changed");
        using var malformed = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/labels/{a.Id}/relationships")
        {
            Content = new StringContent("""{"items":[{"toLabelId":"b","toLabelId":"c"}]}""", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.TryAddWithoutValidation("If-Match", current.ETag);
        await ProblemAsync(await _client.SendAsync(malformed), 400, "validation_failed");
        Assert.Equal(HttpStatusCode.OK, (await SaveRelationshipsAsync(a.Id, current.ETag, edge)).StatusCode);
        var target = await RelationshipsAsync(b.Id);
        var from = Assert.Single(target.Data.Items).From;
        await ProblemAsync(await SaveRelationshipsAsync(b.Id, target.ETag,
            new LabelRelationshipRequest(null, a.Id, "imprint_of", "manual", null, null, from.Version)), 400, "validation_failed");
    }
    [Fact]
    public async Task RelationshipsApi_EnforcesOriginMediaBodyLimitsAndDescribesPreconditionsInOpenApi()
    {
        var (label, _) = await CreateAsync();
        var current = await RelationshipsAsync(label.Id);
        foreach (var (body, media, status, code) in new[]
        {
            ("{}", "text/plain", 415, "unsupported_media_type"),
            ("{", "application/json", 400, "invalid_json"),
            (JsonSerializer.Serialize(new { items = Array.Empty<object>(), oversized = new string('x', 17000) }), "application/json", 413, "body_too_large"),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/labels/{label.Id}/relationships") { Content = new StringContent(body, Encoding.UTF8, media) };
            request.Headers.TryAddWithoutValidation("If-Match", current.ETag);
            await ProblemAsync(await _client.SendAsync(request), status, code);
        }
        using var rejectedOrigin = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/labels/{label.Id}/relationships");
        rejectedOrigin.Headers.TryAddWithoutValidation("Origin", "https://example.test");
        await ProblemAsync(await _client.SendAsync(rejectedOrigin), 403, "origin_not_allowed");
        Assert.Empty((await RelationshipsAsync(label.Id)).Data.Items);
        var document = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var path = document.GetProperty("paths").GetProperty("/api/v1/labels/{id}/relationships");
        var patch = path.GetProperty("patch");
        Assert.Contains(patch.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "If-Match");
        Assert.True(patch.GetProperty("responses").TryGetProperty("412", out _));
        Assert.True(path.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.True(patch.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.True(document.GetProperty("components").GetProperty("schemas").GetProperty("LabelRelationshipRequest").GetProperty("properties").TryGetProperty("targetVersion", out _));
    }
    [Fact]
    public async Task RelationshipsApi_MergePreviewShowsTransferredEdgesAndRetiredReadsRedirect()
    {
        var (survivor, _) = await CreateAsync("Survivor");
        var (retired, _) = await CreateAsync("Duplicate");
        var (parent, _) = await CreateAsync("Parent");
        var initial = await RelationshipsAsync(retired.Id);
        await SaveRelationshipsAsync(retired.Id, initial.ETag, new LabelRelationshipRequest(null, parent.Id, "imprint_of", "manual", null, null, parent.Version));
        var previewResponse = await _client.PostAsJsonAsync($"/api/v1/labels/{survivor.Id}/merge-previews", new { retiredLabelId = retired.Id });
        var preview = (await previewResponse.Content.ReadFromJsonAsync<LabelMergePreviewResponse>())!;
        Assert.Empty(preview.Blockers);
        Assert.Equal(survivor.Id, Assert.Single(preview.Relationships).FromLabelId);
        using var apply = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/label-merge-previews/{preview.OperationId}/apply")
        {
            Content = JsonContent.Create(new { reason = "Confirmed duplicate" }),
        };
        apply.Headers.TryAddWithoutValidation("If-Match", previewResponse.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(apply)).StatusCode);
        Assert.Equal(parent.Id, Assert.Single((await RelationshipsAsync(survivor.Id)).Data.Items).To.Id);
        var redirect = await _client.GetAsync($"/api/v1/labels/{retired.Id}/relationships");
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        Assert.Equal($"/api/v1/labels/{survivor.Id}/relationships", redirect.Headers.Location!.OriginalString);
        await ProblemAsync(await SaveRelationshipsAsync(retired.Id, initial.ETag), 409, "label_retired");
    }
}
