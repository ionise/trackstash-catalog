using Microsoft.Data.Sqlite;
using TrackStash.Core.Sqlite;

namespace TrackStash.Catalog.Tests;

public sealed class ImportCsvIntegrationTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"catalog-import-{Guid.NewGuid():N}.db");

    private static string TempCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog-import-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var p in paths)
            if (File.Exists(p)) File.Delete(p);
    }

    private static CatalogCommands CreateCatalogCommands()
        => new(new SqliteStorageProviderFactory(), "sqlite");

    [Fact]
    public async Task ImportCsvAsync_ImportsFullHierarchy_InDependencyOrder()
    {
        var dbPath = TempDb();
        var csvPath = TempCsv("""
            type,name,title,sort_name,isrc,mix_name,label_ref,artist_ref,artist_role,release_ref,disc_number,track_number,source,external_id,id
            label,Virelith Records,,,,,,,,,,,,
            artist,Bozra Bozra,,Bozra Bozra,,,,,,,,,
            release,,Virelith Sessions,,,,Virelith Records,Bozra Bozra,,,,,,,
            recording,,Signal Drift,,TST000000001,Original Mix,,"Bozra Bozra",primary,"Virelith Sessions",1,1,,,
            """);

        try
        {
            // Bootstrap the database first, then import via catalog
            var provider = new TrackStash.Core.Sqlite.SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var catalog = CreateCatalogCommands();
            var result = await catalog.ImportCsvAsync(new ImportCsvRequest(DatabasePath: dbPath, CsvPath: csvPath));

            Assert.Equal(4, result.TotalRows);
            Assert.Equal(4, result.SucceededRows);
            Assert.Equal(0, result.FailedRows);
            Assert.Equal(0, result.WarningCount);
            Assert.All(result.RowResults, r => Assert.True(r.Success));

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            Assert.Equal(1, await CountAsync(conn, "SELECT COUNT(*) FROM label"));
            Assert.Equal(1, await CountAsync(conn, "SELECT COUNT(*) FROM artist"));
            Assert.Equal(1, await CountAsync(conn, "SELECT COUNT(*) FROM release"));
            Assert.Equal(1, await CountAsync(conn, "SELECT COUNT(*) FROM recording"));
        }
        finally { Cleanup(dbPath, csvPath); }
    }

    [Fact]
    public async Task ImportCsvAsync_DryRun_DoesNotPersistRows()
    {
        var dbPath = TempDb();
        var csvPath = TempCsv("""
            type,name,title,sort_name,isrc,mix_name,label_ref,artist_ref,artist_role,release_ref,disc_number,track_number,source,external_id,id
            label,Dry Run Label,,,,,,,,,,,,
            """);

        try
        {
            var provider = new TrackStash.Core.Sqlite.SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var catalog = CreateCatalogCommands();
            var result = await catalog.ImportCsvAsync(new ImportCsvRequest(DatabasePath: dbPath, CsvPath: csvPath, DryRun: true));

            Assert.Equal(1, result.TotalRows);
            Assert.True(result.DryRun);

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            Assert.Equal(0, await CountAsync(conn, "SELECT COUNT(*) FROM label"));
        }
        finally { Cleanup(dbPath, csvPath); }
    }

    private static async Task<long> CountAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
