using TrackStash.Core.Normalization;
using TrackStash.Core.Sqlite;

namespace TrackStash.Catalog.Tests;

public sealed class DeleteEntityIntegrationTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"catalog-delete-{Guid.NewGuid():N}.db");

    private static CatalogCommands CreateCatalogCommands()
        => new(new SqliteStorageProviderFactory(), "sqlite");

    private static async Task InitWithLabelAsync(string dbPath, string labelName = "Delete Me Label")
    {
        var csvPath = Path.Combine(Path.GetTempPath(), $"catalog-delete-seed-{Guid.NewGuid():N}.csv");
        File.WriteAllText(csvPath, $"""
            type,name,title,sort_name,isrc,mix_name,label_ref,artist_ref,artist_role,release_ref,disc_number,track_number,source,external_id,id
            label,{labelName},,,,,,,,,,,,
            """);

        try
        {
            var provider = new SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var catalog = CreateCatalogCommands();
            await catalog.ImportCsvAsync(new ImportCsvRequest(DatabasePath: dbPath, CsvPath: csvPath));
        }
        finally { if (File.Exists(csvPath)) File.Delete(csvPath); }
    }

    [Fact]
    public async Task DeleteEntityAsync_UnblockedLabel_SucceedsAndCapturesTombstone()
    {
        var dbPath = TempDb();
        try
        {
            await InitWithLabelAsync(dbPath);

            // Find the label ID we just created
            var provider = new SqliteStorageProvider(dbPath);
            await using var uow = await provider.BeginUnitOfWorkAsync();
            var label = await uow.Labels.GetByNormalizedNameAsync(EntityNameNormalizer.NormalizeStrict("Delete Me Label"));
            Assert.NotNull(label);
            await uow.RollbackAsync();

            var catalog = CreateCatalogCommands();
            var result = await catalog.DeleteEntityAsync(new DeleteEntityRequest(
                DatabasePath: dbPath,
                EntityType: "label",
                EntityId: label!.Id,
                DeletedBy: "test",
                DeleteReason: "integration-test"));

            Assert.True(result.Success);
            Assert.True(result.TombstoneCaptured);
            Assert.Empty(result.Blockers);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    [Fact]
    public async Task DeleteEntityAsync_LabelReferencedByRelease_IsBlocked()
    {
        var dbPath = TempDb();
        var csvPath = Path.Combine(Path.GetTempPath(), $"catalog-delete-block-{Guid.NewGuid():N}.csv");
        File.WriteAllText(csvPath, """
            type,name,title,sort_name,isrc,mix_name,label_ref,artist_ref,artist_role,release_ref,disc_number,track_number,source,external_id,id
            label,Blocked Label,,,,,,,,,,,,
            artist,Some Artist,,,,,,,,,,,,
            release,,Has Label Release,,,,Blocked Label,Some Artist,,,,,,,
            """);

        try
        {
            var provider = new SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var catalog = CreateCatalogCommands();
            await catalog.ImportCsvAsync(new ImportCsvRequest(DatabasePath: dbPath, CsvPath: csvPath));

            await using var uow = await provider.BeginUnitOfWorkAsync();
            var label = await uow.Labels.GetByNormalizedNameAsync(EntityNameNormalizer.NormalizeStrict("Blocked Label"));
            Assert.NotNull(label);
            await uow.RollbackAsync();

            var result = await catalog.DeleteEntityAsync(new DeleteEntityRequest(
                DatabasePath: dbPath,
                EntityType: "label",
                EntityId: label!.Id));

            Assert.False(result.Success);
            Assert.NotEmpty(result.Blockers);
            Assert.Contains(result.Blockers, b => b.TableName == "release_label_link");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(csvPath)) File.Delete(csvPath);
        }
    }

    [Fact]
    public async Task DeleteEntityAsync_InvalidEntityType_Throws()
    {
        var dbPath = TempDb();
        try
        {
            var provider = new SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var catalog = CreateCatalogCommands();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                catalog.DeleteEntityAsync(new DeleteEntityRequest(
                    DatabasePath: dbPath,
                    EntityType: "mediafile",
                    EntityId: "some-id")));
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }
}
