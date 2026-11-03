using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public interface ILabelService
{
    ValueTask<LabelPage> QueryAsync(LabelQuery query, CancellationToken cancellationToken = default);
    ValueTask<LabelDetail?> GetAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<LabelWriteResult> CreateAsync(string name, string? sortName = null, CancellationToken cancellationToken = default);
    ValueTask<LabelWriteResult> UpdateAsync(string id, string expectedVersion, LabelUpdate update, CancellationToken cancellationToken = default);
}
