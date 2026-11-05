using Microsoft.Data.Sqlite;

namespace TrackStash.Catalog.Tests;

internal static class ScannerInventoryFixture
{
    public static async Task CreateAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE media_file(media_file_id INTEGER PRIMARY KEY,content_hash TEXT,path TEXT,format TEXT,size_bytes INTEGER,scanned_utc TEXT);
            CREATE TABLE metadata(metadata_id INTEGER PRIMARY KEY,media_file_id INTEGER REFERENCES media_file(media_file_id),artist TEXT,title TEXT,album TEXT);
            CREATE TABLE provider_cache(id TEXT PRIMARY KEY,payload BLOB);
            INSERT INTO media_file VALUES(1,'hash','/JetDrive/Library/track.flac','flac',123,'2026-11-05');
            INSERT INTO metadata VALUES(1,1,'Artist','Title','Album');
            INSERT INTO provider_cache VALUES('one',x'0001FF');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
