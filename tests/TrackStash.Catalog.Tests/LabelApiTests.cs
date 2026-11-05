using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrackStash.Catalog.Api;
using TrackStash.Catalog.Application;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests : IAsyncLifetime
{
    private const string Key = "test-only-api-key-not-a-real-secret-12345";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"catalog-api-{Guid.NewGuid():N}.db");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private SqliteStorageProvider Provider => new(_path);

    public async Task InitializeAsync()
    {
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        _app = Build();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
    }

    private WebApplication Build(Action<WebApplicationBuilder>? configure = null)
        => CatalogApiHost.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CatalogApi:ApiKey"] = Key, ["CatalogApi:DatabasePath"] = _path, ["CatalogApi:ArtworkRoot"] = _path + ".artwork",
                ["CatalogApi:BackupRoot"] = _path + ".backups",
                ["CatalogApi:ScannerDatabasePath"] = "",
            });
            builder.WebHost.UseTestServer();
            configure?.Invoke(builder);
        });

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        File.Delete(_path + ".scanner");
        if (Directory.Exists(_path + ".artwork")) Directory.Delete(_path + ".artwork", recursive: true);
        if (Directory.Exists(_path + ".backups")) Directory.Delete(_path + ".backups", recursive: true);
    }

    private async Task<(LabelResponse Label, string ETag)> CreateAsync(string name = "Example Records")
    {
        var response = await _client.PostAsJsonAsync("/api/v1/labels", new { name, sortName = "Original" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var label = (await response.Content.ReadFromJsonAsync<LabelResponse>())!;
        Assert.Equal($"/api/v1/labels/{label.Id}", response.Headers.Location!.OriginalString);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag.IsWeak);
        return (label, response.Headers.ETag.ToString());
    }

    private async Task<HttpResponseMessage> PatchAsync(string id, string body, string? etag,
        string mediaType = "application/merge-patch+json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/labels/{id}")
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        return problem;
    }

    [Theory]
    [InlineData("/api/v1/labels")]
    [InlineData("/openapi/v1.json")]
    public async Task AllRoutesRequireBearerKey(string path)
    {
        using var client = _app.GetTestClient();
        var response = await client.GetAsync(path);
        await ProblemAsync(response, 401, "unauthorized");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        await ProblemAsync(await client.GetAsync(path), 401, "unauthorized");
    }

    [Fact]
    public async Task WriteWithoutCredentialsCannotCreateALabel()
    {
        using var client = _app.GetTestClient();
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/labels", new { name = "Unauthorized" }), 401, "unauthorized");
        Assert.Equal(0, (await new LabelService(Provider).QueryAsync(new LabelQuery())).Total);
    }

    [Fact]
    public async Task BrowserOriginsAndRemoteConnectionsAreRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/labels");
        request.Headers.Add("Origin", "http://localhost:3000");
        await ProblemAsync(await _client.SendAsync(request), 403, "origin_not_allowed");
        var remote = await _app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Method = "GET";
            context.Request.Path = "/api/v1/labels";
            context.Request.Headers.Authorization = $"Bearer {Key}";
        });
        Assert.Equal(403, remote.Response.StatusCode);
    }

    [Fact]
    public async Task SameOriginBrowserCanEditButStillNeedsBearerKey()
    {
        using var missing = new HttpRequestMessage(HttpMethod.Get, "/api/v1/labels");
        missing.Headers.Add("Origin", "http://localhost");
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.SendAsync(missing), 401, "unauthorized");
        var (label, tag) = await CreateAsync();
        using var edit = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/labels/{label.Id}")
        {
            Content = new StringContent("""{"name":"Same-origin edit"}""", Encoding.UTF8, "application/merge-patch+json"),
        };
        edit.Headers.Add("Origin", "http://localhost");
        edit.Headers.Add("If-Match", tag);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(edit)).StatusCode);
    }

    [Fact]
    public async Task ExplicitLoopbackOriginAllowsPreflightAndExposesETagWithoutCredentials()
    {
        const string origin = "http://localhost:5173";
        await using var app = Build(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["CatalogApi:AllowedOrigins:0"] = origin }));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/labels");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "PATCH");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,if-match");
        var allowed = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(origin, allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("If-Match", allowed.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
        client.DefaultRequestHeaders.Add("Origin", origin);
        var unauthorized = await client.GetAsync("/api/v1/labels");
        await ProblemAsync(unauthorized, 401, "unauthorized");
        Assert.Equal(origin, unauthorized.Headers.GetValues("Access-Control-Allow-Origin").Single());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var response = await client.PostAsJsonAsync("/api/v1/labels", new { name = "Browser-created" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("ETag", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        Assert.Contains("Location", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("*")]
    [InlineData("http://localhost:5173/path")]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://user@localhost:5173")]
    public void InvalidBrowserOriginsFailStartup(string origin)
        => Assert.Throws<InvalidOperationException>(() => Build(builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:AllowedOrigins:0"] = origin })));

    [Fact]
    public async Task NonLoopbackHostIsRejectedEvenOnLoopbackConnection()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/labels");
        request.Headers.Host = "example.invalid";
        await ProblemAsync(await _client.SendAsync(request), 403, "host_not_allowed");
    }

    [Fact]
    public async Task LabelLifecycle_UsesStableIdentityETagsAndMergePatch()
    {
        var (label, etag) = await CreateAsync();
        Assert.Matches("^[0-9a-f]{32}$", label.Id);
        var get = await _client.GetAsync($"/api/v1/labels/{label.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(etag, get.Headers.ETag!.ToString());
        Assert.True(get.Headers.CacheControl!.NoStore);
        var patch = await PatchAsync(label.Id, """{"name":"Edited Records"}""", etag);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var updated = (await patch.Content.ReadFromJsonAsync<LabelResponse>())!;
        Assert.Equal(label.Id, updated.Id);
        Assert.Equal("Edited Records", updated.Name);
        Assert.Equal("Original", updated.SortName);
        Assert.Equal(label.CreatedUtc, updated.CreatedUtc);
        Assert.NotEqual(etag, patch.Headers.ETag!.ToString());
        await ProblemAsync(await PatchAsync(label.Id, """{"name":"Stale"}""", etag), 412, "stale_version");
        await ProblemAsync(await PatchAsync(label.Id, """{"name":"Missing header"}""", null), 428, "precondition_required");
        var clear = await PatchAsync(label.Id, """{"sortName":null}""", patch.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null((await clear.Content.ReadFromJsonAsync<LabelResponse>())!.SortName);
        Assert.Equal("Edited Records", (await _client.GetFromJsonAsync<LabelResponse>($"/api/v1/labels/{label.Id}"))!.Name);
        var duplicate = await CreateAsync("Edited Records");
        Assert.NotEqual(label.Id, duplicate.Label.Id);
        var page = (await _client.GetFromJsonAsync<LabelListResponse>("/api/v1/labels?q=edited&pageSize=1&page=2"))!;
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal(2, page.Page);
        Assert.Equal(1, page.PageSize);
    }

    [Fact]
    public async Task DetailAndChildren_PreserveLegacyIdsAndHideProviderPayloads()
    {
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.Labels.UpsertAsync(new Label
            {
                Id = "legacy-label", Name = "Legacy", SourcePayloadJson = """{"private":"label"}""",
                Aliases = [new EntityAlias { Value = "Café Archive" }],
                ExternalReferences = [new EntityReference { Source = "beatport", ExternalId = "42", PayloadJson = """{"private":"ref"}""" }],
            });
            await unit.CommitAsync();
        }
        var get = await _client.GetAsync("/api/v1/labels/legacy-label");
        var json = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private", json);
        var detail = (await get.Content.ReadFromJsonAsync<LabelResponse>())!;
        Assert.Equal("legacy-label", detail.Id);
        Assert.Single(detail.Aliases);
        Assert.Single(detail.ExternalReferences);
        Assert.Single((await _client.GetFromJsonAsync<LabelAliasResponse[]>("/api/v1/labels/legacy-label/aliases"))!);
        Assert.Single((await _client.GetFromJsonAsync<LabelReferenceResponse[]>("/api/v1/labels/legacy-label/external-references"))!);
        var page = (await _client.GetFromJsonAsync<LabelListResponse>("/api/v1/labels?q=cafe&source=beatport&externalId=42"))!;
        Assert.Equal("legacy-label", Assert.Single(page.Items).Id);
        var patched = await PatchAsync(detail.Id, """{"name":"Renamed"}""", get.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        await using var verify = await Provider.BeginUnitOfWorkAsync();
        var stored = (await verify.Labels.GetByIdAsync(detail.Id))!;
        Assert.Equal("""{"private":"label"}""", stored.SourcePayloadJson);
        Assert.Single(stored.Aliases);
        Assert.Single(stored.ExternalReferences);
    }

    [Theory]
    [InlineData("/api/v1/labels/missing")]
    [InlineData("/api/v1/labels/missing/aliases")]
    [InlineData("/api/v1/labels/missing/external-references")]
    public async Task MissingResources_ReturnProblemDetails(string path)
        => await ProblemAsync(await _client.GetAsync(path), 404, "label_not_found");

    [Fact]
    public async Task PatchMissingResource_DoesNotInsert()
    {
        var (_, etag) = await CreateAsync();
        await ProblemAsync(await PatchAsync("missing", """{"name":"No insertion"}""", etag), 404, "label_not_found");
        Assert.Null(await new LabelService(Provider).GetAsync("missing"));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=garbage")]
    [InlineData("sort=invalid")]
    [InlineData("order=invalid")]
    [InlineData("externalId=42")]
    [InlineData("q=%25")]
    [InlineData("page=1&page=2")]
    [InlineData("databasePath=anything")]
    public async Task InvalidQueries_ReturnFieldErrors(string query)
    {
        var problem = await ProblemAsync(await _client.GetAsync($"/api/v1/labels?{query}"), 400, "validation_failed");
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData("""{"name":null}""")]
    [InlineData("""{"name":" "}""")]
    [InlineData("""{"sortName":123}""")]
    [InlineData("""{"id":"replacement"}""")]
    [InlineData("""{"aliases":[]}""")]
    [InlineData("""{"name":"First","name":"Second"}""")]
    [InlineData("""{"Name":"Wrong casing"}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task InvalidPatches_DoNotChangeVersion(string body)
    {
        var (label, etag) = await CreateAsync();
        await ProblemAsync(await PatchAsync(label.Id, body, etag), 400, "validation_failed");
        var get = await _client.GetAsync($"/api/v1/labels/{label.Id}");
        Assert.Equal(etag, get.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("W/\"weak\"")]
    [InlineData("\"invalid-base64\"")]
    [InlineData("\"YS==\"")]
    [InlineData("\"YQ==\", \"Yg==\"")]
    public async Task InvalidPreconditions_AreRejected(string tag)
    {
        var (label, _) = await CreateAsync();
        await ProblemAsync(await PatchAsync(label.Id, """{"name":"No edit"}""", tag), 400, "validation_failed");
    }

    [Fact]
    public async Task InvalidJsonMediaTypesAndOversizeBodies_ReturnProblems()
    {
        var (label, tag) = await CreateAsync();
        await ProblemAsync(await PatchAsync(label.Id, "{", tag), 400, "invalid_json");
        await ProblemAsync(await PatchAsync(label.Id, """{"name":"Edit"}""", tag, "application/json"), 415, "unsupported_media_type");
        var oversized = JsonSerializer.Serialize(new { name = new string('x', 17000) });
        await ProblemAsync(await _client.PostAsync("/api/v1/labels", new StringContent(oversized, Encoding.UTF8, "application/json")), 413, "body_too_large");
        foreach (var invalid in new[] { "{}", """{"name":null}""", """{"name":123}""", """{"name":"Valid","sourcePayloadJson":{}}""" })
            await ProblemAsync(await _client.PostAsync("/api/v1/labels", new StringContent(invalid, Encoding.UTF8, "application/json")), 400, "validation_failed");
    }

    [Fact]
    public async Task ConcurrentHttpEdits_OnlyOneSucceeds()
    {
        var (label, tag) = await CreateAsync();
        var results = await Task.WhenAll(
            PatchAsync(label.Id, """{"name":"First"}""", tag),
            PatchAsync(label.Id, """{"name":"Second"}""", tag));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        await ProblemAsync(Assert.Single(results, x => x.StatusCode == HttpStatusCode.PreconditionFailed), 412, "stale_version");
    }

    [Fact]
    public async Task LegacyEditInvalidatesHttpPrecondition()
    {
        var (label, tag) = await CreateAsync();
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var stored = (await unit.Labels.GetByIdAsync(label.Id))!;
            await unit.Labels.UpsertAsync(stored with { Aliases = [new EntityAlias { Value = "Added offline" }] });
            await unit.CommitAsync();
        }
        await ProblemAsync(await PatchAsync(label.Id, """{"name":"Stale HTTP edit"}""", tag), 412, "stale_version");
    }

    [Fact]
    public async Task OpenApi_DescribesRoutesSchemasSecurityAndPreconditions()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = doc.GetProperty("paths");
        var collection = paths.GetProperty("/api/v1/labels");
        var detail = paths.GetProperty("/api/v1/labels/{id}");
        Assert.True(collection.TryGetProperty("get", out var list));
        Assert.True(collection.TryGetProperty("post", out var create));
        Assert.True(detail.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty("/api/v1/labels/{id}/aliases", out _));
        Assert.True(paths.TryGetProperty("/api/v1/labels/{id}/external-references", out _));
        Assert.Equal(8, list.GetProperty("parameters").GetArrayLength());
        var patch = detail.GetProperty("patch");
        Assert.Contains(patch.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "If-Match");
        Assert.True(patch.GetProperty("responses").TryGetProperty("428", out _));
        Assert.True(patch.GetProperty("responses").TryGetProperty("412", out _));
        Assert.True(create.GetProperty("responses").GetProperty("201").GetProperty("headers").TryGetProperty("Location", out _));
        Assert.True(detail.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("bearer", schemes.GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.NotEmpty(doc.GetProperty("security").EnumerateArray());
        var schemas = doc.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("LabelResponse").GetProperty("properties").TryGetProperty("externalReferences", out _));
        Assert.False(schemas.GetProperty("LabelResponse").GetProperty("properties").TryGetProperty("sourcePayloadJson", out _));
        var patchSchema = patch.GetProperty("requestBody").GetProperty("content").GetProperty("application/merge-patch+json").GetProperty("schema");
        Assert.Equal("string", patchSchema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        Assert.True(patchSchema.GetProperty("properties").GetProperty("sortName").GetProperty("nullable").GetBoolean());
    }

    [Fact]
    public async Task UnexpectedFailuresAreLoggedAndSanitized()
    {
        await using var app = Build(builder =>
        {
            builder.Services.RemoveAll<ILabelService>();
            builder.Services.AddScoped<ILabelService>(_ => throw new InvalidOperationException("private SQL /sensitive/path"));
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var response = await client.GetAsync("/api/v1/labels");
        await ProblemAsync(response, 500, "internal_error");
        Assert.DoesNotContain("private SQL", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("CatalogApi:ApiKey", "short")]
    [InlineData("CatalogApi:ApiKey", "")]
    [InlineData("CatalogApi:DatabasePath", "relative.db")]
    [InlineData("CatalogApi:Port", "0")]
    [InlineData("Kestrel:Endpoints:Public:Url", "http://0.0.0.0:5187")]
    public void InvalidConfigurationFailsBeforeServing(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
    }

    [Fact]
    public async Task StartupRejectsUninitializedDatabaseWithoutMigratingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog-api-uninitialized-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(path, []);
            await using var app = Build(builder =>
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:DatabasePath"] = path }));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
            Assert.Contains("schema version 7", failure.Message);
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
