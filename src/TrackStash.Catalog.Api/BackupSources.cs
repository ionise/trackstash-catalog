using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>A server-defined backup scope; source paths are never supplied by the browser.</summary>
public sealed record BackupSourceResponse(string Kind, string Name, bool Configured);

public sealed class BackupSources
{
    private readonly ICatalogBackupService _catalog;
    private readonly ICatalogBackupService? _scanner;
    public BackupSources(ICatalogBackupService catalog, IConfiguration configuration)
    {
        _catalog = catalog;
        var path = configuration["CatalogApi:ScannerDatabasePath"] ?? Environment.GetEnvironmentVariable("TRACKSTASH_SCAN_DATABASE_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) == Path.GetFullPath(configuration["CatalogApi:DatabasePath"]!))
            throw new InvalidOperationException("ScannerDatabasePath must be an absolute scanner inventory path, separate from the canonical catalog.");
        _scanner = new CatalogBackupService(new SqliteScannerSnapshotSource(path), null, configuration["CatalogApi:BackupRoot"]);
    }

    public IReadOnlyList<BackupSourceResponse> List() =>
        [new("catalog", "Catalog + artwork", true), new("scanner", "Scanner inventory", _scanner is not null)];

    public ICatalogBackupService Get(string kind) => kind switch
    {
        "catalog" => _catalog,
        "scanner" => _scanner ?? throw new ApiRequestException(409, "scanner_not_configured",
            "Scanner inventory backup is not configured. Set CatalogApi:ScannerDatabasePath on the server and restart the API."),
        _ => throw new ApiRequestException(400, "invalid_backup_kind", "Backup kind must be catalog or scanner."),
    };
}
