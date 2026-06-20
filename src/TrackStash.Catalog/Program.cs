using TrackStash.Catalog;
using TrackStash.Catalog.Config;
using TrackStash.Catalog.Output;
using TrackStash.Core.Sqlite;

var exitCode = await RunAsync(args).ConfigureAwait(false);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 2;
    }

    var command = args[0].ToLowerInvariant();
    var options = ParseOptions(args, 1);
    var config = ConfigResolver.Resolve(options);
    var providerFactory = ResolveProviderFactory(config.Provider);
    var catalog = new CatalogCommands(providerFactory, config.Provider);
    var jsonMode = CommandOutput.IsJsonMode(config.Output.Format);

    try
    {
        return command switch
        {
            "import-csv"    => await RunImportCsvAsync(catalog, config, options, jsonMode).ConfigureAwait(false),
            "summary"       => await RunSummaryAsync(catalog, config, jsonMode).ConfigureAwait(false),
            "delete-entity" => await RunDeleteEntityAsync(catalog, config, options, jsonMode).ConfigureAwait(false),
            "doctor"        => await RunDoctorAsync(catalog, config, jsonMode).ConfigureAwait(false),
            "repair-indexes" => await RunRepairIndexesAsync(catalog, config, options, jsonMode).ConfigureAwait(false),
            _               => UnknownCommand(command),
        };
    }
    catch (ArgumentException ex)
    {
        if (jsonMode)
            CommandOutput.WriteJson(command, ok: false, exitCode: 2, data: null, errors: [ex.Message]);
        else
            Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex)
    {
        if (jsonMode)
            CommandOutput.WriteJson(command, ok: false, exitCode: 1, data: null, errors: [ex.Message]);
        else
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunImportCsvAsync(
    CatalogCommands catalog,
    CatalogConfig config,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
    var dbPath  = RequireDbPath(config);
    var csvPath = GetRequiredOption(options, "file");
    var dryRun  = options.ContainsKey("dry-run");
    var failFast = options.ContainsKey("fail-fast");

    var result = await catalog.ImportCsvAsync(
        new ImportCsvRequest(DatabasePath: dbPath, CsvPath: csvPath, DryRun: dryRun, FailFast: failFast))
        .ConfigureAwait(false);

    if (jsonMode)
    {
        CommandOutput.WriteJson("import-csv", ok: result.FailedRows == 0, exitCode: result.FailedRows > 0 ? 1 : 0, data: new
        {
            databasePath = dbPath,
            csvPath = result.CsvPath,
            totalRows = result.TotalRows,
            succeededRows = result.SucceededRows,
            failedRows = result.FailedRows,
            dryRun = result.DryRun,
            failFast = result.FailFast,
            warningCount = result.WarningCount,
            rowResults = result.RowResults.Select(r => new
            {
                rowNumber = r.RowNumber,
                entityType = r.EntityType,
                entityId = r.EntityId,
                action = r.Action,
                success = r.Success,
                error = r.Error,
                warnings = r.Warnings,
            }),
        });
    }
    else
    {
        CommandOutput.WriteText([
            ("database", dbPath),
            ("csv", result.CsvPath),
            ("totalRows", result.TotalRows),
            ("succeededRows", result.SucceededRows),
            ("failedRows", result.FailedRows),
            ("dryRun", result.DryRun),
            ("failFast", result.FailFast),
            ("warningCount", result.WarningCount),
        ]);
        foreach (var row in result.RowResults.Where(r => r.Warnings.Count > 0))
            Console.Error.WriteLine($"  Row {row.RowNumber} ({row.EntityType}) warnings: {string.Join("; ", row.Warnings)}");
        foreach (var row in result.RowResults.Where(r => !r.Success))
            Console.Error.WriteLine($"  Row {row.RowNumber} ({row.EntityType}): {row.Error}");
    }

    return result.FailedRows > 0 ? 1 : 0;
}

static async Task<int> RunSummaryAsync(
    CatalogCommands catalog,
    CatalogConfig config,
    bool jsonMode)
{
    var dbPath = RequireDbPath(config);
    var result = await catalog.SummaryAsync(dbPath).ConfigureAwait(false);

    if (jsonMode)
    {
        CommandOutput.WriteJson("summary", ok: true, exitCode: 0, data: new
        {
            databasePath = result.DatabasePath,
            currentMigrationVersion = result.CurrentMigrationVersion,
            counts = new
            {
                labels = result.LabelCount,
                artists = result.ArtistCount,
                releases = result.ReleaseCount,
                recordings = result.RecordingCount,
                mediaFiles = result.MediaFileCount,
            },
        });
    }
    else
    {
        CommandOutput.WriteText([
            ("database", result.DatabasePath),
            ("migrationVersion", result.CurrentMigrationVersion),
            ("labels", result.LabelCount),
            ("artists", result.ArtistCount),
            ("releases", result.ReleaseCount),
            ("recordings", result.RecordingCount),
            ("mediaFiles", result.MediaFileCount),
        ]);
    }

    return 0;
}

static async Task<int> RunDeleteEntityAsync(
    CatalogCommands catalog,
    CatalogConfig config,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
    var dbPath = RequireDbPath(config);
    var entityType = GetRequiredOption(options, "type");
    var entityId   = GetRequiredOption(options, "id");
    var deletedBy  = GetOption(options, "deleted-by");
    var reason     = GetOption(options, "reason");

    var result = await catalog.DeleteEntityAsync(
        new DeleteEntityRequest(
            DatabasePath: dbPath,
            EntityType: entityType,
            EntityId: entityId,
            DeletedBy: deletedBy,
            DeleteReason: reason))
        .ConfigureAwait(false);

    if (jsonMode)
    {
        CommandOutput.WriteJson("delete-entity", ok: result.Success, exitCode: result.Success ? 0 : 1, data: new
        {
            entityType = result.EntityType,
            entityId = result.EntityId,
            success = result.Success,
            errorMessage = result.ErrorMessage,
            cleanupRowsDeleted = result.CleanupRowsDeleted,
            tombstoneCaptured = result.TombstoneCaptured,
            blockers = result.Blockers.Select(b => new
            {
                tableName = b.TableName,
                columnName = b.ColumnName,
                rowCount = b.RowCount,
                reason = b.Reason,
            }),
        });
    }
    else
    {
        CommandOutput.WriteText([
            ("entityType", result.EntityType),
            ("entityId", result.EntityId),
            ("success", result.Success),
            ("cleanupRowsDeleted", result.CleanupRowsDeleted),
            ("tombstoneCaptured", result.TombstoneCaptured),
        ]);
        if (!result.Success)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            foreach (var blocker in result.Blockers)
                Console.Error.WriteLine($"  blocker: {blocker.TableName}.{blocker.ColumnName} ({blocker.RowCount} row(s)) — {blocker.Reason}");
        }
    }

    return result.Success ? 0 : 1;
}

static async Task<int> RunDoctorAsync(
    CatalogCommands catalog,
    CatalogConfig config,
    bool jsonMode)
{
    var dbPath = RequireDbPath(config);
    var result = await catalog.DoctorAsync(new DoctorRequest(dbPath)).ConfigureAwait(false);

    if (jsonMode)
    {
        CommandOutput.WriteJson("doctor", ok: !result.HasIssues, exitCode: result.HasIssues ? 1 : 0, data: new
        {
            databasePath = result.DatabasePath,
            databaseReachable = result.DatabaseReachable,
            currentMigrationVersion = result.CurrentMigrationVersion,
            counts = new
            {
                labels = result.LabelCount,
                artists = result.ArtistCount,
                releases = result.ReleaseCount,
                recordings = result.RecordingCount,
                mediaFiles = result.MediaFileCount,
            },
            findings = result.Findings,
            warnings = result.Warnings,
        });
    }
    else
    {
        CommandOutput.WriteText([
            ("database", result.DatabasePath),
            ("databaseReachable", result.DatabaseReachable),
            ("migrationVersion", result.CurrentMigrationVersion),
            ("labels", result.LabelCount),
            ("artists", result.ArtistCount),
            ("releases", result.ReleaseCount),
            ("recordings", result.RecordingCount),
            ("mediaFiles", result.MediaFileCount),
        ]);

        foreach (var finding in result.Findings)
            Console.Error.WriteLine($"  finding: {finding}");

        foreach (var warning in result.Warnings)
            Console.Error.WriteLine($"  warning: {warning}");
    }

    return result.HasIssues ? 1 : 0;
}

static async Task<int> RunRepairIndexesAsync(
    CatalogCommands catalog,
    CatalogConfig config,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
    var dbPath = RequireDbPath(config);
    var dryRun = options.ContainsKey("dry-run");

    var result = await catalog.RepairIndexesAsync(new RepairIndexesRequest(dbPath, dryRun)).ConfigureAwait(false);

    if (jsonMode)
    {
        CommandOutput.WriteJson("repair-indexes", ok: true, exitCode: 0, data: new
        {
            databasePath = result.DatabasePath,
            dryRun = result.DryRun,
            performed = result.Performed,
            actions = result.Actions,
            notes = result.Notes,
        });
    }
    else
    {
        CommandOutput.WriteText([
            ("database", result.DatabasePath),
            ("dryRun", result.DryRun),
            ("performed", result.Performed),
        ]);
        foreach (var action in result.Actions)
            Console.WriteLine($"  action: {action}");
        foreach (var note in result.Notes)
            Console.WriteLine($"  note: {note}");
    }

    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 2;
}

static string RequireDbPath(CatalogConfig config)
{
    if (string.IsNullOrWhiteSpace(config.Sqlite.DbPath))
        throw new ArgumentException("--db-path is required (or set sqlite.dbPath in config file / TRACKSTASH_SQLITE_DB_PATH env var).");
    return config.Sqlite.DbPath;
}

static Dictionary<string, string?> ParseOptions(string[] args, int startIndex)
{
    var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    for (var i = startIndex; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
            continue;

        var key = arg[2..];
        string? value = null;

        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[i + 1];
            i++;
        }

        options[key] = value;
    }

    return options;
}

static string GetRequiredOption(IReadOnlyDictionary<string, string?> options, string key)
{
    var value = GetOption(options, key);
    if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException($"--{key} is required.");
    return value;
}

static string? GetOption(IReadOnlyDictionary<string, string?> options, string key)
    => options.TryGetValue(key, out var value) ? value : null;

static TrackStash.Core.Storage.IStorageProviderFactory ResolveProviderFactory(string provider)
    => provider.ToLowerInvariant() switch
    {
        "sqlite" => new SqliteStorageProviderFactory(),
        _ => throw new ArgumentException($"Unsupported provider: {provider}"),
    };

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  trackstash-catalog import-csv    --db-path <path> --file <path.csv> [--dry-run] [--fail-fast] [--output json]");
    Console.WriteLine("  trackstash-catalog summary       --db-path <path> [--output json]");
    Console.WriteLine("  trackstash-catalog delete-entity --db-path <path> --type <label|artist|release|recording> --id <id> [--deleted-by <name>] [--reason <text>] [--output json]");
    Console.WriteLine("  trackstash-catalog doctor        --db-path <path> [--output json]");
    Console.WriteLine("  trackstash-catalog repair-indexes --db-path <path> [--dry-run] [--output json]");
}
