using TrackStash.Core.Normalization;
using TrackStash.Core.Services;
using TrackStash.Core.Storage;
using TrackStash.Catalog.Entities;

namespace TrackStash.Catalog;

// ── Request / result types ─────────────────────────────────────────────────────

public sealed record ImportCsvRequest(
    string DatabasePath,
    string CsvPath,
    bool DryRun = false,
    bool FailFast = false);

public sealed record CatalogSummaryResult(
    string DatabasePath,
    int LabelCount,
    int ArtistCount,
    int ReleaseCount,
    int RecordingCount,
    int MediaFileCount,
    int CurrentMigrationVersion);

public sealed record DeleteEntityRequest(
    string DatabasePath,
    string EntityType,
    string EntityId,
    string? DeletedBy = null,
    string? DeleteReason = null);

public sealed record DeleteEntityResult(
    string EntityType,
    string EntityId,
    bool Success,
    string? ErrorMessage,
    int CleanupRowsDeleted,
    bool TombstoneCaptured,
    IReadOnlyList<DeleteBlocker> Blockers);

public sealed record DoctorRequest(
    string DatabasePath);

public sealed record DoctorResult(
    string DatabasePath,
    int CurrentMigrationVersion,
    bool DatabaseReachable,
    int LabelCount,
    int ArtistCount,
    int ReleaseCount,
    int RecordingCount,
    int MediaFileCount,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Warnings)
{
    public bool HasIssues => Findings.Count > 0;
}

public sealed record RepairIndexesRequest(
    string DatabasePath,
    bool DryRun = false);

public sealed record RepairIndexesResult(
    string DatabasePath,
    bool DryRun,
    bool Performed,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Notes);

public sealed record ApplyEntityRequest(
    string DatabasePath,
    string FilePath,
    bool DryRun = false);

public sealed record ApplyEntityResult(
    string DatabasePath,
    string FilePath,
    bool DryRun,
    bool Success,
    string Kind,
    string Mode,
    string EntityId,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record GetEntityRequest(
    string DatabasePath,
    string EntityType,
    string EntityId,
    string Format = "yaml");

public sealed record GetEntityResult(
    string DatabasePath,
    string EntityType,
    string EntityId,
    string Format,
    bool Found,
    string? Content,
    string? ErrorMessage);

public sealed record ResolveEntityIdentityRequest(
    string Value);

public sealed record ResolveEntityIdentityResult(
    string Value,
    string NormalizedName,
    string Slug);

// ── Service facade ─────────────────────────────────────────────────────────────

/// <summary>
/// Catalog-facing service facade. Composes core storage contracts and services;
/// does not introduce direct knowledge of SQLite internals.
/// </summary>
public sealed class CatalogCommands
{
    private readonly IStorageProviderFactory _providerFactory;
    private readonly string _provider;

    public CatalogCommands(IStorageProviderFactory providerFactory, string provider)
    {
        _providerFactory = providerFactory;
        _provider = provider;
    }

    public async Task<CatalogImportResult> ImportCsvAsync(
        ImportCsvRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CsvPath);

        var provider = _providerFactory.Create(new StorageProviderDescriptor(
            Provider: _provider,
            DatabasePath: request.DatabasePath));
        var service = new CatalogImportService(provider);

        return await service.ImportAsync(
            new CatalogImportRequest(
                CsvPath: request.CsvPath,
                DryRun: request.DryRun,
                FailFast: request.FailFast),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CatalogSummaryResult> SummaryAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var provider = _providerFactory.Create(new StorageProviderDescriptor(
            Provider: _provider,
            DatabasePath: databasePath));
        var version = await provider.Migrations.GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);

        await using var uow = await provider.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);

        var labelCount = await uow.Labels.CountAsync(cancellationToken).ConfigureAwait(false);
        var artistCount = await uow.Artists.CountAsync(cancellationToken).ConfigureAwait(false);
        var releaseCount = await uow.Releases.CountAsync(cancellationToken).ConfigureAwait(false);
        var recordingCount = await uow.Recordings.CountAsync(cancellationToken).ConfigureAwait(false);
        var mediaFileCount = await uow.MediaFiles.CountAsync(cancellationToken).ConfigureAwait(false);

        return new CatalogSummaryResult(
            DatabasePath: databasePath,
            LabelCount: labelCount,
            ArtistCount: artistCount,
            ReleaseCount: releaseCount,
            RecordingCount: recordingCount,
            MediaFileCount: mediaFileCount,
            CurrentMigrationVersion: version);
    }

    public async Task<DeleteEntityResult> DeleteEntityAsync(
        DeleteEntityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntityId);

        var validTypes = new[] { "label", "artist", "release", "recording" };
        if (!validTypes.Contains(request.EntityType.ToLowerInvariant()))
            throw new ArgumentException($"--type must be one of: {string.Join(", ", validTypes)}");

        var provider = _providerFactory.Create(new StorageProviderDescriptor(
            Provider: _provider,
            DatabasePath: request.DatabasePath));
        await using var uow = await provider.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);

        var deleteService = uow.EntityDelete!;

        // Dry-run analysis first so blockers are always available in result
        var analysis = await deleteService
            .AnalyzeDependenciesAsync(request.EntityType.ToLowerInvariant(), request.EntityId, uow, cancellationToken)
            .ConfigureAwait(false);

        if (!analysis.IsSafeToDelete)
        {
            return new DeleteEntityResult(
                EntityType: request.EntityType,
                EntityId: request.EntityId,
                Success: false,
                ErrorMessage: $"Cannot delete: {analysis.Blockers.Count} blocking reference(s) remain.",
                CleanupRowsDeleted: 0,
                TombstoneCaptured: false,
                Blockers: analysis.Blockers);
        }

        var result = await deleteService
            .DeleteEntityAsync(
                request.EntityType.ToLowerInvariant(),
                request.EntityId,
                deletedBy: request.DeletedBy,
                deleteReason: request.DeleteReason,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new DeleteEntityResult(
            EntityType: request.EntityType,
            EntityId: request.EntityId,
            Success: result.Success,
            ErrorMessage: result.ErrorMessage,
            CleanupRowsDeleted: result.CleanupRowsDeleted,
            TombstoneCaptured: result.Tombstone is not null,
            Blockers: []);
    }

    public async Task<DoctorResult> DoctorAsync(
        DoctorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);

        var provider = _providerFactory.Create(new StorageProviderDescriptor(
            Provider: _provider,
            DatabasePath: request.DatabasePath));

        var findings = new List<string>();
        var warnings = new List<string>();

        var version = await provider.Migrations.GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);

        var labelCount = 0;
        var artistCount = 0;
        var releaseCount = 0;
        var recordingCount = 0;
        var mediaFileCount = 0;

        try
        {
            await using var uow = await provider.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);

            labelCount = await uow.Labels.CountAsync(cancellationToken).ConfigureAwait(false);
            artistCount = await uow.Artists.CountAsync(cancellationToken).ConfigureAwait(false);
            releaseCount = await uow.Releases.CountAsync(cancellationToken).ConfigureAwait(false);
            recordingCount = await uow.Recordings.CountAsync(cancellationToken).ConfigureAwait(false);
            mediaFileCount = await uow.MediaFiles.CountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            findings.Add($"Unable to query catalog tables: {ex.Message}");
            findings.Add("Run bootstrap init-db/migrate before catalog operations.");
        }

        if (version <= 0)
            findings.Add("No schema migration applied. Run bootstrap init-db/migrate.");

        if (recordingCount > 0 && releaseCount == 0)
            findings.Add("Recordings exist without any releases. Catalog may be partially ingested.");

        if ((releaseCount > 0 || recordingCount > 0) && artistCount == 0)
            findings.Add("Releases/recordings exist without artists. Catalog may be incomplete.");

        if (labelCount == 0 && artistCount == 0 && releaseCount == 0 && recordingCount == 0)
            warnings.Add("Catalog is empty.");

        if (mediaFileCount > 0 && recordingCount == 0)
            warnings.Add("Media files exist but no canonical recordings are present yet.");

        return new DoctorResult(
            DatabasePath: request.DatabasePath,
            CurrentMigrationVersion: version,
            DatabaseReachable: true,
            LabelCount: labelCount,
            ArtistCount: artistCount,
            ReleaseCount: releaseCount,
            RecordingCount: recordingCount,
            MediaFileCount: mediaFileCount,
            Findings: findings,
            Warnings: warnings);
    }

    public async Task<RepairIndexesResult> RepairIndexesAsync(
        RepairIndexesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);

        // Placeholder implementation: all current indexes are maintained by migrations/upsert paths.
        // Keep this command idempotent and safe; wire backend-specific rebuild actions as needed.
        var provider = _providerFactory.Create(new StorageProviderDescriptor(
            Provider: _provider,
            DatabasePath: request.DatabasePath));

        var version = await provider.Migrations.GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);
        var actions = new List<string>();
        var notes = new List<string>();

        if (request.DryRun)
        {
            actions.Add("Validate migration state");
            actions.Add("Verify derived index maintenance requirements");
            notes.Add($"Dry-run only. Current migration version: {version}");
            return new RepairIndexesResult(request.DatabasePath, DryRun: true, Performed: false, Actions: actions, Notes: notes);
        }

        actions.Add("Validated migration state");
        notes.Add("No additional derived index repair actions are currently registered.");
        notes.Add($"Current migration version: {version}");

        return new RepairIndexesResult(request.DatabasePath, DryRun: false, Performed: true, Actions: actions, Notes: notes);
    }

    public async Task<ApplyEntityResult> ApplyEntityAsync(
        ApplyEntityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);

        if (!File.Exists(request.FilePath))
            throw new ArgumentException($"File not found: {request.FilePath}");

        var yaml = File.ReadAllText(request.FilePath);
        var envelope = EntityYamlService.ParseSingleDocument(yaml);

        var provider = _providerFactory.Create(new StorageProviderDescriptor(
            Provider: _provider,
            DatabasePath: request.DatabasePath));

        var result = await EntityYamlService.ApplyAsync(
            envelope,
            provider,
            providerName: _provider,
            dbPath: request.DatabasePath,
            dryRun: request.DryRun,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ApplyEntityResult(
            DatabasePath: request.DatabasePath,
            FilePath: request.FilePath,
            DryRun: request.DryRun,
            Success: result.Success,
            Kind: result.Kind,
            Mode: result.Mode,
            EntityId: result.EntityId,
            Actions: result.Actions,
            Warnings: result.Warnings,
            Errors: result.Errors);
    }

    public async Task<GetEntityResult> GetEntityAsync(
        GetEntityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntityId);

        var validTypes = new[] { "label", "artist", "release", "recording" };
        if (!validTypes.Contains(request.EntityType.ToLowerInvariant()))
            throw new ArgumentException($"--type must be one of: {string.Join(", ", validTypes)}");

        if (!string.Equals(request.Format, "yaml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--format currently supports only yaml.");

        var provider = _providerFactory.Create(new StorageProviderDescriptor(
            Provider: _provider,
            DatabasePath: request.DatabasePath));

        var envelope = await EntityYamlService.GetEntityAsync(
            request.EntityType.ToLowerInvariant(),
            request.EntityId,
            provider,
            providerName: _provider,
            dbPath: request.DatabasePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (envelope is null)
        {
            return new GetEntityResult(
                DatabasePath: request.DatabasePath,
                EntityType: request.EntityType,
                EntityId: request.EntityId,
                Format: request.Format,
                Found: false,
                Content: null,
                ErrorMessage: "Entity not found.");
        }

        var yaml = EntityYamlService.ToYaml(envelope);
        return new GetEntityResult(
            DatabasePath: request.DatabasePath,
            EntityType: request.EntityType,
            EntityId: request.EntityId,
            Format: request.Format,
            Found: true,
            Content: yaml,
            ErrorMessage: null);
    }

    public Task<ResolveEntityIdentityResult> ResolveEntityIdentityAsync(
        ResolveEntityIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Value);

        var identity = EntityNameNormalizer.NormalizeWithSlug(request.Value);
        return Task.FromResult(new ResolveEntityIdentityResult(
            Value: request.Value,
            NormalizedName: identity.NormalizedName,
            Slug: identity.Slug));
    }
}
