using TrackStash.Core.Sqlite;

namespace TrackStash.Catalog.Tests;

public sealed class SummaryIntegrationTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"catalog-summary-{Guid.NewGuid():N}.db");

    private static CatalogCommands CreateCatalogCommands()
        => new(new SqliteStorageProviderFactory(), "sqlite");

    [Fact]
    public async Task SummaryAsync_EmptyDatabase_ReturnsZeroCounts()
    {
        var dbPath = TempDb();
        try
        {
            var provider = new TrackStash.Core.Sqlite.SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var catalog = CreateCatalogCommands();
            var result = await catalog.SummaryAsync(dbPath);

            Assert.Equal(0, result.LabelCount);
            Assert.Equal(0, result.ArtistCount);
            Assert.Equal(0, result.ReleaseCount);
            Assert.Equal(0, result.RecordingCount);
            Assert.Equal(0, result.MediaFileCount);
            Assert.Equal(1, result.CurrentMigrationVersion);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    [Fact]
    public async Task SummaryAsync_AfterImport_ReflectsCorrectCounts()
    {
        var dbPath = TempDb();
        var csvPath = Path.Combine(Path.GetTempPath(), $"catalog-summary-{Guid.NewGuid():N}.csv");
        File.WriteAllText(csvPath, """
            type,name,title,sort_name,isrc,mix_name,label_ref,artist_ref,artist_role,release_ref,disc_number,track_number,source,external_id,id
            label,Test Label,,,,,,,,,,,,
            artist,Test Artist,,Test Artist,,,,,,,,,
            release,,Test Release,,,,Test Label,Test Artist,,,,,,,
            recording,,Test Track,,TST000000001,Original Mix,,"Test Artist",primary,"Test Release",1,1,,,
            """);

        try
        {
            var provider = new TrackStash.Core.Sqlite.SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var catalog = CreateCatalogCommands();
            await catalog.ImportCsvAsync(new ImportCsvRequest(DatabasePath: dbPath, CsvPath: csvPath));

            var result = await catalog.SummaryAsync(dbPath);

            Assert.Equal(1, result.LabelCount);
            Assert.Equal(1, result.ArtistCount);
            Assert.Equal(1, result.ReleaseCount);
            Assert.Equal(1, result.RecordingCount);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(csvPath)) File.Delete(csvPath);
        }
    }
}
