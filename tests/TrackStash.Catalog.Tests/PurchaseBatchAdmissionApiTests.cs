using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TrackStash.Catalog.Application;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> BatchApi(bool distinctIsrcs = true)
    {
        await ImportService();
        if (distinctIsrcs) await SourceSql("UPDATE beatport_isrc_lookup SET tracks_json=json_set(tracks_json, '$[1].isrc', 'TEST002')");
        var app = Build(b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogApi:PurchaseSourceDatabasePath"] = PurchaseSourcePath }));
        await app.StartAsync();
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        return (app, client);
    }

    [Fact]
    public async Task BatchAdmission_DryRunWritesNothingThenAdmitsClearCutReleaseOnceWithCompoundUnresolved()
    {
        var (app, client) = await BatchApi();
        await using var _ = app;
        var ready = await (await client.GetAsync("/api/v1/purchase-release-admissions/ready")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([10L], ready.GetProperty("releaseIds").EnumerateArray().Select(v => v.GetInt64()));
        var previews = await ImportCount("release_import_preview");
        var dry = await (await client.PostAsJsonAsync("/api/v1/purchase-release-admissions", new { releaseIds = new[] { 10 }, dryRun = true })).Content.ReadFromJsonAsync<JsonElement>();
        var item = dry.GetProperty("items")[0];
        Assert.Equal("would_admit", item.GetProperty("status").GetString());
        Assert.Equal(2, item.GetProperty("tracks").GetInt32());
        Assert.Equal(2, item.GetProperty("newArtists").GetInt32()); Assert.Equal(1, item.GetProperty("unresolvedArtists").GetInt32());
        Assert.Equal(previews, await ImportCount("release_import_preview")); Assert.Equal(0, await ImportCount("release"));

        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/purchase-release-admissions", new { releaseIds = new[] { 10 } }), 400, "confirmation_required");
        var real = await client.PostAsJsonAsync("/api/v1/purchase-release-admissions", new { releaseIds = new[] { 10 }, confirmed = true });
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        item = (await real.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("admitted", item.GetProperty("status").GetString());
        Assert.Equal(1, await ImportCount("release")); Assert.Equal(2, await ImportCount("recording"));
        Assert.Equal(1, await ImportCount("artist WHERE identity_status='unresolved_compound'"));
        Assert.Equal(1, await ImportCount($"release_import_outcome"));
        Assert.Equal(1, await ImportCount($"release_import_preview WHERE plan_json LIKE '%{PurchaseBatchAdmissionService.NewArtistEvidence[..30]}%'"));

        Assert.Empty((await (await client.GetAsync("/api/v1/purchase-release-admissions/ready")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseIds").EnumerateArray());
        item = (await (await client.PostAsJsonAsync("/api/v1/purchase-release-admissions", new { releaseIds = new[] { 10 }, confirmed = true }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("needs_review", item.GetProperty("status").GetString());
        Assert.Contains("Already admitted", item.GetProperty("reasons")[0].GetString());
        Assert.Equal(1, await ImportCount("release"));
    }

    [Fact]
    public async Task BatchAdmission_LeavesJudgementCallsForReviewAndReusesSameIsrcRecording()
    {
        var (app, client) = await BatchApi();
        await using var _ = app;
        var same = Guid.NewGuid().ToString("N");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.CatalogFoundations!.SaveEntityAsync(new Recording { Id = same, Title = "Track", Name = "Track", NormalizedName = EntityNameNormalizer.NormalizeStrict("Track"),
                MixName = "Original Mix", Isrc = "TEST001" }, null);
            await unit.CatalogFoundations.SaveEntityAsync(new Artist { Id = Guid.NewGuid().ToString("N"), Name = "Amega", NormalizedName = EntityNameNormalizer.NormalizeStrict("Amega") }, null);
            await unit.CommitAsync();
        }
        var item = (await (await client.PostAsJsonAsync("/api/v1/purchase-release-admissions", new { releaseIds = new[] { 10 }, dryRun = true }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("needs_review", item.GetProperty("status").GetString());
        Assert.Contains(item.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.Contains("'Amega'"));

        await ImportSql("DELETE FROM artist WHERE name='Amega'");
        item = (await (await client.PostAsJsonAsync("/api/v1/purchase-release-admissions", new { releaseIds = new[] { 10 }, confirmed = true }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("admitted", item.GetProperty("status").GetString());
        Assert.Equal(1, item.GetProperty("reusedRecordings").GetInt32()); Assert.Equal(1, item.GetProperty("newRecordings").GetInt32());
        Assert.Equal(2, await ImportCount("recording"));
        Assert.Equal(1, await ImportCount($"release_recording WHERE recording_id='{same}'"));
    }

    [Fact]
    public async Task BatchAdmission_HoldsBackTracksSharingAnIsrcOnTheSameRelease()
    {
        var (app, client) = await BatchApi(distinctIsrcs: false);
        await using var _ = app;
        var item = (await (await client.PostAsJsonAsync("/api/v1/purchase-release-admissions", new { releaseIds = new[] { 10 }, confirmed = true }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("needs_review", item.GetProperty("status").GetString());
        Assert.All(item.GetProperty("reasons").EnumerateArray(), r => Assert.Contains("shares an ISRC", r.GetString()));
        Assert.Equal(0, await ImportCount("release")); Assert.Equal(0, await ImportCount("artist"));
    }

    [Fact]
    public async Task BatchAdmission_RejectsInvalidRequestsAndReportsMissingReleasesAsFailed()
    {
        var (app, client) = await BatchApi();
        await using var _ = app;
        const string path = "/api/v1/purchase-release-admissions";
        await ProblemAsync(await client.PostAsJsonAsync(path, new { releaseIds = Array.Empty<long>(), dryRun = true }), 400, "validation_failed");
        await ProblemAsync(await client.PostAsJsonAsync(path, new { releaseIds = Enumerable.Range(1, 26).ToArray(), dryRun = true }), 400, "validation_failed");
        await ProblemAsync(await client.PostAsJsonAsync(path, new { releaseIds = new[] { 10, 10 }, dryRun = true }), 400, "validation_failed");
        await ProblemAsync(await client.PostAsJsonAsync(path, new { releaseIds = new[] { "10" }, dryRun = true }), 400, "validation_failed");
        await ProblemAsync(await client.PostAsJsonAsync(path, new { releaseIds = new[] { 10 }, dryRun = "yes" }), 400, "validation_failed");
        await ProblemAsync(await client.PostAsJsonAsync(path, new { releaseIds = new[] { 10 }, dryRun = true, extra = 1 }), 400, "validation_failed");
        await ProblemAsync(await client.GetAsync($"{path}/ready?page=1"), 400, "validation_failed");
        var item = (await (await client.PostAsJsonAsync(path, new { releaseIds = new[] { 424242 }, dryRun = true })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("failed", item.GetProperty("status").GetString());
        client.DefaultRequestHeaders.Authorization = null;
        await ProblemAsync(await client.PostAsJsonAsync(path, new { releaseIds = new[] { 10 }, dryRun = true }), 401, "unauthorized");
    }
}
