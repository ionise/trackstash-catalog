using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    [Fact]
    public async Task RecordingEnrichment_MissingConfigurationIsExplicitAndNoMatchIsSuccessful()
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory { Status = HttpStatusCode.NotFound };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var failed = await client.PostAsync("/api/v1/recordings/t-shared/enrichment/sonovault", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("sonovault_not_configured", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, factory.Count);
        await using var configured = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await configured.StartAsync();
        using var configuredClient = configured.GetTestClient();
        configuredClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        var response = await configuredClient.PostAsync("/api/v1/recordings/t-shared/enrichment/sonovault", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidates").GetArrayLength());
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public async Task RecordingEnrichment_UsesCatalogSnapshotAndDoesNotWrite()
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory();
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-provider-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var before = await ImportCount("catalog_foundation_revision");
        var response = await client.PostAsync("/api/v1/recordings/t-shared/enrichment/sonovault", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var lookup = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("t-shared", lookup.GetProperty("query").GetProperty("catalogId").GetString());
        Assert.Equal("Original Mix", lookup.GetProperty("query").GetProperty("mixName").GetString());
        Assert.Equal("Zed Primary", lookup.GetProperty("query").GetProperty("artistNames")[0].GetString());
        Assert.Equal("Isrc", lookup.GetProperty("candidates")[0].GetProperty("match").GetProperty("basis").GetString());
        Assert.Equal("/v1/tracks/isrc/GBTRK0000001", factory.LastUri!.AbsolutePath);
        Assert.Equal(before, await ImportCount("catalog_foundation_revision"));
        Assert.Equal(1, factory.Count);

        var missing = await client.PostAsync("/api/v1/recordings/missing/enrichment/sonovault", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/recordings/t-shared/enrichment/sonovault", null)).StatusCode);
        Assert.Equal(1, factory.Count);
    }

    [Theory]
    [InlineData(401, 503, "sonovault_authentication_failed")]
    [InlineData(429, 429, "sonovault_rate_limited")]
    [InlineData(500, 502, "sonovault_upstream_failed")]
    public async Task RecordingEnrichment_ProviderFailureIsSafeAndDoesNotLogOutCatalog(int upstream, int expected, string code)
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory { Status = (HttpStatusCode)upstream };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-provider-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var response = await client.PostAsync("/api/v1/recordings/t-shared/enrichment/sonovault", null);
        Assert.Equal(expected, (int)response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.DoesNotContain("test-provider-key", problem.GetRawText());
        Assert.DoesNotContain("private upstream", problem.GetRawText());
        Assert.Equal(1, factory.Count);
    }

    private sealed class EnrichmentHttpFactory : IHttpClientFactory
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? ResponseJson { get; init; }
        public Func<Task>? BeforeResponseAsync { get; init; }
        public Uri? LastUri { get; set; }
        public int Count { get; set; }
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(EnrichmentHttpFactory factory) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                factory.LastUri = request.RequestUri;
                factory.Count++;
                if (factory.BeforeResponseAsync is { } before) await before();
                return new HttpResponseMessage(factory.Status)
                {
                    Content = new StringContent(factory.Status == HttpStatusCode.OK
                        ? factory.ResponseJson ?? (request.RequestUri!.AbsolutePath.Contains("/tracks") ? """
                        {"id":7,"title":"Shared tune","isrc":"GBTRK0000001","genre":["House"],"bpm":124,"artists":[]}
                        """ : """{"results":[{"id":8,"name":"Zed Primary","country":"United Kingdom","formation_year":1980}],"next_cursor":null}""")
                        : """{"detail":"private upstream"}""", Encoding.UTF8, "application/json")
                };
            }

        }
    }

    [Theory]
    [InlineData("artists", "t-primary", "Zed Primary")]
    [InlineData("labels", "t-label", "Track label")]
    public async Task NamedEntityEnrichment_UsesPublicSnapshotAuthenticatesAndDoesNotWrite(string resource, string id, string name)
    {
        await ImportSql(TrackFixture);
        var factory = new EnrichmentHttpFactory();
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var before = await ImportCount("catalog_foundation_revision");
        var entityBefore = await client.GetStringAsync($"/api/v1/{resource}/{id}");
        var response = await client.PostAsync($"/api/v1/{resource}/{id}/enrichment/sonovault", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var query = result.GetProperty("query");
        Assert.Equal(id, query.GetProperty("catalogId").GetString());
        Assert.Equal(name, query.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(query.GetProperty("catalogVersion").GetString()));
        Assert.False(query.TryGetProperty("notes", out _));
        Assert.False(query.TryGetProperty("profile", out _));
        Assert.Equal($"/v1/{resource}/search", factory.LastUri!.AbsolutePath);
        Assert.Contains("limit=10", factory.LastUri.Query);
        Assert.Equal(entityBefore, await client.GetStringAsync($"/api/v1/{resource}/{id}"));
        Assert.Equal(before, await ImportCount("catalog_foundation_revision"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/{resource}/missing/enrichment/sonovault", null)).StatusCode);
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/{resource}/{id}/enrichment/sonovault", null)).StatusCode);
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public async Task ReleaseEnrichment_UsesReleasePrimaryCreditsAndLabelsWithoutWrites()
    {
        await ImportSql(TrackFixture);
        await ImportSql("""
            INSERT INTO release_artist_credit(credit_id,release_id,artist_id,role,position,credit_name)
            VALUES('enrich-primary','t-new','t-primary','primary',1,'Zed Primary'),
                  ('enrich-remixer','t-new','t-remix','remixer',2,'Remixer Person');
            """);
        var factory = new EnrichmentHttpFactory
        { ResponseJson = """{"results":[{"id":8,"title":"New release","artist":{"id":42,"name":"Zed Primary"},"label":{"id":1,"name":"Track label"}}],"next_cursor":null}""" };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var before = await client.GetStringAsync("/api/v1/releases/t-new");
        var revisions = await ImportCount("catalog_foundation_revision");
        var response = await client.PostAsync("/api/v1/releases/t-new/enrichment/sonovault", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var query = result.GetProperty("query");
        Assert.Equal("t-new", query.GetProperty("catalogId").GetString());
        Assert.Equal("Zed Primary", Assert.Single(query.GetProperty("artistNames").EnumerateArray()).GetString());
        Assert.Equal("Track label", query.GetProperty("labelNames")[0].GetString());
        Assert.Equal("2024-06-01", query.GetProperty("catalogPublishDate").GetString());
        Assert.Equal("/v1/releases/search", factory.LastUri!.AbsolutePath);
        Assert.Contains("artist=Zed%20Primary", factory.LastUri.Query);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(before, await client.GetStringAsync("/api/v1/releases/t-new"));
        Assert.Equal(revisions, await ImportCount("catalog_foundation_revision"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/releases/missing/enrichment/sonovault", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/releases/t-new/enrichment/sonovault/candidates/bad?editionId=12", null)).StatusCode);
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/releases/t-new/enrichment/sonovault", null)).StatusCode);
        Assert.Equal(1, factory.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    public async Task ReleaseEnrichment_ExplicitCandidateDetailsAndEditionAreReadOnly(string? editionId)
    {
        await ImportSql(TrackFixture);
        var json = """
            {"id":8,"title":"New release","edition":null,"editions":[{"id":12,"source":"discogs","external_id":"99","track_count":0}],"tracks":[]}
            """;
        var factory = new EnrichmentHttpFactory { ResponseJson = editionId is null ? json : json.Replace("\"edition\":null", "\"edition\":12") };
        await using var app = Build(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:SonoVaultApiKey"] = "test-key" });
            builder.Services.AddSingleton<IHttpClientFactory>(factory);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var revisions = await ImportCount("catalog_foundation_revision");
        var response = await client.PostAsync("/api/v1/releases/t-new/enrichment/sonovault/candidates/8" +
            (editionId is null ? "" : "?editionId=12"), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/v1/releases/8" + (editionId is null ? "" : "?edition=12"), factory.LastUri!.PathAndQuery);
        Assert.Equal(revisions, await ImportCount("catalog_foundation_revision"));
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/releases/t-new/enrichment/sonovault/candidates/8", null)).StatusCode);
        Assert.Equal(1, factory.Count);
    }
}
