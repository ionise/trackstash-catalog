using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public interface ILabelRelationshipService
{
    ValueTask<LabelRelationships?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<LabelRelationshipResult> ReplaceAsync(string id, string version, IReadOnlyList<LabelRelationshipInput> items, CancellationToken ct = default);
}

public sealed class LabelRelationshipService(IStorageProvider storage) : ILabelRelationshipService
{
    public async ValueTask<LabelRelationships?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        return await unit.LabelRelationships.GetAsync(id, ct);
    }
    public async ValueTask<LabelRelationshipResult> ReplaceAsync(string id, string version, IReadOnlyList<LabelRelationshipInput> items, CancellationToken ct = default)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var result = await unit.LabelRelationships.ReplaceOutgoingAsync(id, version, items, ct);
        if (result.Status == LabelUpdateStatus.Updated) await unit.CommitAsync(ct);
        return result;
    }
}
