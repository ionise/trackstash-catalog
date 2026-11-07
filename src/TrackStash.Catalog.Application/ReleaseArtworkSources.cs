using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public sealed class ReleaseArtworkUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ReleaseArtworkNotFoundException : Exception;
public sealed record ReleaseArtworkSourceQuery(string ReleaseId, string? Title, string? CatalogNumber,
    IReadOnlyList<ReleaseReferenceDetail> ExternalReferences);
public sealed record ReleaseArtworkCandidate(string Provider, string CandidateId, string Revision, string Title,
    string SourceUrl, string? Attribution);
public sealed record ReleaseArtworkCandidatePage(IReadOnlyList<string> Providers, IReadOnlyList<ReleaseArtworkCandidate> Items);
public sealed record ReleaseArtworkCandidateContent(byte[] Bytes, string Filename);

/// <summary>Server-configured adapter. IDs/revisions must be release-scoped; downloads must be bounded, permission-aware and restricted to approved endpoints.</summary>
public interface IReleaseArtworkProvider
{
    string Name { get; }
    ValueTask<IReadOnlyList<ReleaseArtworkCandidate>> FindAsync(ReleaseArtworkSourceQuery query, CancellationToken ct = default);
    ValueTask<ReleaseArtworkCandidateContent> ReadAsync(ReleaseArtworkSourceQuery query, ReleaseArtworkCandidate candidate, CancellationToken ct = default);
}
public interface IReleaseArtworkSourceService
{
    ValueTask<ReleaseArtworkCandidatePage> FindAsync(string releaseId, CancellationToken ct = default);
    ValueTask<ArtworkWriteResult> AttachAsync(string releaseId, string version, string provider, string candidateId,
        string revision, string usageRights, bool confirmed, CancellationToken ct = default, bool isPreferred = true);
    ValueTask<ReleaseArtworkCandidateContent> PreviewAsync(string releaseId, string provider, string candidateId,
        string revision, CancellationToken ct = default);
}

public sealed class ReleaseArtworkSourceService(IReleaseService releases, IArtworkService artwork,
    IEnumerable<IReleaseArtworkProvider> configured) : IReleaseArtworkSourceService
{
    private readonly IReadOnlyList<IReleaseArtworkProvider> _providers = configured.ToArray();
    private void ValidateProviders()
    {
        if (_providers.Count > 10 || _providers.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != _providers.Count ||
            _providers.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100))
            throw new ReleaseArtworkUnavailableException("Artwork providers are not configured correctly.");
    }
    private async Task<ReleaseArtworkSourceQuery> Query(string id, CancellationToken ct)
    {
        var release = await releases.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new ReleaseArtworkNotFoundException();
        return new(release.Id, release.Title, release.CatalogNumber, release.ExternalReferences);
    }
    private async Task<IReadOnlyList<ReleaseArtworkCandidate>> Candidates(IReleaseArtworkProvider provider, ReleaseArtworkSourceQuery query, CancellationToken ct)
    {
        IReadOnlyList<ReleaseArtworkCandidate> items;
        try { items = await provider.FindAsync(query, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new ReleaseArtworkUnavailableException("Artwork provider lookup failed. No artwork was attached.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new ReleaseArtworkUnavailableException("Artwork provider lookup timed out. No artwork was attached.", ex); }
        if (items.Count > 100 || items.Select(i => i.CandidateId).Distinct(StringComparer.Ordinal).Count() != items.Count ||
            items.Any(i => i.Provider != provider.Name || string.IsNullOrWhiteSpace(i.CandidateId) || i.CandidateId.Length > 200 ||
                string.IsNullOrWhiteSpace(i.Revision) || i.Revision.Length > 200 || string.IsNullOrWhiteSpace(i.Title) || i.Title.Length > 500 ||
                i.Attribution?.Length > 1000))
            throw new ReleaseArtworkUnavailableException("Artwork provider returned invalid or oversized candidates.");
        foreach (var candidate in items)
            if (!Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) || candidate.SourceUrl.Length > 2048)
                throw new ReleaseArtworkUnavailableException("Artwork provider returned an invalid source URL.");
        return items;
    }
    public async ValueTask<ReleaseArtworkCandidatePage> FindAsync(string releaseId, CancellationToken ct = default)
    {
        if (await artwork.GetReleaseAsync(releaseId, ct).ConfigureAwait(false) is null) throw new ReleaseArtworkNotFoundException();
        var query = await Query(releaseId, ct).ConfigureAwait(false);
        var items = new List<ReleaseArtworkCandidate>();
        ValidateProviders();
        foreach (var provider in _providers)
            items.AddRange(await Candidates(provider, query, ct).ConfigureAwait(false));
        return new(_providers.Select(p => p.Name).ToArray(), items);
    }
    public async ValueTask<ArtworkWriteResult> AttachAsync(string releaseId, string version, string provider, string candidateId,
        string revision, string usageRights, bool confirmed, CancellationToken ct = default, bool isPreferred = true)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(usageRights) || usageRights.Length > 2000 ||
            string.IsNullOrWhiteSpace(candidateId) || candidateId.Length > 200 || string.IsNullOrWhiteSpace(revision) || revision.Length > 200 ||
            string.IsNullOrWhiteSpace(provider) || provider.Length > 100)
            throw new ArgumentException("Confirm attachment and record usage rights for a valid source candidate.");
        var gallery = await artwork.GetReleaseAsync(releaseId, ct).ConfigureAwait(false);
        if (gallery is null) return new(LabelUpdateStatus.NotFound);
        if (gallery.Version != version) return new(LabelUpdateStatus.Stale);
        ValidateProviders();
        var adapter = _providers.SingleOrDefault(p => p.Name == provider)
            ?? throw new ReleaseArtworkUnavailableException("This artwork provider is not configured. No external artwork was fetched.");
        var query = await Query(releaseId, ct).ConfigureAwait(false);
        var candidate = (await Candidates(adapter, query, ct).ConfigureAwait(false)).SingleOrDefault(c => c.CandidateId == candidateId && c.Revision == revision)
            ?? throw new ReleaseArtworkUnavailableException("Artwork candidate changed or is no longer available. Look up candidates again.");
        // The adapter resolves its opaque candidate, never a URL supplied by the browser.
        ReleaseArtworkCandidateContent content;
        try { content = await adapter.ReadAsync(query, candidate, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new ReleaseArtworkUnavailableException("Artwork provider download failed. No artwork was attached.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new ReleaseArtworkUnavailableException("Artwork provider download timed out. No artwork was attached.", ex); }
        return await artwork.UploadReleaseAsync(releaseId, version, new(content.Bytes, content.Filename, candidate.SourceUrl,
            candidate.Attribution, usageRights, "cover", candidate.Title, isPreferred,
            Source: new(candidate.Provider, candidate.CandidateId, candidate.Revision)), ct).ConfigureAwait(false);
    }

    public async ValueTask<ReleaseArtworkCandidateContent> PreviewAsync(string releaseId, string provider, string candidateId,
        string revision, CancellationToken ct = default)
    {
        ValidateProviders();
        if (await artwork.GetReleaseAsync(releaseId, ct).ConfigureAwait(false) is null) throw new ReleaseArtworkNotFoundException();
        var adapter = _providers.SingleOrDefault(p => p.Name == provider)
            ?? throw new ReleaseArtworkUnavailableException("Artwork provider is not configured.");
        var query = await Query(releaseId, ct).ConfigureAwait(false);
        var candidate = (await Candidates(adapter, query, ct).ConfigureAwait(false))
            .SingleOrDefault(c => c.CandidateId == candidateId && c.Revision == revision)
            ?? throw new ReleaseArtworkUnavailableException("Artwork candidate changed. Look up candidates again.");
        try { return await adapter.ReadAsync(query, candidate, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new ReleaseArtworkUnavailableException("Artwork preview download failed.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new ReleaseArtworkUnavailableException("Artwork preview download timed out.", ex); }
    }
}
