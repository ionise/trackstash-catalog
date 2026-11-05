using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

internal sealed class CatalogReadinessCheck(IStorageProvider storage, ILogger<CatalogReadinessCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(cancellationToken) < 7)
            throw new InvalidOperationException("Catalog schema version 7 is required. Back up the canonical catalog and run explicit migrations before starting this host.");
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken);
        await unit.Labels.QueryAsync(new LabelQuery { PageSize = 1 }, cancellationToken);
        logger.LogInformation("Catalog label storage is ready. No startup migrations or seeding were performed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
