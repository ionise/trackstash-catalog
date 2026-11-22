using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    [Fact]
    public async Task RemotePseudo_CreatesAndReplaysUsingUnverifiedSnapshots()
    {
        var db = _path + ".remote-pseudo";
        const string token = "test-device-token-distinct-from-admin-123456";
        await using var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = token
        }));
        try
        {
            await app.StartAsync();
            using var admin = app.GetTestClient();
            admin.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var item = new RemoteInventoryRecord { MediaFileId = 1, Path = "/offline/a.flac", Format = "flac",
                SizeBytes = 10, ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
                Title = "My bootleg", Artist = "New artist" };
            var copy = item with { MediaFileId = 2, Path = "/offline/copy.flac" };
            await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", new RemoteInventoryBatchRequest { Items = [item, copy] });
            var snapshots = (await admin.GetFromJsonAsync<RemoteInventoryPage>("/api/v1/remote-inventory"))!.Items;
            var request = new CreateRemotePseudoRequest(Guid.NewGuid().ToString("N"), "My edits", null,
                [new(new("owner", "mac", 1), snapshots.Single(x => x.MediaFileId == 1).Version, null, null, "My bootleg", null, null, "New artist"),
                 new(new("owner", "mac", 2), snapshots.Single(x => x.MediaFileId == 2).Version, null, 1, null, null, null, null)]);
            var created = await admin.PostAsJsonAsync("/api/v1/remote-inventory/pseudo-releases", request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var outcome = await created.Content.ReadFromJsonAsync<PseudoReleaseOutcome>();
            Assert.Equal(2, outcome!.MediaFiles);
            Assert.Equal(1, outcome.Recordings);
            Assert.Equal(1, await ImportCount("release"));
            Assert.Equal(2, await ImportCount("media_file_recording_match WHERE evidence_json LIKE '%\"FileVerified\":false%'"));
            Assert.Equal(1, await ImportCount("release_recording"));
            var replay = await admin.PostAsJsonAsync("/api/v1/remote-inventory/pseudo-releases", request);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(outcome.ReleaseId, (await replay.Content.ReadFromJsonAsync<PseudoReleaseOutcome>())!.ReleaseId);
            Assert.Equal(1, await ImportCount("release"));
            await ProblemAsync(await admin.PostAsJsonAsync("/api/v1/remote-inventory/pseudo-releases",
                request with { Title = "Different" }), 409, "pseudo_operation_conflict");
        }
        finally { File.Delete(db); }
    }

    [Fact]
    public async Task RemotePseudo_StaleSnapshotRollsBackEntireRelease()
    {
        var db = _path + ".remote-pseudo-stale";
        const string token = "test-device-token-distinct-from-admin-123456";
        await using var app = Build(builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:RemoteInventoryDatabasePath"] = db, ["CatalogApi:InventoryDeviceId"] = "mac",
            ["CatalogApi:InventoryOwnerId"] = "owner", ["CatalogApi:InventoryDeviceToken"] = token
        }));
        try
        {
            await app.StartAsync();
            using var admin = app.GetTestClient();
            admin.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
            using var device = app.GetTestClient();
            device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var item = new RemoteInventoryRecord { MediaFileId = 1, Path = "/offline/a.flac", Format = "flac",
                SizeBytes = 10, ScannedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow, Title = "Track" };
            await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", new RemoteInventoryBatchRequest { Items = [item] });
            var snapshot = Assert.Single((await admin.GetFromJsonAsync<RemoteInventoryPage>("/api/v1/remote-inventory"))!.Items);
            await device.PostAsJsonAsync("/api/v1/remote-inventory/batches", new RemoteInventoryBatchRequest {
                Items = [item with { ScannedUtc = item.ScannedUtc.AddSeconds(1), Title = "Changed" }] });
            var request = new CreateRemotePseudoRequest(Guid.NewGuid().ToString("N"), "My edits", "NO CAT",
                [new(new("owner", "mac", 1), snapshot.Version, null, null, "Track", null, null, null)]);
            await ProblemAsync(await admin.PostAsJsonAsync("/api/v1/remote-inventory/pseudo-releases", request), 412, "remote_inventory_changed");
            Assert.Equal(0, await ImportCount("release"));
            Assert.Equal(0, await ImportCount("media_file"));
        }
        finally { File.Delete(db); }
    }
}
