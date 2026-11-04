using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static LabelProfileResponse ExampleProfile => new(
        "Independent electronic label.", "ES", "Seville", 2007, "active",
        "Personal note", true, "reviewed",
        [new("discogs", "https://www.discogs.com/label/591075", "Discogs", "https://ra.co/labels/9782")]);

    [Fact]
    public async Task ProfileApi_RoundtripsAllFieldsAndClearsWithoutOverwritingOtherData()
    {
        var (label, etag) = await CreateAsync();
        Assert.Equal("unknown", label.Profile.LifecycleStatus);
        Assert.False(label.Profile.Favourite);
        var response = await PatchAsync(label.Id, JsonSerializer.Serialize(new { profile = ExampleProfile }, WebJson), etag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<LabelResponse>())!;
        Assert.Equal(JsonSerializer.Serialize(ExampleProfile), JsonSerializer.Serialize(saved.Profile));
        Assert.Equal(label.Id, saved.Id);
        Assert.Equal(label.Name, saved.Name);
        Assert.NotEqual(etag, response.Headers.ETag!.ToString());
        var read = (await _client.GetFromJsonAsync<LabelResponse>($"/api/v1/labels/{label.Id}"))!;
        Assert.Equal(JsonSerializer.Serialize(ExampleProfile), JsonSerializer.Serialize(read.Profile));
        var rename = await PatchAsync(label.Id, """{"sortName":null}""", response.Headers.ETag.ToString());
        var renamed = (await rename.Content.ReadFromJsonAsync<LabelResponse>())!;
        Assert.Equal(JsonSerializer.Serialize(ExampleProfile), JsonSerializer.Serialize(renamed.Profile));
        await ProblemAsync(await PatchAsync(label.Id, JsonSerializer.Serialize(new { profile = ExampleProfile }, WebJson), etag), 412, "stale_version");
        var empty = new LabelProfileResponse(null, null, null, null, "unknown", null, false, "unreviewed", []);
        var cleared = await PatchAsync(label.Id, JsonSerializer.Serialize(new { profile = empty }, WebJson), rename.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(empty), JsonSerializer.Serialize((await cleared.Content.ReadFromJsonAsync<LabelResponse>())!.Profile));
    }

    [Theory]
    [InlineData("""{"profile":null}""")]
    [InlineData("""{"profile":{}}""")]
    [InlineData("""{"profile":{"description":"Partial replacement is not allowed"}}""")]
    [InlineData("""{"profile":{"description":null,"countryCode":null,"city":null,"foundedYear":null,"lifecycleStatus":"unknown","notes":null,"favourite":false,"reviewStatus":"unreviewed","links":[],"unexpected":true}}""")]
    [InlineData("""{"profile":{"description":null,"description":"duplicate","countryCode":null,"city":null,"foundedYear":null,"lifecycleStatus":"unknown","notes":null,"favourite":false,"reviewStatus":"unreviewed","links":[]}}""")]
    public async Task ProfileApi_RejectsIncompleteUnknownAndDuplicateFields(string body)
    {
        var (label, etag) = await CreateAsync();
        await ProblemAsync(await PatchAsync(label.Id, body, etag), 400, "validation_failed");
        Assert.Equal(etag, (await _client.GetAsync($"/api/v1/labels/{label.Id}")).Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData("countryCode", "\"Spain\"")]
    [InlineData("foundedYear", "\"2007\"")]
    [InlineData("foundedYear", "2007.5")]
    [InlineData("foundedYear", "0")]
    [InlineData("favourite", "\"true\"")]
    [InlineData("lifecycleStatus", "\"invalid\"")]
    [InlineData("reviewStatus", "\"invalid\"")]
    [InlineData("links", """[{"kind":"official","url":"javascript:alert(1)","title":null,"sourceUrl":null}]""")]
    [InlineData("links", """[{"kind":"official","url":"https://user:pass@example.test","title":null,"sourceUrl":null}]""")]
    [InlineData("links", """[{"kind":"official","url":"https://example.test","title":null,"sourceUrl":null,"unexpected":true}]""")]
    [InlineData("links", """[{"kind":"official","url":"https://example.test","title":null,"sourceUrl":null},{"kind":"other","url":"https://example.test","title":null,"sourceUrl":null}]""")]
    public async Task ProfileApi_RejectsInvalidTypesValuesAndUnsafeLinks(string field, string jsonValue)
    {
        var (label, etag) = await CreateAsync();
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(ExampleProfile, WebJson))!;
        fields[field] = JsonSerializer.Deserialize<JsonElement>(jsonValue);
        var body = JsonSerializer.Serialize(new { name = "Must not change", profile = fields });
        await ProblemAsync(await PatchAsync(label.Id, body, etag), 400, "validation_failed");
        var unchanged = await _client.GetFromJsonAsync<LabelResponse>($"/api/v1/labels/{label.Id}");
        Assert.Equal(label.Name, unchanged!.Name);
        Assert.Null(unchanged.Profile.Description);
    }

    [Fact]
    public async Task ProfileApi_UsesExistingAuthenticationPreconditionAndRetirementPolicy()
    {
        var (label, etag) = await CreateAsync();
        var body = JsonSerializer.Serialize(new { profile = ExampleProfile }, WebJson);
        await ProblemAsync(await PatchAsync(label.Id, body, null), 428, "precondition_required");
        await ProblemAsync(await PatchAsync("missing", body, etag), 404, "label_not_found");
        var other = await CreateAsync();
        var preview = await MergePreviewAsync(other.Label.Id, label.Id);
        Assert.Equal(HttpStatusCode.OK, (await MergeApplyAsync(preview.Preview.OperationId, preview.ETag)).StatusCode);
        await ProblemAsync(await PatchAsync(label.Id, body, etag), 409, "label_retired");
    }

    [Fact]
    public async Task ProfileApi_DocumentsCompleteReplacementAndNullableFields()
    {
        var schema = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var schemas = schema.GetProperty("components").GetProperty("schemas");
        var profile = schemas.GetProperty("LabelProfileResponse");
        Assert.Equal(9, profile.GetProperty("required").GetArrayLength());
        Assert.False(profile.GetProperty("additionalProperties").GetBoolean());
        Assert.True(profile.GetProperty("properties").GetProperty("description").GetProperty("nullable").GetBoolean());
        Assert.True(profile.GetProperty("properties").GetProperty("foundedYear").GetProperty("nullable").GetBoolean());
        Assert.Equal(100, profile.GetProperty("properties").GetProperty("links").GetProperty("maxItems").GetInt32());
        Assert.Equal(4, schemas.GetProperty("LabelWebLinkResponse").GetProperty("required").GetArrayLength());
        var patch = schema.GetProperty("paths").GetProperty("/api/v1/labels/{id}").GetProperty("patch");
        Assert.True(patch.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/merge-patch+json", out _));
    }
}
