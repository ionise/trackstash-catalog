using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api.Beatport;

public sealed class NamedArtworkNotFoundException : Exception;

public interface INamedArtworkSources
{
    ValueTask<ReleaseArtworkCandidatePage> FindAsync(string kind, string id, CancellationToken ct);
    ValueTask<ReleaseArtworkCandidateContent> PreviewAsync(string kind, string id, string provider, string candidateId, string revision, CancellationToken ct);
    ValueTask<ArtworkWriteResult> AttachAsync(string kind, string id, string version, string provider, string candidateId, string revision, bool preferred, CancellationToken ct);
}

public sealed class NamedArtworkSources(IArtistService artists, ILabelService labels, IArtworkService artwork,
    IEnumerable<IReleaseArtworkProvider> configured) : INamedArtworkSources
{
    private readonly BeatportArtworkProvider? _provider = configured.OfType<BeatportArtworkProvider>().SingleOrDefault();

    private async ValueTask<ArtworkDetail?> Gallery(string kind, string id, CancellationToken ct) => kind switch
    {
        "artist" => await artwork.GetArtistAsync(id, ct),
        "label" => await artwork.GetLabelAsync(id, ct),
        _ => throw new ArgumentException("Unsupported artwork entity.", nameof(kind))
    };

    private async ValueTask<string[]> References(string kind, string id, CancellationToken ct)
    {
        if (kind == "artist")
        {
            var artist = await artists.GetAsync(id, ct) ?? throw new NamedArtworkNotFoundException();
            return artist.ExternalReferences.Where(r => r.Source == "beatport").Select(r => r.ExternalId).ToArray();
        }
        if (kind == "label")
        {
            var label = await labels.GetAsync(id, ct) ?? throw new NamedArtworkNotFoundException();
            return label.ExternalReferences.Where(r => r.Source == "beatport").Select(r => r.ExternalId).ToArray();
        }
        throw new ArgumentException("Unsupported artwork entity.", nameof(kind));
    }

    private async ValueTask<IReadOnlyList<ReleaseArtworkCandidate>> Candidates(string kind, string id, CancellationToken ct)
    {
        if (await Gallery(kind, id, ct) is null) throw new NamedArtworkNotFoundException();
        var references = await References(kind, id, ct);
        if (_provider is null) return [];
        try { return await _provider.FindNamedAsync(kind, id, references, ct); }
        catch (HttpRequestException ex) { throw new ReleaseArtworkUnavailableException("Artwork provider lookup failed. No artwork was attached.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new ReleaseArtworkUnavailableException("Artwork provider lookup timed out. No artwork was attached.", ex); }
    }

    public async ValueTask<ReleaseArtworkCandidatePage> FindAsync(string kind, string id, CancellationToken ct)
        => new(_provider is null ? [] : [_provider.Name], await Candidates(kind, id, ct));

    private async ValueTask<(ReleaseArtworkCandidate Candidate, ReleaseArtworkCandidateContent Content)> Read(
        string kind, string id, string provider, string candidateId, string revision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 100 || string.IsNullOrWhiteSpace(candidateId)
            || candidateId.Length > 200 || string.IsNullOrWhiteSpace(revision) || revision.Length > 200)
            throw new ArgumentException("Invalid artwork candidate identifiers.");
        var candidates = await Candidates(kind, id, ct);
        var adapter = _provider is { } p && p.Name == provider ? p
            : throw new ReleaseArtworkUnavailableException("Artwork provider is not configured.");
        var candidate = candidates.SingleOrDefault(c => c.CandidateId == candidateId && c.Revision == revision)
            ?? throw new ReleaseArtworkUnavailableException("Artwork candidate changed. Look up candidates again.");
        var references = await References(kind, id, ct);
        try { return (candidate, await adapter.ReadNamedAsync(kind, id, references, candidate, ct)); }
        catch (HttpRequestException ex) { throw new ReleaseArtworkUnavailableException("Artwork provider download failed. No artwork was attached.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new ReleaseArtworkUnavailableException("Artwork provider download timed out. No artwork was attached.", ex); }
    }

    public async ValueTask<ReleaseArtworkCandidateContent> PreviewAsync(string kind, string id, string provider, string candidateId, string revision, CancellationToken ct)
        => (await Read(kind, id, provider, candidateId, revision, ct)).Content;

    public async ValueTask<ArtworkWriteResult> AttachAsync(string kind, string id, string version, string provider,
        string candidateId, string revision, bool preferred, CancellationToken ct)
    {
        var gallery = await Gallery(kind, id, ct);
        if (gallery is null) return new(LabelUpdateStatus.NotFound);
        if (gallery.Version != version) return new(LabelUpdateStatus.Stale);
        var (candidate, content) = await Read(kind, id, provider, candidateId, revision, ct);
        var upload = new ArtworkUpload(content.Bytes, content.Filename, candidate.SourceUrl, candidate.Attribution,
            "Not recorded", kind == "artist" ? "portrait" : "logo", candidate.Title, preferred);
        return kind == "artist" ? await artwork.UploadArtistAsync(id, version, upload, ct)
            : await artwork.UploadLabelAsync(id, version, upload, ct);
    }
}
