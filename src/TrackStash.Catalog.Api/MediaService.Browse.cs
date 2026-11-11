using Microsoft.Data.Sqlite;

namespace TrackStash.Catalog.Api;

/// <summary>Literal embedded-metadata filters, deterministic sorting and selectable grouping.</summary>
public sealed record MediaBrowseOptions(string? Label = null, string? Release = null, string? Artist = null,
    string? Track = null, string Sort = "title", string Direction = "asc", string GroupBy = "none", string? GroupKey = null,
    string? Classification = null, int PageSize = 25);
/// <summary>A whole-inventory metadata group and recorded locations, not verified backup coverage.</summary>
public sealed record MediaGroup(string Key, string Name, string? Label, string? Release, int FileCount,
    IReadOnlyList<string> Libraries, bool CanAssociateRelease, string? AssociatedReleaseId = null, string? AssociatedReleaseTitle = null);

public sealed partial class MediaService
{
    private static async Task<HashSet<string>> Columns(SqliteConnection connection, string table, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        return columns;
    }
    private static string GroupText(string? value) => value?.Trim().ToUpperInvariant() ?? "";
    private string GroupKey(MediaInventoryItem item, string groupBy) => Hash(new {
        SourceId, GroupBy = groupBy,
        Parts = groupBy switch {
            "label" => new[] { GroupText(item.Label) },
            "release" => new[] { GroupText(item.Label), GroupText(item.Release), string.IsNullOrWhiteSpace(item.Release) ? item.Id.ToString() : "" },
            "artist" => new[] { GroupText(item.Artist) },
            "track" => new[] { GroupText(item.Artist), GroupText(item.Title), GroupText(item.Mix), GroupText(item.Isrc),
                string.IsNullOrWhiteSpace(item.Title) ? item.Id.ToString() : "" },
            "fingerprint" => new[] { item.FingerprintKey ?? $"missing:{item.Id}" },
            _ => throw Error(400, "validation_failed", "Unsupported media grouping.")
        }
    });
    private static string GroupName(MediaInventoryItem item, string groupBy) => groupBy switch {
        "label" => item.Label ?? "Unknown Label",
        "release" => item.Release ?? "Unknown Release",
        "artist" => item.Artist ?? "Unknown Artist",
        "track" => $"{item.Title ?? "Unknown Track"} {item.Mix}".Trim(),
        "fingerprint" => item.FingerprintKey is null ? $"{item.Title ?? "Track"} (no fingerprint)" : $"{item.Title ?? "Audio"} (exact fingerprint)",
        _ => "Media"
    };
    private (MediaInventoryItem[] Items, int Total, MediaGroup[] Groups, int GroupTotal) Browse(
        List<MediaInventoryItem> rows, string? library, string? search, int page, MediaBrowseOptions options)
    {
        if (options.PageSize is not (25 or 50 or 100) || options.Sort is not ("title" or "label" or "release" or "artist" or "library") ||
            options.Direction is not ("asc" or "desc") ||
            options.GroupBy is not ("none" or "label" or "release" or "artist" or "track" or "fingerprint") ||
            new[] { options.Label, options.Release, options.Artist, options.Track, options.GroupKey }.Any(v => v?.Length > 200) ||
            options.GroupKey is not null && options.GroupBy == "none")
            throw Error(400, "validation_failed", "Invalid media filters, sort, direction or grouping.");
        static bool Contains(string? value, string? query) => string.IsNullOrWhiteSpace(query) ||
            value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        var filtered = rows.Where(r => (library is null || r.Library == library) &&
            (string.IsNullOrWhiteSpace(search) || new[] { r.Title, r.Artist, r.Label, r.Release, r.Isrc }.Any(v => Contains(v, search))) &&
            Contains(r.Label, options.Label) && Contains(r.Release, options.Release) &&
            Contains(r.Artist, options.Artist) && Contains(r.Title, options.Track) &&
            (options.GroupKey is null || GroupKey(r, options.GroupBy) == options.GroupKey)).ToArray();
        var offset = (int)Math.Min(((long)page - 1) * options.PageSize, int.MaxValue);
        string Sort(MediaInventoryItem item) => options.Sort switch {
            "label" => item.Label ?? "", "release" => item.Release ?? "", "artist" => item.Artist ?? "",
            "library" => Libraries.First(l => l.Id == item.Library).Name, _ => item.Title ?? ""
        };
        var ordered = options.Direction == "asc" ? filtered.OrderBy(Sort, StringComparer.OrdinalIgnoreCase)
            : filtered.OrderByDescending(Sort, StringComparer.OrdinalIgnoreCase);
        if (options.GroupBy == "none" || options.GroupKey is not null)
            return (ordered.ThenBy(r => r.Id).Skip(offset).Take(options.PageSize).ToArray(), filtered.Length, [], 0);
        var groups = ordered.ThenBy(r => r.Id).GroupBy(r => GroupKey(r, options.GroupBy)).Select(g => {
            var first = g.First();
            return new MediaGroup(g.Key, GroupName(first, options.GroupBy), first.Label, first.Release, g.Count(),
                g.Select(r => r.Library).Distinct().Order(StringComparer.Ordinal).ToArray(),
                options.GroupBy == "release" && !string.IsNullOrWhiteSpace(first.Release));
        }).ToArray();
        return ([], filtered.Length, groups.Skip(offset).Take(options.PageSize).ToArray(), groups.Length);
    }
}
