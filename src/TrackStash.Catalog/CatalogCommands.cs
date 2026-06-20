using TrackStash.Core.Services;
using TrackStash.Core.Storage;

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
}
