using System.Globalization;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Api.Beatport;

public sealed partial class BeatportEnrichmentProvider
{
    private static ApiRequestException InvalidTrack() => new(502, "beatport_invalid_response", "Beatport returned invalid Recording metadata.");

    private async ValueTask<EnrichmentLookup> FindRecordingAsync(EnrichmentQuery query, CancellationToken ct)
    {
        if (query.LookupEditionId is not null) throw new ArgumentException("Beatport Recordings have no edition selector.");
        var ids = query.LookupCandidateId is { } candidate ? [candidate]
            : query.ExternalReferences.Where(r => r.Source == "beatport").Select(r => r.ExternalId).Distinct().ToArray();
        if (ids.Length > 1) throw new ArgumentException("Choose one Beatport Recording identity.");
        var warnings = new List<string>();
        JsonElement[] tracks;
        EnrichmentMatchBasis basis;
        if (ids.Length == 1)
        {
            if (!long.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ArgumentException("Beatport track IDs must be positive integers.");
            var track = await broker.GetCatalogAsync($"/v4/catalog/tracks/{id}/", [], ct);
            if (track is null) return new("beatport", query, DateTimeOffset.UtcNow, [], []);
            if (TrackId(track.Value) != id) throw InvalidTrack();
            tracks = [track.Value];
            basis = EnrichmentMatchBasis.ExternalReference;
        }
        else
        {
            var parameters = new List<KeyValuePair<string, string>> { new("page", "1"), new("per_page", "10"), new("order_by", "name") };
            if (!string.IsNullOrWhiteSpace(query.Isrc))
            {
                parameters.Add(new("isrc", query.Isrc.Trim()));
                basis = EnrichmentMatchBasis.Isrc;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(query.Name)) throw new ArgumentException("A Recording title, ISRC or Beatport ID is required.");
                parameters.Add(new("name", query.Name.Trim()));
                // Compound/multiple Artist expressions are compared locally, not sent as an invented API filter.
                if (query.ArtistNames.Count == 1) parameters.Add(new("artists.name", query.ArtistNames[0]));
                if (!string.IsNullOrWhiteSpace(query.MixName)) parameters.Add(new("mix_name", query.MixName.Trim()));
                basis = EnrichmentMatchBasis.MetadataSearch;
            }
            var response = await broker.GetCatalogAsync("/v4/catalog/tracks/", parameters, ct);
            if (response is not { ValueKind: JsonValueKind.Object } page ||
                !page.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() > 10 || !page.TryGetProperty("count", out var count) ||
                count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var total) || total < results.GetArrayLength())
                throw InvalidTrack();
            tracks = results.EnumerateArray().ToArray();
            if (total > tracks.Length) warnings.Add("Only the first ten Recording candidates are shown; no further pages were fetched.");
        }
        var retrieved = DateTimeOffset.UtcNow;
        var candidates = tracks.Select(track => MapRecording(track, query, basis, retrieved)).OrderBy(c => c.Match.Conflicts.Count).ToArray();
        warnings.Add("One bounded broker call per lookup/review preparation. Duration is evidence only in this apply scope. Credits are not accepted Artist identities, and no release, artwork, ownership or media links are inferred.");
        return new("beatport", query, retrieved, candidates, warnings);
    }

    private static long TrackId(JsonElement track) =>
        track.ValueKind == JsonValueKind.Object && track.TryGetProperty("id", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0 ? id : throw InvalidTrack();

    private static string? TrackText(JsonElement track, string name)
    {
        if (track.ValueKind != JsonValueKind.Object) throw InvalidTrack();
        if (!track.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : throw InvalidTrack();
    }
    private static decimal? TrackNumber(JsonElement track, string name)
    {
        if (!track.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : throw InvalidTrack();
    }
    private static string? TrackNamedValue(JsonElement track, string name)
    {
        if (!track.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw InvalidTrack();
        return TrackText(value, "name");
    }
    private static IEnumerable<EnrichmentCredit> TrackCredits(JsonElement track, string field, string role)
    {
        if (!track.TryGetProperty(field, out var values) || values.ValueKind == JsonValueKind.Null) yield break;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 100) throw InvalidTrack();
        foreach (var value in values.EnumerateArray())
            yield return new(TrackText(value, "name") ?? throw InvalidTrack(), role,
                new("beatport", TrackId(value).ToString(CultureInfo.InvariantCulture)));
    }
    private static EnrichmentCandidate MapRecording(JsonElement track, EnrichmentQuery query,
        EnrichmentMatchBasis basis, DateTimeOffset retrieved)
    {
        var id = TrackId(track).ToString(CultureInfo.InvariantCulture);
        var title = TrackText(track, "name") ?? throw InvalidTrack();
        var mix = TrackText(track, "mix_name");
        var isrc = TrackText(track, "isrc");
        var bpm = TrackNumber(track, "bpm");
        if (bpm is <= 0 or > 1000) throw InvalidTrack();
        var duration = TrackNumber(track, "length_ms");
        if (duration is < 0 or > long.MaxValue || duration is { } ms && decimal.Truncate(ms) != ms) throw InvalidTrack();
        var key = TrackNamedValue(track, "key");
        var genre = TrackNamedValue(track, "genre");
        var subgenre = TrackNamedValue(track, "sub_genre");
        var credits = TrackCredits(track, "artists", "primary").Concat(TrackCredits(track, "remixers", "remixer")).ToArray();
        var conflicts = new List<string>();
        static bool Same(string? a, string? b) => a is not null && b is not null &&
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        if (query.Name is not null && !Same(query.Name, title)) conflicts.Add("Returned title differs from the catalog Recording.");
        if (query.MixName is not null && !Same(query.MixName, mix)) conflicts.Add("Returned mix differs from or is missing relative to the catalog version.");
        if (query.Isrc is not null && !Same(query.Isrc, isrc)) conflicts.Add("Returned ISRC differs from or is missing relative to the catalog ISRC.");
        if (query.ArtistNames.Count > 0 && !credits.Any(c => c.Role == "primary" && query.ArtistNames.Any(n => Same(n, c.Name))))
            conflicts.Add("Returned primary Artists differ from or are missing relative to the catalog Recording.");
        var metadata = new EnrichmentMetadata(Name: title, MixName: mix, Isrc: isrc,
            DurationMs: duration is null ? null : (long)duration.Value, Bpm: bpm,
            MusicalKey: key is null ? null : new(key, "Musical"), Genre: genre,
            Genres: genre is null ? [] : [genre], Subgenres: subgenre is null ? [] : [subgenre],
            ExternalReferences: [new("beatport", id)], Credits: credits);
        var fields = new List<EnrichmentField> { EnrichmentField.Name, EnrichmentField.ExternalReferences };
        if (mix is not null) fields.Add(EnrichmentField.MixName);
        if (isrc is not null) fields.Add(EnrichmentField.Isrc);
        if (duration is not null) fields.Add(EnrichmentField.DurationMs);
        if (bpm is not null) fields.Add(EnrichmentField.Bpm);
        if (key is not null) fields.Add(EnrichmentField.MusicalKey);
        if (genre is not null) fields.Add(EnrichmentField.Genre);
        if (subgenre is not null) fields.Add(EnrichmentField.Subgenre);
        if (credits.Length > 0) fields.Add(EnrichmentField.Credits);
        return new("beatport", id, null, null, EnrichmentEntityKind.Recording,
            new(basis, "Beatport track candidate; a provider track ID or ISRC match is not proof of canonical Recording/version identity.", conflicts),
            metadata, fields.Select(field => new EnrichmentFieldEvidence(field, "beatport", id,
                new Uri($"https://www.beatport.com/track/-/{id}"), retrieved, null)).ToArray());
    }
}
