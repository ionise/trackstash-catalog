using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private const string SonoImportSource = """
        {"id":800,"title":"New Sono album","artist":{"id":701,"name":"New Sono artist"},
         "label":{"id":1,"name":"New Sono label"},"edition":12,
         "editions":[{"id":12,"source":"discogs","external_id":"99","title":"New Sono edition",
           "label":{"id":1,"name":"New Sono label"},"catalog_no":"CAT1","track_count":1}],
         "tracks":[{"id":707,"title":"New Sono track","isrc":"GBTRK0000099","bpm":124,"key_alphanumeric":"11A",
           "genre":["House"],"disc_number":1,"track_number":1,
           "artists":[{"id":701,"name":"New Sono artist","is_primary":true}]}]}
        """;

    [Fact]
    public async Task SonoImport_AtomicCreationAndReplay()
    {
        var factory = new EnrichmentHttpFactory { ResponseJson = SonoImportSource };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var lookup = await client.GetFromJsonAsync<JsonElement>("/api/v1/media/provider-release/sonovault/800?editionId=12");
        var response = await client.PostAsJsonAsync("/api/v1/sonovault-imports", new {
            releaseId = "800", editionId = "12", sourceVersion = lookup.GetProperty("sourceVersion").GetString() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(review.GetProperty("blockers").EnumerateArray());
        var tag = response.Headers.ETag!.ToString();
        var id = review.GetProperty("id").GetString();
        var intent = new { labelId = (string?)null, artists = new[] { new { providerId = "701", canonicalId = (string?)null } },
            recordings = new[] { new { providerId = "707", canonicalId = (string?)null } }, allowIncomplete = false, confirmDistinctRelease = false };
        async Task<HttpResponseMessage> Apply()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sonovault-imports/{id}/apply") { Content = JsonContent.Create(intent) };
            request.Headers.TryAddWithoutValidation("If-Match", tag);
            return await client.SendAsync(request);
        }
        var applied = await Apply();
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("placements").GetInt32());
        Assert.Equal(1, await ImportCount("release"));
        Assert.Equal(1, await ImportCount("recording"));
        Assert.Equal(1, await ImportCount("recording_musical_metadata WHERE bpm=124 AND key_value='11A'"));
        var replay = await Apply();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(result.GetProperty("releaseId").GetString(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseId").GetString());
        Assert.Equal(1, await ImportCount("release"));
        Assert.Equal(1, await ImportCount("enrichment_apply_revision"));
    }

    [Fact]
    public async Task SonoImport_RejectsCatalogChangesWithoutPartialWrites()
    {
        var factory = new EnrichmentHttpFactory { ResponseJson = SonoImportSource };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var lookup = await client.GetFromJsonAsync<JsonElement>("/api/v1/media/provider-release/sonovault/800?editionId=12");
        var response = await client.PostAsJsonAsync("/api/v1/sonovault-imports", new {
            releaseId = "800", editionId = "12", sourceVersion = lookup.GetProperty("sourceVersion").GetString() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        await ImportSql("INSERT INTO label(label_id,name) VALUES('new-label','Changed')");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sonovault-imports/{review.GetProperty("id").GetString()}/apply")
        {
            Content = JsonContent.Create(new { labelId = (string?)null,
                artists = new[] { new { providerId = "701", canonicalId = (string?)null } },
                recordings = new[] { new { providerId = "707", canonicalId = (string?)null } }, allowIncomplete = false, confirmDistinctRelease = false })
        };
        request.Headers.TryAddWithoutValidation("If-Match", response.Headers.ETag!.ToString());
        await ProblemAsync(await client.SendAsync(request), 412, "enrichment_state_changed");
        Assert.Equal(0, await ImportCount("release"));
        Assert.Equal(0, await ImportCount("recording"));
    }

    [Fact]
    public async Task SonoImport_RejectsChangedSourceAndRequiresExplicitDistinctRelease()
    {
        await ImportSql("INSERT INTO release(release_id,name,title) VALUES('existing','New Sono edition','New Sono edition')");
        var factory = new EnrichmentHttpFactory { ResponseJson = SonoImportSource };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var lookup = await client.GetFromJsonAsync<JsonElement>("/api/v1/media/provider-release/sonovault/800?editionId=12");
        var response = await client.PostAsJsonAsync("/api/v1/sonovault-imports", new {
            releaseId = "800", editionId = "12", sourceVersion = lookup.GetProperty("sourceVersion").GetString() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(review.GetProperty("releaseCandidates").EnumerateArray());
        var intent = new { labelId = (string?)null, artists = new[] { new { providerId = "701", canonicalId = (string?)null } },
            recordings = new[] { new { providerId = "707", canonicalId = (string?)null } }, allowIncomplete = false,
            confirmDistinctRelease = false };
        async Task<HttpResponseMessage> Apply(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sonovault-imports/{review.GetProperty("id").GetString()}/apply")
                { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", response.Headers.ETag!.ToString());
            return await client.SendAsync(request);
        }
        await ProblemAsync(await Apply(intent), 409, "sonovault_release_ambiguous");
        factory.ResponseJson = SonoImportSource.Replace("\"bpm\":124", "\"bpm\":125");
        await ProblemAsync(await Apply(new { intent.labelId, intent.artists, intent.recordings, intent.allowIncomplete,
            confirmDistinctRelease = true }), 412, "enrichment_state_changed");
        Assert.Equal(1, await ImportCount("release"));
        Assert.Equal(0, await ImportCount("recording"));
    }
}
