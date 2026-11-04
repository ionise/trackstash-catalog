using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrackStash.Catalog.Api;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private async Task<(LabelMergePreviewResponse Preview, string ETag)> MergePreviewAsync(string survivor, string retired)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/labels/{survivor}/merge-previews", new { retiredLabelId = retired });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<LabelMergePreviewResponse>())!;
        Assert.Equal($"/api/v1/label-merge-previews/{preview.OperationId}", response.Headers.Location!.OriginalString);
        Assert.NotNull(response.Headers.ETag);
        return (preview, response.Headers.ETag.ToString());
    }

    private async Task<HttpResponseMessage> MergeApplyAsync(string operationId, string? etag, string reason = "Confirmed same identity")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/label-merge-previews/{operationId}/apply")
        {
            Content = JsonContent.Create(new { reason }),
        };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task MergeApi_PreviewApplyRedirectAndRetryPreserveReferencesWithoutPayloads()
    {
        var survivor = await CreateAsync("Univack");
        var retired = await CreateAsync("Univack duplicate");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.Labels.UpsertAsync(new Label
            {
                Id = survivor.Label.Id, Name = "Univack", SourcePayloadJson = "private-survivor-payload",
                ExternalReferences = [new EntityReference { Source = "beatport", ExternalId = "10955", PayloadJson = "private-ref-payload", IsPrimary = true }],
            });
            await unit.Labels.UpsertAsync(new Label
            {
                Id = retired.Label.Id, Name = "Univack duplicate", SourcePayloadJson = "private-retired-payload",
                ExternalReferences = [new EntityReference { Source = "beatport", ExternalId = "254639", IsPrimary = true }],
            });
            await unit.CommitAsync();
        }
        var (preview, etag) = await MergePreviewAsync(survivor.Label.Id, retired.Label.Id);
        Assert.Empty(preview.Blockers);
        Assert.Equal(2, preview.ExternalReferences.Count);
        var saved = await _client.GetAsync($"/api/v1/label-merge-previews/{preview.OperationId}");
        Assert.Equal(etag, saved.Headers.ETag!.ToString());
        Assert.DoesNotContain("private-", await saved.Content.ReadAsStringAsync());
        var applied = await MergeApplyAsync(preview.OperationId, etag);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var outcome = (await applied.Content.ReadFromJsonAsync<LabelMergeOutcomeResponse>())!;
        Assert.Equal(survivor.Label.Id, outcome.CanonicalId);
        Assert.Equal(outcome, await (await MergeApplyAsync(preview.OperationId, etag)).Content.ReadFromJsonAsync<LabelMergeOutcomeResponse>());
        await ProblemAsync(await MergeApplyAsync(preview.OperationId, etag, "Changed reason"), 409, "merge_replay_conflict");
        foreach (var suffix in new[] { "", "/aliases", "/external-references" })
        {
            var redirect = await _client.GetAsync($"/api/v1/labels/{retired.Label.Id}{suffix}");
            Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
            Assert.Equal($"/api/v1/labels/{survivor.Label.Id}{suffix}", redirect.Headers.Location!.OriginalString);
        }
        var problem = await ProblemAsync(await PatchAsync(retired.Label.Id, """{"name":"Must not resurrect"}""", retired.ETag), 409, "label_retired");
        Assert.Equal(survivor.Label.Id, problem.GetProperty("canonicalId").GetString());
        var survivorResponse = await _client.GetFromJsonAsync<LabelResponse>($"/api/v1/labels/{survivor.Label.Id}");
        Assert.Equal(2, survivorResponse!.ExternalReferences.Count);
        Assert.Contains(survivorResponse.Aliases, a => a.Value == "Univack duplicate");
    }

    [Fact]
    public async Task MergeApi_RejectsMissingStaleChangedAndBlockedReviews()
    {
        var survivor = await CreateAsync();
        var retired = await CreateAsync();
        var (preview, etag) = await MergePreviewAsync(survivor.Label.Id, retired.Label.Id);
        await ProblemAsync(await MergeApplyAsync(preview.OperationId, null), 428, "precondition_required");
        await ProblemAsync(await MergeApplyAsync(preview.OperationId, "\"b3RoZXI=\""), 412, "merge_state_changed");
        await PatchAsync(retired.Label.Id, """{"name":"Changed"}""", retired.ETag);
        await ProblemAsync(await MergeApplyAsync(preview.OperationId, etag), 412, "merge_state_changed");
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            foreach (var id in new[] { survivor.Label.Id, retired.Label.Id })
                await unit.Releases.UpsertAsync(new Release
                {
                    Id = "conflicting-release", LabelLinks = [new ReleaseLabelLink { LabelId = id, Role = id }],
                });
            await unit.CommitAsync();
        }
        var blocked = await MergePreviewAsync(survivor.Label.Id, retired.Label.Id);
        Assert.NotEmpty(blocked.Preview.Blockers);
        await ProblemAsync(await MergeApplyAsync(blocked.Preview.OperationId, blocked.ETag), 409, "merge_blocked");
        await ProblemAsync(await MergeApplyAsync("missing", etag), 404, "merge_not_found");
        await ProblemAsync(await _client.GetAsync("/api/v1/label-merge-previews/missing"), 404, "merge_not_found");
    }

    [Theory]
    [InlineData("""{"retiredLabelId":null}""")]
    [InlineData("""{"retiredLabelId":"x","extra":true}""")]
    [InlineData("""{"RetiredLabelId":"x"}""")]
    [InlineData("""{"retiredLabelId":"x","retiredLabelId":"y"}""")]
    public async Task MergeApi_UsesStrictRequestValidation(string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        await ProblemAsync(await _client.PostAsync("/api/v1/labels/survivor/merge-previews", body), 400, "validation_failed");
    }

    [Fact]
    public async Task MergeApi_RequiresBearerAndDocumentsContracts()
    {
        using var client = _app.GetTestClient();
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/labels/one/merge-previews", new { retiredLabelId = "two" }), 401, "unauthorized");
        await ProblemAsync(await client.GetAsync("/api/v1/label-merge-previews/one"), 401, "unauthorized");
        await ProblemAsync(await client.PostAsJsonAsync("/api/v1/label-merge-previews/one/apply", new { reason = "same" }), 401, "unauthorized");
        var schema = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var apply = schema.GetProperty("paths").GetProperty("/api/v1/label-merge-previews/{operationId}/apply").GetProperty("post");
        Assert.Contains(apply.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "If-Match");
        Assert.True(apply.GetProperty("responses").TryGetProperty("412", out _));
        Assert.True(apply.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
    }

    private sealed class MergeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task MergeApi_ExpiresUnappliedReviews()
    {
        var survivor = await CreateAsync();
        var retired = await CreateAsync();
        var clock = new MergeClock();
        await using var app = Build(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var response = await client.PostAsJsonAsync($"/api/v1/labels/{survivor.Label.Id}/merge-previews", new { retiredLabelId = retired.Label.Id });
        var preview = (await response.Content.ReadFromJsonAsync<LabelMergePreviewResponse>())!;
        clock.Now = preview.ExpiresUtc;
        using var apply = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/label-merge-previews/{preview.OperationId}/apply") { Content = JsonContent.Create(new { reason = "Same" }) };
        apply.Headers.TryAddWithoutValidation("If-Match", response.Headers.ETag!.ToString());
        await ProblemAsync(await client.SendAsync(apply), 410, "merge_preview_expired");
        await ProblemAsync(await client.GetAsync($"/api/v1/label-merge-previews/{preview.OperationId}"), 410, "merge_preview_expired");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task StartupRejectsOldSchemaWithoutChangingIt(int version)
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog-v3-readiness-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_migration(version INTEGER PRIMARY KEY, name TEXT, applied_utc TEXT);
                    INSERT INTO schema_migration VALUES (@version, 'old-schema', '2026-11-03');
                    CREATE TABLE label(label_id TEXT PRIMARY KEY, name TEXT);
                    INSERT INTO label VALUES ('keep-id','Keep name');
                    """;
                command.Parameters.AddWithValue("@version", version);
                await command.ExecuteNonQueryAsync();
            }
            await using var app = Build(builder => builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CatalogApi:DatabasePath"] = path }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
            await using var check = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            await check.OpenAsync();
            using var query = check.CreateCommand();
            query.CommandText = "SELECT MAX(version) FROM schema_migration";
            Assert.Equal((long)version, await query.ExecuteScalarAsync());
            query.CommandText = "SELECT name FROM label WHERE label_id='keep-id'";
            Assert.Equal("Keep name", await query.ExecuteScalarAsync());
            query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table'";
            Assert.Equal(2L, await query.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
