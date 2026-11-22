using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api.Beatport;

/// <summary>Artwork for explicitly linked Beatport entities, downloaded from the approved media host.</summary>
public sealed class BeatportArtworkProvider(IBeatportBrokerClient broker, HttpClient http) : IReleaseArtworkProvider
{
    public const string HttpClientName = "beatport-artwork";
    public string Name => "beatport";

    private static long? ProviderId(IEnumerable<string> references, string kind)
    {
        var ids = references.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return null;
        if (ids.Length != 1 || !long.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ReleaseArtworkUnavailableException($"Artwork lookup requires one valid linked Beatport {kind} ID.");
        return id;
    }

    private static string? Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<(ReleaseArtworkCandidate Candidate, Uri Image)?> Resolve(
        string kind, string catalogId, IEnumerable<string> references, CancellationToken ct)
    {
        var id = ProviderId(references, kind);
        if (id is null) return null;
        var row = await broker.GetCatalogAsync($"/v4/catalog/{kind}s/{id}/", [], ct);
        if (row is null) return null;
        var release = row.Value;
        if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("id", out var identity)
            || identity.ValueKind != JsonValueKind.Number || !identity.TryGetInt64(out var returned) || returned != id)
            throw new ReleaseArtworkUnavailableException($"Beatport returned a different {kind} identity.");
        if (!release.TryGetProperty("image", out var image) || image.ValueKind == JsonValueKind.Null) return null;
        var raw = Text(image, "dynamic_uri")?.Replace("{w}x{h}", "1400x1400", StringComparison.Ordinal) ?? Text(image, "uri");
        if (raw is null) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.Equals(uri.Host, "geo-media.beatport.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ReleaseArtworkUnavailableException("Beatport artwork URL is outside the approved media host.");
        var title = Text(release, "name");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 500)
            throw new ReleaseArtworkUnavailableException($"Beatport returned an invalid {kind} name.");
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri + "\n" + release.GetRawText())));
        var candidateId = $"{catalogId}:{id}";
        var type = char.ToUpperInvariant(kind[0]) + kind[1..];
        return (new(Name, candidateId, revision, title, $"https://www.beatport.com/{kind}/-/{id}",
            $"{type} artwork supplied by Beatport ({type} {id}); usage rights require explicit review."), uri);
    }

    public async ValueTask<IReadOnlyList<ReleaseArtworkCandidate>> FindAsync(ReleaseArtworkSourceQuery query, CancellationToken ct = default)
    {
        var resolved = await Resolve("release", query.ReleaseId, query.ExternalReferences.Where(r => r.Source == Name).Select(r => r.ExternalId), ct);
        return resolved is { } item ? [item.Candidate] : [];
    }

    public async ValueTask<ReleaseArtworkCandidateContent> ReadAsync(ReleaseArtworkSourceQuery query, ReleaseArtworkCandidate candidate, CancellationToken ct = default)
    {
        var references = query.ExternalReferences.Where(r => r.Source == Name).Select(r => r.ExternalId).ToArray();
        var resolved = await Resolve("release", query.ReleaseId, references, ct);
        return await DownloadAsync(resolved, candidate, ct);
    }

    public async ValueTask<IReadOnlyList<ReleaseArtworkCandidate>> FindNamedAsync(
        string kind, string catalogId, IReadOnlyList<string> references, CancellationToken ct = default)
    {
        if (kind is not ("artist" or "label")) throw new ArgumentException("Unsupported artwork entity.", nameof(kind));
        var resolved = await Resolve(kind, catalogId, references, ct);
        return resolved is { } item ? [item.Candidate] : [];
    }

    public async ValueTask<ReleaseArtworkCandidateContent> ReadNamedAsync(string kind, string catalogId,
        IReadOnlyList<string> references, ReleaseArtworkCandidate candidate, CancellationToken ct = default)
    {
        if (kind is not ("artist" or "label")) throw new ArgumentException("Unsupported artwork entity.", nameof(kind));
        return await DownloadAsync(await Resolve(kind, catalogId, references, ct), candidate, ct);
    }

    private async ValueTask<ReleaseArtworkCandidateContent> DownloadAsync(
        (ReleaseArtworkCandidate Candidate, Uri Image)? resolved, ReleaseArtworkCandidate candidate, CancellationToken ct)
    {
        if (resolved is null || resolved.Value.Candidate != candidate)
            throw new ReleaseArtworkUnavailableException("Beatport artwork candidate changed. Look up candidates again.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(resolved.Value.Image, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
            throw new ReleaseArtworkUnavailableException($"Beatport artwork download failed (HTTP {(int)response.StatusCode}).");
        if (response.Content.Headers.ContentLength > ArtworkRules.MaximumBytes)
            throw new ReleaseArtworkUnavailableException("Beatport artwork exceeds 10 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, deadline.Token)) > 0)
        {
            if (buffer.Length + count > ArtworkRules.MaximumBytes)
                throw new ReleaseArtworkUnavailableException("Beatport artwork exceeds 10 MiB.");
            buffer.Write(bytes, 0, count);
        }
        return new(buffer.ToArray(), $"beatport-{candidate.CandidateId.Split(':').Last()}.jpg");
    }
}
