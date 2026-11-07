using System.Globalization;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api.Beatport;

/// <summary>A Beatport artist or label reference.</summary>
public sealed record BeatportRef(long Id, string Name);

/// <summary>A Beatport release with how it relates to this catalog and the purchase history.</summary>
/// <remarks><c>InPurchaseHistory</c> and <c>PurchaseSourceComplete</c> are null when the purchase source is not configured.</remarks>
public sealed record BeatportReleaseItem(long Id, string Name, string? Type, IReadOnlyList<BeatportRef> Artists, IReadOnlyList<BeatportRef> Remixers,
    BeatportRef? Label, IReadOnlyList<string> CatalogLabelIds, string? CatalogNumber, string? PublishDate, int? TrackCount, string? ImageUrl,
    string BeatportUrl, IReadOnlyList<string> CatalogReleaseIds, bool? InPurchaseHistory, bool? PurchaseSourceComplete);

/// <summary>One page of Beatport releases.</summary>
public sealed record BeatportReleasePage(IReadOnlyList<BeatportReleaseItem> Items, int Total, int Page, int PageSize);

/// <summary>A Beatport track as listed on a release.</summary>
public sealed record BeatportTrackItem(long Id, int? Number, string Name, string? MixName, IReadOnlyList<BeatportRef> Artists,
    IReadOnlyList<BeatportRef> Remixers, string? Isrc, int? LengthMs, int? Bpm, string? Key, string? Genre, string BeatportUrl,
    EnrichmentCandidate? RecordingCandidate = null);

/// <summary>A Beatport release with its tracks.</summary>
public sealed record BeatportReleaseDetail(BeatportReleaseItem Release, IReadOnlyList<BeatportTrackItem> Tracks, int TrackTotal);

/// <summary>A Beatport label with the catalog labels that already reference it.</summary>
public sealed record BeatportLabelItem(long Id, string Name, string? ImageUrl, string BeatportUrl, IReadOnlyList<string> CatalogLabelIds);

/// <summary>One page of Beatport labels.</summary>
public sealed record BeatportLabelPage(IReadOnlyList<BeatportLabelItem> Items, int Total, int Page, int PageSize);

/// <summary>Release list filters; all are optional, and with none Beatport lists every release in the chosen order.</summary>
public sealed record BeatportReleaseSearch(string? Query, long? LabelId, long? ArtistId, string Sort, int Page, int PageSize);

public interface IBeatportBrowseService
{
    Task<BeatportReleasePage> SearchReleasesAsync(BeatportReleaseSearch search, CancellationToken ct);
    Task<BeatportReleaseDetail> GetReleaseAsync(long id, CancellationToken ct, bool requireEnrichmentEvidence = false);
    Task<BeatportLabelPage> SearchLabelsAsync(string query, int page, int pageSize, CancellationToken ct);
    Task<BeatportLabelItem> GetLabelAsync(long id, CancellationToken ct);
}

/// <summary>Read-only Beatport catalog browsing through the broker, annotated with catalog and purchase-history status.</summary>
/// <remarks>Responses are reduced to the fields the GUI needs; raw Beatport JSON is never passed through.</remarks>
public sealed class BeatportBrowseService(IBeatportBrokerClient broker, IStorageProvider storage, PurchaseReadinessCache readiness) : IBeatportBrowseService
{
    public static readonly IReadOnlyDictionary<string, string> Sorts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["newest"] = "-publish_date", ["oldest"] = "publish_date", ["name"] = "name",
    };

    public async Task<BeatportReleasePage> SearchReleasesAsync(BeatportReleaseSearch search, CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("page", search.Page.ToString(CultureInfo.InvariantCulture)),
            new("per_page", search.PageSize.ToString(CultureInfo.InvariantCulture)),
            new("order_by", Sorts[search.Sort]),
        };
        if (!string.IsNullOrWhiteSpace(search.Query)) query.Add(new("name", search.Query.Trim()));
        if (search.LabelId is { } label) query.Add(new("label_id", label.ToString(CultureInfo.InvariantCulture)));
        if (search.ArtistId is { } artist) query.Add(new("artist_id", artist.ToString(CultureInfo.InvariantCulture)));
        var list = await broker.GetCatalogAsync("/v4/catalog/releases/", query, ct) ?? throw NotFound("Beatport release search");
        var results = Results(list);
        var items = await AnnotateAsync(results, ct);
        return new(items, Count(list, items.Count), search.Page, search.PageSize);
    }

    public async Task<BeatportReleaseDetail> GetReleaseAsync(long id, CancellationToken ct, bool requireEnrichmentEvidence = false)
    {
        var release = await broker.GetCatalogAsync($"/v4/catalog/releases/{id}/", [], ct)
            ?? throw new ApiRequestException(404, "beatport_release_not_found", "Beatport has no release with that ID.");
        if (requireEnrichmentEvidence)
        {
            if (Long(release, "id") != id || string.IsNullOrWhiteSpace(Text(release, "name")))
                throw new ApiRequestException(502, "beatport_invalid_response", "Beatport returned an invalid Release identity or title.");
            foreach (var field in new[] { "catalog_number", "publish_date" })
                if (release.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new ApiRequestException(502, "beatport_invalid_response", "Beatport returned invalid Release metadata.");
        }
        var item = (await AnnotateAsync([release], ct))[0];
        var tracks = await broker.GetCatalogAsync("/v4/catalog/tracks/", [new("release_id", id.ToString(CultureInfo.InvariantCulture)), new("per_page", "100")], ct);
        if (requireEnrichmentEvidence && (tracks is not { ValueKind: JsonValueKind.Object } trackPage ||
            !trackPage.TryGetProperty("results", out var trackResults) || trackResults.ValueKind != JsonValueKind.Array ||
            trackResults.GetArrayLength() > 100 || !trackPage.TryGetProperty("count", out var total) ||
            total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out var trackCount) || trackCount < trackResults.GetArrayLength()))
            throw new ApiRequestException(502, "beatport_invalid_response", "Beatport returned invalid Release tracklist evidence.");
        var trackItems = tracks is { } t ? Results(t).Select(track =>
            {
                var item = Track(track);
                if (!requireEnrichmentEvidence) return item;
                var query = new EnrichmentQuery(EnrichmentEntityKind.Recording, "", "", null, null, [], null, null, null, null, null, []);
                var evidence = BeatportEnrichmentProvider.MapRecording(track, query, EnrichmentMatchBasis.ExternalReference, DateTimeOffset.UtcNow);
                return item! with { RecordingCandidate = evidence };
            }).OfType<BeatportTrackItem>()
            .OrderBy(track => track.Number ?? int.MaxValue).ThenBy(track => track.Id).ToList() : [];
        return new(item, trackItems, tracks is { } all ? Count(all, trackItems.Count) : 0);
    }

    public async Task<BeatportLabelPage> SearchLabelsAsync(string query, int page, int pageSize, CancellationToken ct)
    {
        var list = await broker.GetCatalogAsync("/v4/catalog/labels/", [
            new("name", query.Trim()), new("page", page.ToString(CultureInfo.InvariantCulture)), new("per_page", pageSize.ToString(CultureInfo.InvariantCulture)),
        ], ct) ?? throw NotFound("Beatport label search");
        var labels = Results(list).Select(Label).OfType<BeatportRefWithImage>().ToList();
        var catalog = await CatalogLabelsAsync(labels.Select(label => label.Ref.Id), ct);
        return new(labels.Select(label => LabelItem(label, catalog)).ToList(), Count(list, labels.Count), page, pageSize);
    }

    public async Task<BeatportLabelItem> GetLabelAsync(long id, CancellationToken ct)
    {
        var json = await broker.GetCatalogAsync($"/v4/catalog/labels/{id}/", [], ct);
        var label = json is { } j ? Label(j) : null;
        if (label is null) throw new ApiRequestException(404, "beatport_label_not_found", "Beatport has no label with that ID.");
        return LabelItem(label, await CatalogLabelsAsync([label.Ref.Id], ct));
    }

    private static ApiRequestException NotFound(string what) => new(502, "beatport_upstream_error", $"{what} returned no results page.");

    private sealed record BeatportRefWithImage(BeatportRef Ref, string Slug, string? ImageUrl);

    private static BeatportLabelItem LabelItem(BeatportRefWithImage label, IReadOnlyDictionary<long, IReadOnlyList<string>> catalog) =>
        new(label.Ref.Id, label.Ref.Name, label.ImageUrl, $"https://www.beatport.com/label/{label.Slug}/{label.Ref.Id}",
            catalog.GetValueOrDefault(label.Ref.Id) ?? []);

    private async Task<IReadOnlyList<BeatportReleaseItem>> AnnotateAsync(IReadOnlyList<JsonElement> releases, CancellationToken ct)
    {
        var parsed = releases.Select(release => (Json: release, Id: Long(release, "id"))).Where(r => r.Id is > 0).ToList();
        IReadOnlyDictionary<long, PurchaseSourceSummary>? purchases;
        try { purchases = await readiness.GetAsync(ct); }
        catch (PurchaseSourceUnavailableException) { purchases = null; }
        var labelIds = parsed.Select(r => Ref(r.Json, "label")?.Id).OfType<long>().Distinct().ToList();
        var catalogLabels = await CatalogLabelsAsync(labelIds, ct);
        var catalogReleases = new Dictionary<long, IReadOnlyList<string>>();
        if (await storage.Migrations.GetCurrentVersionAsync(ct) >= 9)
        {
            await using var uow = await storage.BeginUnitOfWorkAsync(ct);
            if (uow.CatalogFoundations is { } foundations)
                foreach (var (_, id) in parsed)
                    catalogReleases[id!.Value] = await foundations.FindReferenceOwnersAsync(CatalogEntityKind.Release, "beatport",
                        id.Value.ToString(CultureInfo.InvariantCulture), ct);
        }
        return parsed.Select(r =>
        {
            var id = r.Id!.Value;
            var label = Ref(r.Json, "label");
            PurchaseSourceSummary? purchase = purchases?.GetValueOrDefault(id);
            return new BeatportReleaseItem(id, Text(r.Json, "name") ?? $"Release {id}", Nested(r.Json, "type", "name"), Refs(r.Json, "artists"), Refs(r.Json, "remixers"),
                label, label is { } l ? catalogLabels.GetValueOrDefault(l.Id) ?? [] : [], Text(r.Json, "catalog_number"),
                Text(r.Json, "publish_date") ?? Text(r.Json, "new_release_date"), Int(r.Json, "track_count"), Image(r.Json),
                $"https://www.beatport.com/release/{Slug(r.Json)}/{id}", catalogReleases.GetValueOrDefault(id) ?? [],
                purchases is null ? null : purchase is not null, purchases is null ? null : purchase?.SourceComplete);
        }).ToList();
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> CatalogLabelsAsync(IEnumerable<long> beatportIds, CancellationToken ct)
    {
        var result = new Dictionary<long, IReadOnlyList<string>>();
        var ids = beatportIds.Distinct().ToList();
        if (ids.Count == 0) return result;
        await using var uow = await storage.BeginUnitOfWorkAsync(ct);
        foreach (var id in ids)
        {
            var page = await uow.Labels.QueryAsync(new LabelQuery { Source = "beatport", ExternalId = id.ToString(CultureInfo.InvariantCulture), PageSize = 10 }, ct);
            result[id] = page.Items.Select(item => item.Id).ToList();
        }
        return result;
    }

    private static BeatportTrackItem? Track(JsonElement track)
    {
        if (Long(track, "id") is not ({ } id and > 0)) return null;
        return new(id, Int(track, "number"), Text(track, "name") ?? $"Track {id}", Text(track, "mix_name"), Refs(track, "artists"), Refs(track, "remixers"),
            Text(track, "isrc"), Int(track, "length_ms"), Int(track, "bpm"), Nested(track, "key", "name"), Nested(track, "genre", "name"),
            $"https://www.beatport.com/track/{Slug(track)}/{id}");
    }

    private static BeatportRefWithImage? Label(JsonElement label) =>
        Long(label, "id") is { } id and > 0 ? new(new(id, Text(label, "name") ?? $"Label {id}"), Slug(label), Image(label)) : null;

    private static IReadOnlyList<JsonElement> Results(JsonElement list) =>
        list.ValueKind == JsonValueKind.Object && list.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList() : [];

    private static int Count(JsonElement list, int fallback) => Int(list, "count") ?? fallback;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string? Nested(JsonElement e, string name, string field) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? Text(v, field) : null;

    private static BeatportRef? Ref(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && Long(v, "id") is { } id and > 0 ? new(id, Text(v, "name") ?? $"{id}") : null;

    private static IReadOnlyList<BeatportRef> Refs(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(a => Long(a, "id") is { } id and > 0 ? new BeatportRef(id, Text(a, "name") ?? $"{id}") : null).OfType<BeatportRef>().ToList()
            : [];

    private static string Slug(JsonElement e) =>
        Text(e, "slug") is { } slug && slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? slug : "-";

    /// <summary>A small cover image on Beatport's own media host; anything else is dropped.</summary>
    private static string? Image(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("image", out var image)) return null;
        var url = Text(image, "dynamic_uri")?.Replace("{w}x{h}", "250x250", StringComparison.Ordinal) ?? Text(image, "uri");
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host == "beatport.com" || uri.Host.EndsWith(".beatport.com", StringComparison.OrdinalIgnoreCase)) ? uri.AbsoluteUri : null;
    }
}
