using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Entities;
using TrackStash.Core.Services;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Tests;

public sealed class LabelRetirementImportTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"retired-import-{Guid.NewGuid():N}.db");
    private SqliteStorageProvider Provider => new(_path);

    public async Task InitializeAsync()
    {
        await Provider.Migrations.ApplyPendingMigrationsAsync();
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        await unit.Labels.UpsertAsync(new Label { Id = "survivor", Name = "Kept" });
        await unit.Labels.UpsertAsync(new Label
        {
            Id = "retired", Name = "Other",
            ExternalReferences = [new EntityReference { Source = "beatport", ExternalId = "254639" }],
        });
        var preview = (await unit.LabelMerges.PreviewAsync("survivor", "retired", DateTimeOffset.UtcNow)).Preview!;
        await unit.LabelMerges.ApplyAsync(preview.OperationId, preview.Version, "Same identity", null, "test", DateTimeOffset.UtcNow);
        await unit.CommitAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Yaml_RejectsSpecOrMetadataRetiredIdIncludingDryRun(bool dryRun, bool metadataOnly)
    {
        var yaml = """
            apiVersion: catalog.trackstash/v1
            kind: Label
            mode: replace
            metadata:
              id: retired
            spec:
              name: Recreated
            """ + (metadataOnly ? "" : "\n  id: retired\n");
        var result = await EntityYamlService.ApplyAsync(EntityYamlService.ParseSingleDocument(yaml), Provider, dryRun);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("retired") && e.Contains("survivor"));
        await using var unit = await Provider.BeginUnitOfWorkAsync();
        Assert.Null(await unit.Labels.GetByIdAsync("retired"));
    }

    [Fact]
    public async Task Yaml_TransferredProviderIdFindsSurvivorAndRetiredReadIsExplicit()
    {
        var artifact = EntityYamlService.ParseSingleDocument("""
            apiVersion: catalog.trackstash/v1
            kind: Label
            mode: merge
            spec:
              name: Kept
              externalRefs:
                - source: beatport
                  externalId: '254639'
            """);
        var result = await EntityYamlService.ApplyAsync(artifact, Provider, dryRun: false);
        Assert.True(result.Success);
        Assert.Equal("survivor", result.EntityId);
        var retired = await Assert.ThrowsAsync<RetiredLabelException>(() => EntityYamlService.GetEntityAsync("label", "retired", Provider));
        Assert.Equal("survivor", retired.CanonicalId);
    }

    [Theory]
    [InlineData(false, "label,Recreated,retired,,")]
    [InlineData(true, "label,Recreated,retired,,")]
    [InlineData(false, "release,,,Release,retired")]
    [InlineData(true, "release,,,Release,retired")]
    public async Task Csv_RejectsExplicitRetiredIdentityOrReleaseLink(bool dryRun, string row)
    {
        var csv = Path.Combine(Path.GetTempPath(), $"retired-import-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(csv, "type,name,id,title,label_ref\n" + row + "\n");
            var result = await new CatalogImportService(Provider).ImportAsync(new CatalogImportRequest(csv, DryRun: dryRun));
            Assert.Equal(1, result.FailedRows);
            Assert.Contains("retired", Assert.Single(result.RowResults).Error);
        }
        finally { File.Delete(csv); }
    }

    [Fact]
    public async Task Csv_TransferredProviderIdentityReusesSurvivor()
    {
        var csv = Path.Combine(Path.GetTempPath(), $"retired-import-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(csv, "type,name,source,external_id\nlabel,Kept,beatport,254639\n");
            var result = await new CatalogImportService(Provider).ImportAsync(new CatalogImportRequest(csv));
            Assert.Equal(1, result.SucceededRows);
            Assert.Equal("survivor", Assert.Single(result.RowResults).EntityId);
        }
        finally { File.Delete(csv); }
    }
}
