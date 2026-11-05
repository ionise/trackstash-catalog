using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public interface ILabelGroupService
{
    ValueTask<GroupPage<LabelGroup>> QueryAsync(GroupQuery query, CancellationToken ct);
    ValueTask<LabelGroup?> GetAsync(string id, CancellationToken ct);
    ValueTask<GroupWriteResult> CreateAsync(string kind, GroupFields fields, CancellationToken ct);
    ValueTask<GroupWriteResult> UpdateAsync(string id, string version, IReadOnlyDictionary<string, string?> fields, CancellationToken ct);
    ValueTask<GroupPage<MembershipView>?> MembershipsAsync(string? labelId, string? groupId, GroupQuery query, CancellationToken ct);
    ValueTask<MembershipView?> MembershipAsync(string id, CancellationToken ct);
    ValueTask<GroupWriteResult> SaveMembershipAsync(string? id, string? version, string labelId, string groupId,
        string labelVersion, string groupVersion, MembershipFields fields, bool confirmRejoin, CancellationToken ct);
    ValueTask<GroupWriteResult> RemoveMembershipAsync(string id, string version, CancellationToken ct);
    ValueTask<GroupPage<RelatedLabelItem>?> RelatedAsync(string labelId, GroupQuery query, CancellationToken ct);
}

public sealed class LabelGroupService(IStorageProvider storage) : ILabelGroupService
{
    private async ValueTask<T> Read<T>(Func<ILabelGroupRepository, ValueTask<T>> action, CancellationToken ct)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        return await action(unit.LabelGroups);
    }
    private async ValueTask<GroupWriteResult> Write(Func<ILabelGroupRepository, ValueTask<GroupWriteResult>> action, CancellationToken ct)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var result = await action(unit.LabelGroups);
        if (result.Status == GroupWriteStatus.Updated) await unit.CommitAsync(ct);
        return result;
    }
    public ValueTask<GroupPage<LabelGroup>> QueryAsync(GroupQuery query, CancellationToken ct) => Read(r => r.QueryAsync(query, ct), ct);
    public ValueTask<LabelGroup?> GetAsync(string id, CancellationToken ct) => Read(r => r.GetAsync(id, ct), ct);
    public ValueTask<GroupWriteResult> CreateAsync(string kind, GroupFields fields, CancellationToken ct) => Write(r => r.CreateAsync(kind, fields, ct), ct);
    public ValueTask<GroupWriteResult> UpdateAsync(string id, string version, IReadOnlyDictionary<string, string?> fields, CancellationToken ct) => Write(async r =>
    {
        var current = await r.GetAsync(id, ct);
        if (current is null) return new(GroupWriteStatus.NotFound);
        string? Value(string key, string? fallback) => fields.TryGetValue(key, out var value) ? value : fallback;
        if (fields.Count == 0) throw new GroupValidationException("Supply at least one group field.");
        return await r.UpdateAsync(id, version, new(Value("name", current.Name) ?? "", Value("sortName", current.SortName),
            Value("description", current.Description), Value("website", current.Website)), ct);
    }, ct);
    public ValueTask<GroupPage<MembershipView>?> MembershipsAsync(string? labelId, string? groupId, GroupQuery query, CancellationToken ct) =>
        Read(r => r.MembershipsAsync(labelId, groupId, query, ct), ct);
    public ValueTask<MembershipView?> MembershipAsync(string id, CancellationToken ct) => Read(r => r.MembershipAsync(id, ct), ct);
    public ValueTask<GroupWriteResult> SaveMembershipAsync(string? id, string? version, string labelId, string groupId,
        string labelVersion, string groupVersion, MembershipFields fields, bool confirmRejoin, CancellationToken ct) =>
        Write(r => r.SaveMembershipAsync(id, version, labelId, groupId, labelVersion, groupVersion, fields, confirmRejoin, ct), ct);
    public ValueTask<GroupWriteResult> RemoveMembershipAsync(string id, string version, CancellationToken ct) => Write(r => r.RemoveMembershipAsync(id, version, ct), ct);
    public ValueTask<GroupPage<RelatedLabelItem>?> RelatedAsync(string labelId, GroupQuery query, CancellationToken ct) => Read(r => r.RelatedAsync(labelId, query, ct), ct);
}
