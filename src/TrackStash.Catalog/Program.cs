using TrackStash.Catalog;
using TrackStash.Catalog.Config;
using TrackStash.Catalog.Entities;
using TrackStash.Catalog.Output;
using TrackStash.Core.Storage;
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
    var jsonMode = CommandOutput.IsJsonMode(config.Output.Format);

    try
    {
        return command switch
        {
            "import-csv"    => await RunImportCsvAsync(CreateCatalogCommands(config, out var importTarget), importTarget.DatabasePath, options, jsonMode).ConfigureAwait(false),
            "summary"       => await RunSummaryAsync(CreateCatalogCommands(config, out var summaryTarget), summaryTarget.DatabasePath, jsonMode).ConfigureAwait(false),
            "delete-entity" => await RunDeleteEntityAsync(CreateCatalogCommands(config, out var deleteTarget), deleteTarget.DatabasePath, options, jsonMode).ConfigureAwait(false),
            "doctor"        => await RunDoctorAsync(CreateCatalogCommands(config, out var doctorTarget), doctorTarget.DatabasePath, jsonMode).ConfigureAwait(false),
            "repair-indexes" => await RunRepairIndexesAsync(CreateCatalogCommands(config, out var repairTarget), repairTarget.DatabasePath, options, jsonMode).ConfigureAwait(false),
            "template"      => await RunTemplateAsync(options, jsonMode).ConfigureAwait(false),
            "validate-entity" => await RunValidateEntityAsync(options, jsonMode).ConfigureAwait(false),
            "apply-entity"  => await RunApplyEntityAsync(ResolveCatalogTarget(config).DatabasePath, options, jsonMode).ConfigureAwait(false),
            "get-entity"    => await RunGetEntityAsync(ResolveCatalogTarget(config).DatabasePath, options, jsonMode).ConfigureAwait(false),
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
    string dbPath,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
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
    string dbPath,
    bool jsonMode)
{
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
    string dbPath,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
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
    string dbPath,
    bool jsonMode)
{
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
    string dbPath,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
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

static Task<int> RunTemplateAsync(
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
    var kind = GetRequiredOption(options, "kind").ToLowerInvariant();
    var validKinds = new[] { "label", "artist", "release", "recording" };
    if (!validKinds.Contains(kind))
        throw new ArgumentException($"--kind must be one of: {string.Join(", ", validKinds)}");

    var templatePath = ResolveTemplatePath(kind);
    var templateContent = File.ReadAllText(templatePath);

    if (jsonMode)
    {
        CommandOutput.WriteJson("template", ok: true, exitCode: 0, data: new
        {
            kind,
            templatePath,
            content = templateContent,
        });
    }
    else
    {
        Console.WriteLine(templateContent);
    }

    return Task.FromResult(0);
}

static Task<int> RunValidateEntityAsync(
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
    var filePath = GetRequiredOption(options, "file");
    if (!File.Exists(filePath))
        throw new ArgumentException($"File not found: {filePath}");

    var yaml = File.ReadAllText(filePath);
    var result = EntityYamlValidator.Validate(yaml);

    if (jsonMode)
    {
        CommandOutput.WriteJson("validate-entity", ok: result.IsValid, exitCode: result.IsValid ? 0 : 1, data: new
        {
            file = filePath,
            documentCount = result.DocumentCount,
            isValid = result.IsValid,
            errorCount = result.ErrorCount,
            warningCount = result.WarningCount,
            issues = result.Issues.Select(i => new
            {
                severity = i.Severity,
                path = i.Path,
                message = i.Message,
            }),
        });
    }
    else
    {
        CommandOutput.WriteText([
            ("file", filePath),
            ("documentCount", result.DocumentCount),
            ("isValid", result.IsValid),
            ("errorCount", result.ErrorCount),
            ("warningCount", result.WarningCount),
        ]);

        foreach (var issue in result.Issues)
            Console.Error.WriteLine($"  {issue.Severity}: {issue.Path} - {issue.Message}");
    }

    return Task.FromResult(result.IsValid ? 0 : 1);
}

static Task<int> RunApplyEntityAsync(
    string dbPath,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
    var filePath = GetRequiredOption(options, "file");
    var dryRun = options.ContainsKey("dry-run");
    var message = "Command scaffolded only. Desired-state apply/reconcile engine is not implemented yet.";

    if (jsonMode)
    {
        CommandOutput.WriteJson("apply-entity", ok: false, exitCode: 1, data: new
        {
            databasePath = dbPath,
            file = filePath,
            dryRun,
            implemented = false,
            nextStep = "Add YAML-to-domain mapping and mode-specific transactional reconciliation.",
        }, errors: [message]);
    }
    else
    {
        CommandOutput.WriteText([
            ("database", dbPath),
            ("file", filePath),
            ("dryRun", dryRun),
            ("implemented", false),
        ]);
        Console.Error.WriteLine(message);
    }

    return Task.FromResult(1);
}

static Task<int> RunGetEntityAsync(
    string dbPath,
    IReadOnlyDictionary<string, string?> options,
    bool jsonMode)
{
    var entityType = GetRequiredOption(options, "type").ToLowerInvariant();
    var entityId = GetRequiredOption(options, "id");
    var format = GetOption(options, "format") ?? "yaml";
    var validTypes = new[] { "label", "artist", "release", "recording" };
    if (!validTypes.Contains(entityType))
        throw new ArgumentException($"--type must be one of: {string.Join(", ", validTypes)}");

    var message = "Command scaffolded only. Entity lookup and YAML export are not implemented yet.";

    if (jsonMode)
    {
        CommandOutput.WriteJson("get-entity", ok: false, exitCode: 1, data: new
        {
            databasePath = dbPath,
            entityType,
            entityId,
            format,
            implemented = false,
            nextStep = "Add provider-backed entity retrieval and desired-state document emitter.",
        }, errors: [message]);
    }
    else
    {
        CommandOutput.WriteText([
            ("database", dbPath),
            ("entityType", entityType),
            ("entityId", entityId),
            ("format", format),
            ("implemented", false),
        ]);
        Console.Error.WriteLine(message);
    }

    return Task.FromResult(1);
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 2;
}

static ResolvedCatalogTarget ResolveCatalogTarget(CatalogConfig config)
{
    var catalogName = string.IsNullOrWhiteSpace(config.Catalog)
        ? "default"
        : config.Catalog.Trim();

    CatalogTargetConfig? mappedTarget = null;
    if (config.Catalogs is not null)
        config.Catalogs.TryGetValue(catalogName, out mappedTarget);

    var provider = mappedTarget?.Provider;
    if (string.IsNullOrWhiteSpace(provider))
        provider = config.Provider;

    var dbPath = mappedTarget?.Sqlite?.DbPath;
    if (string.IsNullOrWhiteSpace(dbPath))
        dbPath = config.Sqlite.DbPath;

    if (string.IsNullOrWhiteSpace(dbPath))
        throw new ArgumentException(
            $"No database path resolved for catalog '{catalogName}'. Provide --catalog with a mapped config entry, or set --db-path / TRACKSTASH_SQLITE_DB_PATH.");

    return new ResolvedCatalogTarget(
        CatalogName: catalogName,
        Provider: provider!,
        DatabasePath: dbPath);
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

static CatalogCommands CreateCatalogCommands(CatalogConfig config, out ResolvedCatalogTarget target)
{
    target = ResolveCatalogTarget(config);
    var providerFactory = ResolveProviderFactory(target.Provider);
    return new CatalogCommands(providerFactory, target.Provider);
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  trackstash-catalog import-csv    [--catalog <name>] [--db-path <path>] --file <path.csv> [--dry-run] [--fail-fast] [--output json]");
    Console.WriteLine("  trackstash-catalog summary       [--catalog <name>] [--db-path <path>] [--output json]");
    Console.WriteLine("  trackstash-catalog delete-entity [--catalog <name>] [--db-path <path>] --type <label|artist|release|recording> --id <id> [--deleted-by <name>] [--reason <text>] [--output json]");
    Console.WriteLine("  trackstash-catalog doctor        [--catalog <name>] [--db-path <path>] [--output json]");
    Console.WriteLine("  trackstash-catalog repair-indexes [--catalog <name>] [--db-path <path>] [--dry-run] [--output json]");
    Console.WriteLine("  trackstash-catalog template      --kind <label|artist|release|recording> [--output json]");
    Console.WriteLine("  trackstash-catalog validate-entity --file <path.yaml> [--output json]");
    Console.WriteLine("  trackstash-catalog apply-entity  [--catalog <name>] [--db-path <path>] --file <path.yaml> [--dry-run] [--output json]");
    Console.WriteLine("  trackstash-catalog get-entity    [--catalog <name>] [--db-path <path>] --type <label|artist|release|recording> --id <id> [--format yaml] [--output json]");
}

static string ResolveTemplatePath(string kind)
{
    var relative = Path.Combine("templates", "entities", $"{kind}.v1.yaml");

    var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), relative);
    if (File.Exists(cwdCandidate))
        return cwdCandidate;

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate))
            return candidate;

        if (string.Equals(dir.Name, "trackstash-catalog", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }

        dir = dir.Parent;
    }

    throw new ArgumentException($"Template not found for kind '{kind}'. Expected at {relative}.");
}

internal sealed record ResolvedCatalogTarget(
    string CatalogName,
    string Provider,
    string DatabasePath);
