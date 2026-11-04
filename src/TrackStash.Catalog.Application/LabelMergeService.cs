using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public interface ILabelMergeService
{
    ValueTask<LabelIdentity> ResolveAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<LabelMergeResult> PreviewAsync(string survivorId, string retiredId, CancellationToken cancellationToken = default);
    ValueTask<LabelMergeResult> GetPreviewAsync(string operationId, CancellationToken cancellationToken = default);
    ValueTask<LabelMergeResult> ApplyAsync(string operationId, string expectedVersion, string reason, string? operatorName,
        CancellationToken cancellationToken = default);
}

public sealed class LabelMergeService(IStorageProvider storage, TimeProvider clock) : ILabelMergeService
{
    public async ValueTask<LabelIdentity> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        return await unit.Labels.ResolveIdentityAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LabelMergeResult> PreviewAsync(string survivorId, string retiredId, CancellationToken cancellationToken = default)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var result = await unit.LabelMerges.PreviewAsync(survivorId, retiredId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (result.Status == LabelMergeStatus.Ready) await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<LabelMergeResult> GetPreviewAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        return await unit.LabelMerges.GetPreviewAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LabelMergeResult> ApplyAsync(string operationId, string expectedVersion, string reason, string? operatorName,
        CancellationToken cancellationToken = default)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var result = await unit.LabelMerges.ApplyAsync(operationId, expectedVersion, reason, operatorName,
            "local-api-key", clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (result.Status == LabelMergeStatus.Applied) await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
