using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private sealed class RequestBodyDetection : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
    [Fact]
    public async Task RemoteInventory_ScopedDeviceUploadIsAtomicReplayableAndBrowsableOnlyByAdmin()
    {
        var root = Path.Combine(Path.GetTempPath(), "trackstash-remote-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string deviceKey = "test-device-token-distinct-from-admin-123456";
        var db = Path.Combine(root, "remote.db");
        var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:RemoteInventoryDatabasePath"] = db,
            ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner",
            ["CatalogApi:InventoryDeviceToken"] = deviceKey,
        }));
        try
        {
            await app.StartAsync();
            using var admin = app.GetTestClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
            var item = new RemoteInventoryRecord {
                MediaFileId = 1, Path = "/music/tune.flac", Format = "flac", SizeBytes = 10,
                ScannedUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                Title = "Tune", Artist = "Artist", Label = "Label", Release = "Release",
                FingerprintRaw = "private-fingerprint", FingerprintAlgorithm = "chromaprint", FingerprintVersion = "1"
            };
            var batch = new RemoteInventoryBatchRequest { Items = [item] };
            Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PostAsJsonAsync("/api/v1/remote-inventory/batches", batch)).StatusCode);
            Assert.False(File.Exists(db));
            Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/v1/labels")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/v1/remote-inventory")).StatusCode);
            var uploaded = await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", batch);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            Assert.Equal(1, (await uploaded.Content.ReadFromJsonAsync<RemoteInventoryBatchResponse>())!.Saved);
            Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", batch)).StatusCode);
            var page = (await admin.GetFromJsonAsync<RemoteInventoryPage>("/api/v1/remote-inventory?q=label"))!;
            var result = Assert.Single(page.Items);
            Assert.Equal("owner", result.OwnerId);
            Assert.Equal("mac", result.DeviceId);
            Assert.Equal("Tune", result.Title);
            Assert.Equal(1, page.Total);
            Assert.DoesNotContain("private-fingerprint", await admin.GetStringAsync("/api/v1/remote-inventory"));
            var older = item with { ScannedUtc = item.ScannedUtc.AddDays(-1), Title = "Old" };
            var replay = await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", new RemoteInventoryBatchRequest { Items = [older] });
            Assert.Equal(1, (await replay.Content.ReadFromJsonAsync<RemoteInventoryBatchResponse>())!.Older);
            Assert.Equal("Tune", Assert.Single((await admin.GetFromJsonAsync<RemoteInventoryPage>("/api/v1/remote-inventory"))!.Items).Title);
            var invalid = item with { MediaFileId = 2, Path = "" };
            Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                new RemoteInventoryBatchRequest { Items = [item with { Title = "Changed" }, invalid] })).StatusCode);
            Assert.Equal("Tune", Assert.Single((await admin.GetFromJsonAsync<RemoteInventoryPage>("/api/v1/remote-inventory"))!.Items).Title);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/remote-inventory?page=0")).StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemoteInventory_PreservesTextAndNumericScannerTags()
    {
        var db = _path + ".remote";
        const string deviceKey = "test-device-token-distinct-from-admin-123456";
        var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = deviceKey
        }));
        try
        {
            await app.StartAsync();
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
            var items = new object[] {
                new { media_file_id = 1, path = "/music/a", format = "flac", size_bytes = 1,
                    scanned_utc = "2026-01-02T00:00:00Z", last_modified_utc = "2026-01-01T00:00:00Z",
                    track_number = "2/11", disc_number = 1, bpm = "120 BPM", fingerprint_raw = new string('a', 233004) },
                new { media_file_id = 2, path = "/music/b", format = "flac", size_bytes = 1,
                    scanned_utc = "2026-01-02T00:00:00Z", last_modified_utc = "2026-01-01T00:00:00Z",
                    track_number = 3, disc_number = "1/2", bpm = 122.5 }
            };
            Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", new { items })).StatusCode);
            await using var connection = new SqliteConnection("Data Source=" + db);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_json FROM remote_inventory ORDER BY media_file_id";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            using var first = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal("2/11", first.RootElement.GetProperty("track_number").GetString());
            Assert.Equal(1, first.RootElement.GetProperty("disc_number").GetInt32());
            Assert.Equal("120 BPM", first.RootElement.GetProperty("bpm").GetString());
            Assert.Equal(233004, first.RootElement.GetProperty("fingerprint_raw").GetString()!.Length);
            Assert.True(await reader.ReadAsync());
            using var second = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal(3, second.RootElement.GetProperty("track_number").GetInt32());
            Assert.Equal("1/2", second.RootElement.GetProperty("disc_number").GetString());
            Assert.Equal(122.5, second.RootElement.GetProperty("bpm").GetDouble());

            var invalid = new { items = new[] { new { media_file_id = 3, path = "/music/c", format = "flac", size_bytes = 1,
                scanned_utc = "2026-01-02T00:00:00Z", last_modified_utc = "2026-01-01T00:00:00Z", bpm = new { value = 120 } } } };
            Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", invalid)).StatusCode);
            var oversized = new { items = new[] { new { media_file_id = 3, path = "/music/c", format = "flac", size_bytes = 1,
                scanned_utc = "2026-01-02T00:00:00Z", last_modified_utc = "2026-01-01T00:00:00Z", fingerprint_raw = new string('a', 800001) } } };
            Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", oversized)).StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Fact]
    public async Task RemoteInventory_MatchesHistoricalScannerMetadataWithoutAccessToAudio()
    {
        await ImportSql(TrackFixture);
        await ImportSql("UPDATE recording SET duration_ms=400000 WHERE recording_id='t-shared'");
        var db = _path + ".remote";
        const string deviceKey = "test-device-token-distinct-from-admin-123456";
        await using var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = deviceKey
        }));
        try
        {
            await app.StartAsync();
            using var admin = app.GetTestClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
            var items = new[] {
                new RemoteInventoryRecord { MediaFileId = 1, Path = "/offline/a.flac", Format = "flac", SizeBytes = 10,
                    ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
                    Title = "Shared tune", Subtitle = "Original Mix", Artist = "Zed Primary", Isrc = "GBTRK0000001", DurationSeconds = 400,
                    FingerprintRaw = "fingerprint+example", FingerprintAlgorithm = "chromaprint", FingerprintVersion = "1" },
                new RemoteInventoryRecord { MediaFileId = 2, Path = "/offline/b.flac", Format = "flac", SizeBytes = 10,
                    ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
                    Title = "Shared tune", Subtitle = "Original Mix", Artist = "Zed Primary", Isrc = "GBTRK0000001", DurationSeconds = 400,
                    FingerprintRaw = "fingerprint+example", FingerprintAlgorithm = "chromaprint", FingerprintVersion = "1" }
            };
            Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                new RemoteInventoryBatchRequest { Items = items })).StatusCode);
            var keys = new[] { new RemoteInventoryKey("owner", "mac", 1), new RemoteInventoryKey("owner", "mac", 2) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync("/api/v1/remote-inventory/match-proposals",
                new RemoteMatchProposalsRequest(keys))).StatusCode);
            var response = await admin.PostAsJsonAsync("/api/v1/remote-inventory/match-proposals", new RemoteMatchProposalsRequest(keys));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var proposals = (await response.Content.ReadFromJsonAsync<RemoteMatchProposal[]>())!;
            Assert.All(proposals, proposal => Assert.Equal("t-shared", proposal.SuggestedRecordingId));
            var selections = proposals.Select(proposal => new RemoteMatchSelection(proposal.Key, proposal.Version, "t-shared")).ToArray();
            var stale = selections[1] with { Version = new string('0', 64) };
            await ProblemAsync(await admin.PostAsJsonAsync("/api/v1/remote-inventory/matches",
                new RemoteMatchRequest([selections[0], stale])), 409, "remote_inventory_changed");
            Assert.Equal(0, await ImportCount("media_file"));
            response = await admin.PostAsJsonAsync("/api/v1/remote-inventory/matches", new RemoteMatchRequest(selections));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, (await response.Content.ReadFromJsonAsync<RemoteMatchResult>())!.Saved);
            Assert.Equal(2, (await admin.GetFromJsonAsync<RecordingResponse>("/api/v1/recordings/t-shared"))!.LinkedCopyCount);
            var linked = (await admin.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!;
            Assert.Equal(2, linked.Length);
            Assert.All(linked, item => Assert.Null(item.Sha256));
            Assert.All(linked, item => Assert.False(item.VerifiedAtLink));
            Assert.All(linked, item => Assert.NotNull(item.FingerprintKey));
            Assert.Single(linked.Select(item => item.FingerprintKey).Distinct());
            const string fingerprintJson = """{"algorithm":"chromaprint","version":"1","fingerprint":"fingerprint+example"}""";
            var expectedKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fingerprintJson)))).ToLowerInvariant();
            Assert.All(linked, item => Assert.Equal(expectedKey, item.FingerprintKey));
            response = await admin.PostAsJsonAsync("/api/v1/remote-inventory/matches", new RemoteMatchRequest(selections));
            Assert.Equal(2, (await response.Content.ReadFromJsonAsync<RemoteMatchResult>())!.AlreadyLinked);
            await ProblemAsync(await admin.PostAsJsonAsync("/api/v1/remote-inventory/matches",
                new RemoteMatchRequest([selections[0] with { RecordingId = "t-second" }])), 409, "media_already_linked");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Fact]
    public async Task RemoteInventory_ManualChoiceAndIdenticalScannerIdsRemainDistinct()
    {
        await ImportSql(TrackFixture);
        var db = _path + ".remote";
        const string deviceKey = "test-device-token-distinct-from-admin-123456";
        await using var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = deviceKey
        }));
        try
        {
            await app.StartAsync();
            using var admin = app.GetTestClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
            var item = new RemoteInventoryRecord { MediaFileId = 1, Path = "/offline/a.flac", Format = "flac", SizeBytes = 10,
                ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow, Title = "Unrelated title" };
            Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                new RemoteInventoryBatchRequest { Items = [item] })).StatusCode);
            await using (var connection = new SqliteConnection("Data Source=" + db))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO remote_inventory
                    SELECT 'other-owner', device_id, media_file_id, scanned_utc, item_json FROM remote_inventory;
                    INSERT INTO remote_inventory
                    SELECT owner_id, 'other-device', media_file_id, scanned_utc, item_json
                    FROM remote_inventory WHERE owner_id='owner';
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var keys = new[] { new RemoteInventoryKey("owner", "mac", 1), new RemoteInventoryKey("other-owner", "mac", 1),
                new RemoteInventoryKey("owner", "other-device", 1) };
            var response = await admin.PostAsJsonAsync("/api/v1/remote-inventory/match-proposals", new RemoteMatchProposalsRequest(keys));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var proposals = (await response.Content.ReadFromJsonAsync<RemoteMatchProposal[]>())!;
            Assert.All(proposals, proposal => Assert.Null(proposal.SuggestedRecordingId));
            var selections = proposals.Select(proposal => new RemoteMatchSelection(proposal.Key, proposal.Version, "t-second")).ToArray();
            response = await admin.PostAsJsonAsync("/api/v1/remote-inventory/matches", new RemoteMatchRequest(selections));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, (await response.Content.ReadFromJsonAsync<RemoteMatchResult>())!.Saved);
            Assert.Equal(3, await ImportCount("media_file"));
            response = await admin.PostAsJsonAsync("/api/v1/remote-inventory/match-proposals", new RemoteMatchProposalsRequest(keys));
            Assert.All((await response.Content.ReadFromJsonAsync<RemoteMatchProposal[]>())!,
                proposal => Assert.Equal("t-second", proposal.LinkedRecordingId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Fact]
    public async Task RemoteInventory_IsDisabledWithoutDeviceConfiguration()
    {
        using var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/v1/remote-inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/remote-inventory/batches",
            new RemoteInventoryBatchRequest { Items = [] })).StatusCode);
    }

    [Fact]
    public async Task RemoteInventory_PersistsAcrossRestartAndTokenRotation()
    {
        var db = _path + ".remote";
        const string oldKey = "test-device-token-distinct-from-admin-123456";
        const string newKey = "replacement-device-token-distinct-123456789";
        Microsoft.AspNetCore.Builder.WebApplication CreateApp(string token) => Build(builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
                ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = token
            }));
        try
        {
            await using (var app = CreateApp(oldKey))
            {
                await app.StartAsync();
                using var device = app.GetTestClient();
                device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldKey);
                var item = new RemoteInventoryRecord { MediaFileId = 1, Path = "/a", Format = "flac", SizeBytes = 1,
                    ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow };
                Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                    new RemoteInventoryBatchRequest { Items = [item] })).StatusCode);
            }
            await using (var app = CreateApp(newKey))
            {
                await app.StartAsync();
                using var client = app.GetTestClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldKey);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                    new RemoteInventoryBatchRequest { Items = [] })).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newKey);
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                    new RemoteInventoryBatchRequest { Items = [] })).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
                Assert.Equal(1, (await client.GetFromJsonAsync<RemoteInventoryPage>("/api/v1/remote-inventory"))!.Total);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteInventory_RejectsAnExistingUnrelatedDatabaseWithoutChangingIt(bool empty)
    {
        var db = _path + ".other";
        if (empty) await File.WriteAllBytesAsync(db, []);
        else await using (var connection = new SqliteConnection("Data Source=" + db))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE important_data(value TEXT); INSERT INTO important_data VALUES('preserve');";
            await command.ExecuteNonQueryAsync();
        }
        var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = "test-device-token-distinct-from-admin-123456"
        }));
        try
        {
            await app.StartAsync();
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-device-token-distinct-from-admin-123456");
            var item = new RemoteInventoryRecord { MediaFileId = 1, Path = "/a", Format = "flac", SizeBytes = 1,
                ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow };
            await ProblemAsync(await device.PostAsJsonAsync("/api/v1/remote-inventory/batches",
                new RemoteInventoryBatchRequest { Items = [item] }), 409, "remote_inventory_invalid_store");
            if (empty) Assert.Equal(0, new FileInfo(db).Length);
            else
            {
                await using var connection = new SqliteConnection("Data Source=" + db);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM important_data";
                Assert.Equal("preserve", await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Fact]
    public async Task RemoteInventory_ProxyRequiresTrustedPeerExactOriginAndDeviceCredential()
    {
        const string origin = "https://catalog.example.test";
        const string token = "test-device-token-distinct-from-admin-123456";
        var db = _path + ".remote";
        var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:PublicOrigin"] = origin, ["CatalogApi:TrustedProxyIp"] = "192.0.2.10",
            ["CatalogApi:ListenAddress"] = "0.0.0.0", ["CatalogApi:TrustedProxyIdentity"] = "true",
            ["CatalogApi:TrustedProxyLogoutPath"] = "/outpost.goauthentik.io/sign_out",
            ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = token
        }));
        try
        {
            await app.StartAsync();
            Task<HttpContext> Send(string peer = "192.0.2.10", string? requestOrigin = origin, string? bearer = token,
                string path = "/api/v1/remote-inventory/batches", string method = "POST")
                => app.GetTestServer().SendAsync(context => {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                    context.Request.Host = new HostString("catalog.example.test");
                    context.Request.Headers["X-Forwarded-Proto"] = "https";
                    context.Request.Headers["X-Trackstash-User"] = "approved-user";
                    if (requestOrigin is not null) context.Request.Headers.Origin = requestOrigin;
                    if (bearer is not null) context.Request.Headers.Authorization = "Bearer " + bearer;
                    context.Request.Path = path;
                    context.Request.Method = method;
                    if (method == "POST")
                    {
                        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetection());
                        var body = JsonSerializer.SerializeToUtf8Bytes(new { items = new[] { new {
                            media_file_id = 1, path = "/music/a", format = "flac", size_bytes = 1,
                            scanned_utc = "2026-01-02T00:00:00Z", last_modified_utc = "2026-01-01T00:00:00Z"
                        } } });
                        context.Request.ContentType = "application/json";
                        context.Request.ContentLength = body.Length;
                        context.Request.Body = new MemoryStream(body);
                    }
                });
            Assert.Equal(403, (await Send(peer: "192.0.2.11")).Response.StatusCode);
            Assert.Equal(403, (await Send(requestOrigin: null)).Response.StatusCode);
            Assert.Equal(403, (await Send(requestOrigin: "https://other.example.test")).Response.StatusCode);
            Assert.Equal(401, (await Send(bearer: null)).Response.StatusCode);
            Assert.Equal(401, (await Send(bearer: Key)).Response.StatusCode);
            var accepted = await Send();
            var bodyText = await new StreamReader(accepted.Response.Body).ReadToEndAsync();
            Assert.True(accepted.Response.StatusCode == 200, $"Unexpected status {accepted.Response.StatusCode}: {bodyText}");
            Assert.Equal(401, (await Send(path: "/api/v1/labels", method: "GET")).Response.StatusCode);
            Assert.Equal(200, (await Send(path: "/api/v1/remote-inventory", method: "GET", bearer: null)).Response.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }
}
