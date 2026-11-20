using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TrackStash.Catalog.Api;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed class SessionApiTests : IAsyncLifetime
{
    private const string Session = "/api/v1/session";
    private const string Origin = "http://localhost";
    private const string DevOrigin = "http://localhost:5173";
    private const string Key = "test-only-api-key-not-a-real-secret-12345";
    private const string Username = "test-admin";
    private const string Password = "test-only example password";
    private static readonly string Hash = HashPassword(Password);
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, $"session-tests-{Guid.NewGuid():N}");
    private readonly TestClock _clock = new();
    private string Database => Path.Combine(_directory, "catalog.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await new SqliteStorageProvider(Database).Migrations.ApplyPendingMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoginAndLogoutProtectApiAndRejectAnExplicitInvalidBearerWithValidCookie()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = app.GetTestClient();
        await AssertState(client, true, false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/labels")).StatusCode);
        using var login = await Login(client);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var setCookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("; httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        var cookie = setCookie.Split(';')[0];
        await AssertState(client, true, true, cookie);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/labels", cookie: cookie)).StatusCode);
        using var swagger = await Send(client, HttpMethod.Get, "/openapi/v1.json", cookie: cookie);
        await AssertOpenApiSchemes(swagger, "Bearer", "NativeSession");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(client, HttpMethod.Get, "/api/v1/labels", cookie: cookie, key: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, HttpMethod.Get, "/api/v1/labels", cookie: cookie, key: Key)).StatusCode);
        // API-key requests without Origin continue to work in loopback mode.
        Assert.Equal(HttpStatusCode.Created, (await Send(client, HttpMethod.Post, "/api/v1/labels",
            key: Key, body: JsonContent.Create(new { name = "Machine Client Records" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Post, "/api/v1/labels",
            cookie: cookie, body: JsonContent.Create(new { name = "Cookie Records" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, HttpMethod.Post, "/api/v1/labels", Origin,
            cookie, body: JsonContent.Create(new { name = "Cookie Records" }))).StatusCode);

        using var logout = await Send(client, HttpMethod.Delete, Session, Origin, cookie);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.StartsWith(cookie.Split('=')[0] + "=;", logout.Headers.GetValues("Set-Cookie").Single());
        await AssertState(client, true, false, cookie); // Replaying the original cookie fails server-side.
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, "/api/v1/labels", key: Key)).StatusCode);
    }

    [Fact]
    public async Task SessionWritesRequireOneExactOwnOriginEvenWithBearerOrAllowedDevOrigin()
    {
        await using var app = Build(extra: new() { ["CatalogApi:AllowedOrigins:0"] = DevOrigin });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var login = await Login(client);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        foreach (var origin in new[] { null, "", "null", DevOrigin, $"{Origin}/", "http://evil.test", $"{Origin}, {Origin}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Login(client, origin: origin)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Delete, Session, origin, cookie, Key)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, HttpMethod.Post, "/api/v1/labels", DevOrigin, cookie)).StatusCode);
        await AssertState(client, true, true, cookie);
    }

    [Fact]
    public async Task WrongCredentialsShareAnErrorAndThrottleAcrossUsernames()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = app.GetTestClient();
        for (var i = 0; i < 5; i++)
        {
            using var response = await Login(client, i % 2 == 0 ? Username : "unknown-admin",
                i % 2 == 0 ? "wrong-password" : Password);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid_credentials", error.RootElement.GetProperty("code").GetString());
            Assert.Equal("Invalid username or password", error.RootElement.GetProperty("title").GetString());
        }
        using var limited = await Login(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client)).StatusCode);
    }

    [Fact]
    public async Task InvalidAndOversizedPayloadsNeverIssueACookie()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var (body, mediaType, expected) in new[]
        {
            ("{", "application/json", 400),
            ("{}", "application/json", 400),
            ("null", "application/json", 400),
            (new string('x', 4097), "application/json", 413),
            ("{}", "text/plain", 415),
        })
        {
            using var response = await Send(client, HttpMethod.Post, Session, Origin,
                body: new StringContent(body, Encoding.UTF8, mediaType));
            Assert.Equal(expected, (int)response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        var chunked = await app.GetTestServer().SendAsync(context =>
        {
            context.Request.Host = new HostString("localhost");
            context.Request.Method = "POST";
            context.Request.Path = Session;
            context.Request.Headers.Origin = Origin;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(new byte[4097]);
        });
        Assert.Equal(413, chunked.Response.StatusCode);
    }

    [Fact]
    public async Task ExpirationAndPasswordRotationInvalidateSessions()
    {
        string cookie;
        await using (var app = Build())
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var initial = await Login(client);
            var oldCookie = initial.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            using var replacement = await Login(client, cookie: oldCookie);
            cookie = replacement.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            Assert.NotEqual(oldCookie, cookie);
            await AssertState(client, true, false, oldCookie);
            await AssertState(client, true, true, cookie);
            _clock.Advance(TimeSpan.FromHours(8));
            await AssertState(client, true, false, cookie);
            using var fresh = await Login(client);
            cookie = fresh.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        }

        const string changedPassword = "another test-only example password";
        await using var rotated = Build(hash: HashPassword(changedPassword));
        await rotated.StartAsync();
        using var rotatedClient = rotated.GetTestClient();
        await AssertState(rotatedClient, true, false, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(rotatedClient)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(rotatedClient, password: changedPassword)).StatusCode);
    }

    [Fact]
    public async Task ModeOffReportsUnavailableAndPreservesBearerAndOpenApi()
    {
        await using var app = Build(username: null, hash: null);
        await app.StartAsync();
        using var client = app.GetTestClient();
        await AssertState(client, false, false, cookie: "TrackStash.Catalog.Session=fake", key: Key);
        Assert.Equal(HttpStatusCode.NotFound, (await Login(client)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Delete, Session, Origin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/api/v1/labels")).StatusCode);
        using var swagger = await Send(client, HttpMethod.Get, "/openapi/v1.json", key: Key);
        await AssertOpenApiSchemes(swagger, "Bearer");
        using var doc = JsonDocument.Parse(await swagger.Content.ReadAsStringAsync());
        var operations = doc.RootElement.GetProperty("paths").GetProperty(Session);
        Assert.Empty(operations.GetProperty("get").GetProperty("security").EnumerateArray().Single().EnumerateObject());
        var post = operations.GetProperty("post");
        Assert.True(post.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.True(post.GetProperty("responses").TryGetProperty("204", out _));
        Assert.True(operations.GetProperty("delete").GetProperty("responses").TryGetProperty("204", out _));
    }

    [Fact]
    public async Task NativeOnlyAuthenticatesWithoutKeyAndRejectsExplicitAuthorization()
    {
        await using var app = Build(apiKey: null);
        await app.StartAsync();
        using var client = app.GetTestClient();
        await AssertState(client, true, false);
        using var anonymous = await Send(client, HttpMethod.Get, "/openapi/v1.json");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(anonymous.Headers.WwwAuthenticate);
        using var login = await Login(client);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var rejected = await Send(client, HttpMethod.Get, "/openapi/v1.json", cookie: cookie, key: Key);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var authenticated = await Send(client, HttpMethod.Get, "/openapi/v1.json", cookie: cookie);
        await AssertOpenApiSchemes(authenticated, "NativeSession");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("test-only-api-key-with-a space-12345")]
    public void InvalidProvidedKeyFailsEvenWithValidNativeCredentials(string key)
        => Assert.Throws<InvalidOperationException>(() => Build(apiKey: key));

    [Fact]
    public void NoAuthenticationModeFailsStartup()
        => Assert.Throws<InvalidOperationException>(() => Build(username: null, hash: null, apiKey: null));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(Username, false)]
    public void PartialOrInvalidConfigurationFailsClosed(string? username, bool includeHash)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(username, includeHash ? "malformed-hash" : null));
        Assert.Contains("CatalogApi:AdminPasswordHash", error.Message);
        Assert.DoesNotContain("malformed-hash", error.Message);
    }

    [Fact]
    public async Task ProxyCookieRequiresTrustedPeerHttpsAndExactPublicOrigin()
    {
        const string host = "catalog.example.test:8443";
        const string publicOrigin = "https://" + host;
        await using var app = Build(extra: new()
        {
            ["CatalogApi:PublicOrigin"] = publicOrigin,
            ["CatalogApi:TrustedProxyIp"] = "192.0.2.10",
            ["CatalogApi:ListenAddress"] = "0.0.0.0",
        });
        await app.StartAsync();
        Task<HttpContext> SendProxy(string method = "POST", string peer = "192.0.2.10",
            string scheme = "https", string? origin = publicOrigin, string? cookie = null,
            string path = Session, string requestHost = host)
            => app.GetTestServer().SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                context.Request.Method = method;
                context.Request.Path = path;
                context.Request.Host = new HostString(requestHost);
                context.Request.Headers["X-Forwarded-Proto"] = scheme;
                if (origin is not null) context.Request.Headers.Origin = origin;
                if (cookie is not null) context.Request.Headers.Cookie = cookie;
                if (method == "POST")
                {
                    var body = JsonSerializer.SerializeToUtf8Bytes(new { username = Username, password = Password });
                    context.Request.ContentType = "application/json";
                    context.Request.ContentLength = body.Length;
                    context.Request.Body = new MemoryStream(body);
                }
            });

        Assert.Equal(403, (await SendProxy(peer: "192.0.2.11")).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(scheme: "http")).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(requestHost: "catalog.example.test")).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(origin: Origin)).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(origin: null)).Response.StatusCode);
        var login = await SendProxy();
        Assert.Equal(204, login.Response.StatusCode);
        var setCookie = login.Response.Headers.SetCookie.Single()!;
        Assert.StartsWith("__Host-TrackStash.Catalog.Session=", setCookie);
        Assert.Contains("; secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        var cookie = setCookie.Split(';')[0];
        Assert.Equal(200, (await SendProxy(method: "GET", cookie: cookie, path: "/api/v1/labels")).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(method: "DELETE", origin: null, cookie: cookie)).Response.StatusCode);
        Assert.Equal(204, (await SendProxy(method: "DELETE", cookie: cookie)).Response.StatusCode);
        Assert.Equal(401, (await SendProxy(method: "GET", cookie: cookie, path: "/api/v1/labels")).Response.StatusCode);
    }

    [Fact]
    public async Task TrustedProxyIdentityRequiresTrustedPeerAndProtectsWrites()
    {
        const string origin = "https://catalog.example.test";
        await using var app = Build(username: null, hash: null, apiKey: null, extra: new()
        {
            ["CatalogApi:PublicOrigin"] = origin,
            ["CatalogApi:TrustedProxyIp"] = "192.0.2.10",
            ["CatalogApi:ListenAddress"] = "0.0.0.0",
            ["CatalogApi:TrustedProxyIdentity"] = "true",
            ["CatalogApi:TrustedProxyLogoutPath"] = "/outpost.goauthentik.io/sign_out",
        });
        await app.StartAsync();
        Task<HttpContext> SendProxy(string method = "GET", string path = "/api/v1/labels",
            string peer = "192.0.2.10", string? username = "approved-user", string? requestOrigin = null,
            string? authorization = null, bool duplicate = false)
            => app.GetTestServer().SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                context.Request.Method = method;
                context.Request.Path = path;
                context.Request.Host = new HostString("catalog.example.test");
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                if (username is not null)
                {
                    context.Request.Headers.Append("X-Trackstash-User", username);
                    if (duplicate) context.Request.Headers.Append("X-Trackstash-User", "other-user");
                }
                if (requestOrigin is not null) context.Request.Headers.Origin = requestOrigin;
                if (authorization is not null) context.Request.Headers.Authorization = authorization;
                if (method == "POST")
                {
                    var body = JsonSerializer.SerializeToUtf8Bytes(new { name = "Proxy Records" });
                    context.Request.ContentType = "application/json";
                    context.Request.ContentLength = body.Length;
                    context.Request.Body = new MemoryStream(body);
                }
            });

        Assert.Equal(200, (await SendProxy()).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(peer: "192.0.2.11")).Response.StatusCode);
        Assert.Equal(401, (await SendProxy(username: null)).Response.StatusCode);
        Assert.Equal(401, (await SendProxy(username: "")).Response.StatusCode);
        Assert.Equal(401, (await SendProxy(username: "first,second")).Response.StatusCode);
        Assert.Equal(401, (await SendProxy(duplicate: true)).Response.StatusCode);
        Assert.Equal(401, (await SendProxy(authorization: "Bearer invalid")).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(method: "POST")).Response.StatusCode);
        Assert.Equal(403, (await SendProxy(method: "POST", requestOrigin: "https://evil.example")).Response.StatusCode);
        Assert.Equal(201, (await SendProxy(method: "POST", requestOrigin: origin)).Response.StatusCode);

        var status = await SendProxy(path: Session);
        using var json = await JsonDocument.ParseAsync(status.Response.Body);
        Assert.False(json.RootElement.GetProperty("available").GetBoolean());
        Assert.True(json.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("approved-user", json.RootElement.GetProperty("proxyUser").GetString());
        Assert.Equal("/outpost.goauthentik.io/sign_out", json.RootElement.GetProperty("proxyLogoutPath").GetString());
        var openApi = await SendProxy(path: "/openapi/v1.json");
        using var openApiJson = await JsonDocument.ParseAsync(openApi.Response.Body);
        Assert.True(openApiJson.RootElement.GetProperty("components").GetProperty("securitySchemes")
            .TryGetProperty("TrustedProxyIdentity", out _));
    }

    [Fact]
    public async Task TrustedIdentityCanCoexistWithNativeAndBearerAuthentication()
    {
        await using var app = Build(extra: new()
        {
            ["CatalogApi:PublicOrigin"] = "https://catalog.example.test",
            ["CatalogApi:TrustedProxyIp"] = "192.0.2.10",
            ["CatalogApi:ListenAddress"] = "0.0.0.0",
            ["CatalogApi:TrustedProxyIdentity"] = "true",
            ["CatalogApi:TrustedProxyLogoutPath"] = "/outpost.goauthentik.io/sign_out",
        });
        await app.StartAsync();
        Task<HttpContext> SendProxy(string? user, string? bearer = null, string? cookie = null)
            => app.GetTestServer().SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                context.Request.Method = "GET";
                context.Request.Path = "/api/v1/labels";
                context.Request.Host = new HostString("catalog.example.test");
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                if (user is not null) context.Request.Headers["X-Trackstash-User"] = user;
                if (bearer is not null) context.Request.Headers.Authorization = $"Bearer {bearer}";
                if (cookie is not null) context.Request.Headers.Cookie = cookie;
            });
        Assert.Equal(200, (await SendProxy(null, Key)).Response.StatusCode);
        Assert.Equal(200, (await SendProxy("approved-user")).Response.StatusCode);
        Assert.Equal(401, (await SendProxy("approved-user", "invalid")).Response.StatusCode);
        Assert.Equal(401, (await SendProxy(null)).Response.StatusCode);
        var login = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
            context.Request.Method = "POST";
            context.Request.Path = Session;
            context.Request.Host = new HostString("catalog.example.test");
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers.Origin = "https://catalog.example.test";
            var body = JsonSerializer.SerializeToUtf8Bytes(new { username = Username, password = Password });
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
        });
        Assert.Equal(204, login.Response.StatusCode);
        var cookie = login.Response.Headers.SetCookie.Single()!.Split(';')[0];
        Assert.Equal(200, (await SendProxy(null, cookie: cookie)).Response.StatusCode);
    }

    [Theory]
    [InlineData("true", null, false)]
    [InlineData("true", "https://evil.example/logout", true)]
    [InlineData("true", "//evil.example/logout", true)]
    [InlineData("true", "/", true)]
    [InlineData("true", "/a/../logout", true)]
    [InlineData("invalid", null, true)]
    [InlineData("false", "/logout", true)]
    public void InvalidTrustedIdentityConfigurationFailsStartup(string enabled, string? logout, bool proxy)
    {
        var extra = new Dictionary<string, string?>
        {
            ["CatalogApi:TrustedProxyIdentity"] = enabled,
            ["CatalogApi:TrustedProxyLogoutPath"] = logout,
        };
        if (proxy)
        {
            extra["CatalogApi:PublicOrigin"] = "https://catalog.example.test";
            extra["CatalogApi:TrustedProxyIp"] = "192.0.2.10";
            extra["CatalogApi:ListenAddress"] = "0.0.0.0";
        }
        Assert.Throws<InvalidOperationException>(() => Build(extra: extra));
    }

    [Fact]
    public async Task IdentityHeaderIsIgnoredWhenNotEnabled()
    {
        await using var app = Build();
        await app.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/labels");
        request.Headers.Add("X-Trackstash-User", "spoofed");
        using var response = await app.GetTestClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private WebApplication Build(string? username = Username, string? hash = "default", Dictionary<string, string?>? extra = null,
        string? apiKey = Key)
        => CatalogApiHost.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CatalogApi:ApiKey"] = apiKey, ["CatalogApi:DatabasePath"] = Database,
                ["CatalogApi:BackupRoot"] = Path.Combine(_directory, "backups"),
                ["CatalogApi:MediaLibrarySettingsPath"] = Path.Combine(_directory, "media-settings.json"),
                ["CatalogApi:ScannerDatabasePath"] = "", ["CatalogApi:PurchaseSourceDatabasePath"] = "",
                ["CatalogApi:PublicOrigin"] = null, ["CatalogApi:TrustedProxyIp"] = null, ["CatalogApi:ListenAddress"] = null,
                ["CatalogApi:AdminUsername"] = username, ["CatalogApi:AdminPasswordHash"] = hash == "default" ? Hash : hash,
            });
            if (extra is not null) builder.Configuration.AddInMemoryCollection(extra);
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(_clock);
            builder.Services.AddSingleton<IArtworkContentStore>(new UnusedArtworkStore(Path.Combine(_directory, "artwork")));
            builder.Services.AddSingleton<ICatalogBackupService, UnusedBackupService>();
        });

    private static string HashPassword(string password) =>
        new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }))
            .HashPassword(new object(), password);

    private static async Task AssertOpenApiSchemes(HttpResponseMessage response, params string[] expected)
    {
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var schemes = root.GetProperty("components").GetProperty("securitySchemes").EnumerateObject().Select(s => s.Name);
        var requirements = root.GetProperty("security").EnumerateArray().Select(r => r.EnumerateObject().Single().Name);
        Assert.Equal(expected, schemes.Order(StringComparer.Ordinal));
        Assert.Equal(expected, requirements.Order(StringComparer.Ordinal));
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string username = Username, string password = Password,
        string? origin = Origin, string? cookie = null)
        => Send(client, HttpMethod.Post, Session, origin, cookie, body: JsonContent.Create(new { username, password }));

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path,
        string? origin = null, string? cookie = null, string? key = null, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await client.SendAsync(request);
    }

    private static async Task AssertState(HttpClient client, bool available, bool authenticated, string? cookie = null, string? key = null)
    {
        using var response = await Send(client, HttpMethod.Get, Session, cookie: cookie, key: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Equal(available, json.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(authenticated, json.RootElement.GetProperty("authenticated").GetBoolean());
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    // Auth tests use a real catalog database but must never read or write artwork.
    private sealed class UnusedArtworkStore(string root) : IArtworkContentStore
    {
        public string Root => root;
        public ValueTask SaveAsync(string id, byte[] original, byte[]? thumbnail, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<byte[]> ReadAsync(string id, bool thumbnail, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DeleteUncommittedAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class UnusedBackupService : ICatalogBackupService
    {
        public Task<CatalogBackupResult> CreateAsync(IProgress<string>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogBackupInfo>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenDownloadAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<BackupManifest> VerifyAsync(string archive, CancellationToken ct) => throw new NotSupportedException();
        public Task RestoreAsync(string archive, string destination, CancellationToken ct) => throw new NotSupportedException();
    }
}
