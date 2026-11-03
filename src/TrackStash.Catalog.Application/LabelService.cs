using TrackStash.Core.Identifiers;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public sealed record LabelAliasDetail(string Value, bool IsPrimary);
public sealed record LabelReferenceDetail(string Source, string ExternalId, bool IsPrimary, DateTimeOffset? LastSeenUtc);
public sealed record LabelDetail(
    string Id, string? Name, string? SortName, string Version,
    DateTimeOffset? CreatedUtc, DateTimeOffset? UpdatedUtc,
    IReadOnlyList<LabelAliasDetail> Aliases, IReadOnlyList<LabelReferenceDetail> ExternalReferences);

public enum LabelWriteStatus { Created, Updated, NotFound, Stale, Conflict }
public sealed record LabelWriteResult(LabelWriteStatus Status, LabelDetail? Label = null);

public sealed class LabelService
{
    private readonly IStorageProvider _storage;

    public LabelService(IStorageProvider storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async ValueTask<LabelPage> QueryAsync(LabelQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await using var unit = await _storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        return await unit.Labels.QueryAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LabelDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var unit = await _storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var label = await unit.Labels.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return label is null ? null : ToDetail(label);
    }

    public async ValueTask<LabelWriteResult> CreateAsync(string name, string? sortName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = DateTimeOffset.UtcNow;
        var label = new Label
        {
            Id = EntityId.New(), Name = name.Trim(), NormalizedName = EntityNameNormalizer.NormalizeStrict(name),
            SortName = sortName, CreatedUtc = now, UpdatedUtc = now,
        };
        await using var unit = await _storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        if (!await unit.Labels.TryCreateAsync(label, cancellationToken).ConfigureAwait(false))
            return new LabelWriteResult(LabelWriteStatus.Conflict);
        var stored = await unit.Labels.GetByIdAsync(label.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Created label could not be read in its transaction.");
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LabelWriteResult(LabelWriteStatus.Created, ToDetail(stored));
    }

    public async ValueTask<LabelWriteResult> UpdateAsync(string id, string expectedVersion, LabelUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentNullException.ThrowIfNull(update);
        update.Validate();
        await using var unit = await _storage.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);
        var result = await unit.Labels.UpdateAsync(id, expectedVersion, update, cancellationToken).ConfigureAwait(false);
        if (result.Status == LabelUpdateStatus.NotFound)
            return new LabelWriteResult(LabelWriteStatus.NotFound);
        if (result.Status == LabelUpdateStatus.Stale)
            return new LabelWriteResult(LabelWriteStatus.Stale);
        var stored = result.Label ?? throw new InvalidOperationException("Updated label result is missing its label.");
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LabelWriteResult(LabelWriteStatus.Updated, ToDetail(stored));
    }

    private static LabelDetail ToDetail(Label label) => new(
        label.Id, label.Name, label.SortName, label.Version, label.CreatedUtc, label.UpdatedUtc,
        label.Aliases.OrderBy(a => a.Value, StringComparer.Ordinal)
            .Select(a => new LabelAliasDetail(a.Value, a.IsPrimary)).ToArray(),
        label.ExternalReferences.OrderBy(r => r.Source, StringComparer.Ordinal).ThenBy(r => r.ExternalId, StringComparer.Ordinal)
            .Select(r => new LabelReferenceDetail(r.Source, r.ExternalId, r.IsPrimary, r.LastSeenUtc)).ToArray());
}
