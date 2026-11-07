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
        public Uri? LastUri { get; set; }
        public int Count { get; set; }
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(EnrichmentHttpFactory factory) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                factory.LastUri = request.RequestUri;
                factory.Count++;
                return Task.FromResult(new HttpResponseMessage(factory.Status)
                {
                    Content = new StringContent(factory.Status == HttpStatusCode.OK ? """
                        {"id":7,"title":"Shared tune","isrc":"GBTRK0000001","genre":["House"],"bpm":124,"artists":[]}
                        """ : """{"detail":"private upstream"}""", Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
