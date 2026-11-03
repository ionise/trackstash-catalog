using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Application;
using TrackStash.Core.Identifiers;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed class LabelServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"label-service-{Guid.NewGuid():N}.db");
    private SqliteStorageProvider Provider => new(_path);
    private LabelService Service => new(Provider);

    public async Task InitializeAsync() => await Provider.Migrations.ApplyPendingMigrationsAsync();

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateReadQueryUpdate_PersistsIdentityAndDoesNotMergeSameNames()
    {
        var created = await Service.CreateAsync("  New Label  ", "Sort");
        Assert.Equal(LabelWriteStatus.Created, created.Status);
        var label = Assert.IsType<LabelDetail>(created.Label);
        Assert.True(EntityId.IsValid(label.Id));
        Assert.Equal("New Label", label.Name);
        Assert.NotEmpty(label.Version);
        Assert.Equal(label, await Service.GetAsync(label.Id));
        Assert.Empty(label.Aliases);
        Assert.Empty(label.ExternalReferences);
        var second = await Service.CreateAsync("New Label");
        Assert.NotEqual(label.Id, second.Label!.Id);
        var page = await Service.QueryAsync(new LabelQuery { NormalizedName = "newlabel" });
        Assert.Equal(2, page.Total);
        var updated = await Service.UpdateAsync(label.Id, label.Version,
            new LabelUpdate { SetSortName = true });
        Assert.Equal(LabelWriteStatus.Updated, updated.Status);
        Assert.Null(updated.Label!.SortName);
        Assert.Equal(label.Id, updated.Label.Id);
        Assert.NotEqual(label.Version, updated.Label.Version);
        Assert.Equal(LabelWriteStatus.Stale, (await Service.UpdateAsync(label.Id, label.Version,
            new LabelUpdate { SetName = true, Name = "Stale edit" })).Status);
        Assert.Equal(LabelWriteStatus.NotFound, (await Service.UpdateAsync("missing", "token",
            new LabelUpdate { SetName = true, Name = "Missing" })).Status);
        Assert.Null(await Service.GetAsync("missing"));
        Assert.Equal(2, (await Service.QueryAsync(new LabelQuery())).Total);
    }

    [Fact]
    public async Task ConcurrentWriters_ExactlyOneCanUseTheOriginalVersion()
    {
        var original = (await Service.CreateAsync("Original")).Label!;
        using var start = new ManualResetEventSlim();
        var tasks = new[] { "First editor", "Second editor" }.Select(name => Task.Run(async () =>
        {
            start.Wait();
            return await Service.UpdateAsync(original.Id, original.Version,
                new LabelUpdate { SetName = true, Name = name });
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results, r => r.Status == LabelWriteStatus.Updated);
        Assert.Single(results, r => r.Status == LabelWriteStatus.Stale);
        var stored = (await Service.GetAsync(original.Id))!;
        Assert.Equal(winner.Label!.Name, stored.Name);
        Assert.Equal(winner.Label.Version, stored.Version);
    }

    [Fact]
    public async Task LegacyWriter_InvalidatesServiceVersionAndKeepsProviderPayloadsPrivate()
    {
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            await unit.Labels.UpsertAsync(new Label
            {
                Id = "legacy-label", Name = "Legacy", SourcePayloadJson = """{"private":"label"}""",
                Aliases = [new EntityAlias { Value = "Alias" }],
                ExternalReferences = [new EntityReference { Source = "beatport", ExternalId = "1", PayloadJson = """{"private":"ref"}""" }],
            });
            await unit.CommitAsync();
        }
        var original = (await Service.GetAsync("legacy-label"))!;
        Assert.Single(original.Aliases);
        Assert.Single(original.ExternalReferences);
        var serialized = System.Text.Json.JsonSerializer.Serialize(original);
        Assert.DoesNotContain("private", serialized);
        await using (var unit = await Provider.BeginUnitOfWorkAsync())
        {
            var label = (await unit.Labels.GetByIdAsync(original.Id))!;
            await unit.Labels.UpsertAsync(label with { Name = "Local edit" });
            await unit.CommitAsync();
        }
        Assert.Equal(LabelWriteStatus.Stale, (await Service.UpdateAsync(original.Id, original.Version,
            new LabelUpdate { SetName = true, Name = "Overwritten" })).Status);
        Assert.Equal("Local edit", (await Service.GetAsync(original.Id))!.Name);
    }

    [Fact]
    public async Task InvalidInputFailsWithoutCreatingOrModifyingLabels()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service.CreateAsync(" ").AsTask());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service.QueryAsync(new LabelQuery { PageSize = 101 }).AsTask());
        var label = (await Service.CreateAsync("Original")).Label!;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service.UpdateAsync(label.Id, label.Version,
            new LabelUpdate { SetName = true, Name = null }).AsTask());
        Assert.Equal(label.Version, (await Service.GetAsync(label.Id))!.Version);
        Assert.Equal(1, (await Service.QueryAsync(new LabelQuery())).Total);
    }
}
