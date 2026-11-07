using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TrackStash.Catalog.Application;

/// <summary>A safe provider failure; upstream response bodies and credentials are never included.</summary>
public sealed class SonoVaultLookupException(string code, string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Read-only Recording lookups against the official SonoVault API, with no retries or catalog writes.</summary>
public sealed class SonoVaultEnrichmentProvider(HttpClient http, string? apiKey) : IEnrichmentProvider
{
    public const string HttpClientName = "sonovault-enrichment";
    private const int MaximumResponseBytes = 1024 * 1024;
    public EnrichmentProviderDescriptor Descriptor { get; } = new("sonovault", "SonoVault",
        Array.AsReadOnly(new[] { EnrichmentEntityKind.Recording }));

    public async ValueTask<EnrichmentLookup> FindAsync(EnrichmentQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (query.EntityKind != EnrichmentEntityKind.Recording)
            throw new NotSupportedException("SonoVault enrichment currently supports Recordings only.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new SonoVaultLookupException("not_configured", "Set CatalogApi__SonoVaultApiKey on the catalog server.");
        var ids = query.ExternalReferences.Where(r => r.Source == "sonovault").Select(r => r.ExternalId).Distinct().ToArray();
        if (ids.Length > 1) throw new ArgumentException("Choose one SonoVault Recording identity before lookup.");
        string path;
        EnrichmentMatchBasis basis;
        if (ids.Length == 1)
        {
            if (!long.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ArgumentException("The SonoVault Recording ID must be a positive integer.");
            path = $"/v1/tracks/{id}";
            basis = EnrichmentMatchBasis.ExternalReference;
        }
        else if (!string.IsNullOrWhiteSpace(query.Isrc))
        {
            path = $"/v1/tracks/isrc/{Uri.EscapeDataString(query.Isrc.Trim())}";
            basis = EnrichmentMatchBasis.Isrc;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(query.Name) || query.ArtistNames.Count == 0 || query.ArtistNames.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("An ISRC, SonoVault ID, or artist and title are required for lookup.");
            var title = query.Name + (string.IsNullOrWhiteSpace(query.MixName) ? "" : $" ({query.MixName})");
            path = $"/v1/tracks/search?artist={Uri.EscapeDataString(string.Join(", ", query.ArtistNames))}&title={Uri.EscapeDataString(title)}&limit=10";
            basis = EnrichmentMatchBasis.MetadataSearch;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.sonovault.now" + path));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var retrieved = DateTimeOffset.UtcNow;
            if (response.StatusCode == HttpStatusCode.NotFound && basis != EnrichmentMatchBasis.MetadataSearch)
                return new("sonovault", query, retrieved, [], []);
            if (!response.IsSuccessStatusCode)
                throw new SonoVaultLookupException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "authentication_failed",
                    HttpStatusCode.Forbidden => "access_denied",
                    HttpStatusCode.TooManyRequests => "rate_limited",
                    _ => "upstream_failed"
                }, $"SonoVault lookup failed (HTTP {(int)response.StatusCode}). No automatic retry.", response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new SonoVaultLookupException("invalid_response", "SonoVault response exceeds the lookup size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new SonoVaultLookupException("invalid_response", "SonoVault response exceeds the lookup size limit.");
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            var warnings = new List<string>();
            var tracks = basis == EnrichmentMatchBasis.MetadataSearch
                ? root.GetProperty("results").EnumerateArray().ToArray() : [root];
            if (tracks.Length > 10)
                throw new SonoVaultLookupException("invalid_response", "SonoVault returned more candidates than requested.");
            if (basis == EnrichmentMatchBasis.MetadataSearch && Text(root, "next_cursor") is not null)
                warnings.Add("More search results exist. Only the first page was fetched to conserve quota.");
            var candidates = tracks.Select(track => Map(track, query, basis, retrieved)).ToArray();
            warnings.Add("Duration units are not verified and release-specific metadata is not proposed. Mix names are only separated when a trailing parenthesized suffix matches the catalog title and recorded mix.");
            return new("sonovault", query, retrieved, candidates, warnings);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new SonoVaultLookupException("timeout", "SonoVault lookup timed out. No automatic retry.", inner: ex); }
        catch (HttpRequestException ex)
        { throw new SonoVaultLookupException("unavailable", "SonoVault could not be reached. No automatic retry.", inner: ex); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid Recording response.", inner: ex); }
    }

    private static EnrichmentCandidate Map(JsonElement track, EnrichmentQuery query, EnrichmentMatchBasis basis, DateTimeOffset retrieved)
    {
        var id = track.GetProperty("id").GetInt64();
        var title = Text(track, "title");
        if (id <= 0 || string.IsNullOrWhiteSpace(title))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid Recording identity.");
        var rawTitle = title;
        string? mixName = null;
        if (!string.IsNullOrWhiteSpace(query.Name) && !string.IsNullOrWhiteSpace(query.MixName))
        {
            var suffix = $" ({query.MixName.Trim()})";
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var mainTitle = title[..^suffix.Length].TrimEnd();
                if (string.Equals(mainTitle, query.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    title = mainTitle;
                    mixName = rawTitle[(rawTitle.Length - suffix.Length + 2)..^1];
                }
            }
        }
        var isrc = Text(track, "isrc");
        var bpm = track.TryGetProperty("bpm", out var tempo) && tempo.ValueKind != JsonValueKind.Null ? tempo.GetDecimal() : (decimal?)null;
        if (bpm <= 0) throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid tempo.");
        var key = Text(track, "key_alphanumeric");
        var genres = Classifications(track, "genre");
        var subgenres = Classifications(track, "subgenre");
        var credits = track.GetProperty("artists").EnumerateArray().Select(a =>
        {
            var artistId = a.GetProperty("id").GetInt64();
            var name = Text(a, "name");
            if (artistId <= 0 || string.IsNullOrWhiteSpace(name))
                throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid Artist credit.");
            return new EnrichmentCredit(name, a.TryGetProperty("is_remixer", out var remixer) && remixer.GetBoolean() ? "remixer"
                : a.TryGetProperty("is_primary", out var primary) && primary.GetBoolean() ? "primary" : "artist",
                new("sonovault", artistId.ToString(CultureInfo.InvariantCulture)));
        }).ToArray();
        var conflicts = new List<string>();
        if (query.Isrc is not null && !string.Equals(query.Isrc.Trim(), isrc, StringComparison.OrdinalIgnoreCase))
            conflicts.Add("Returned ISRC differs from or is missing relative to the catalog ISRC.");
        if (query.Name is not null && !string.Equals(query.Name.Trim(), title, StringComparison.OrdinalIgnoreCase))
            conflicts.Add("Returned title differs from the catalog title; review recording/version identity.");
        if (!string.IsNullOrWhiteSpace(query.MixName) && mixName is null)
            conflicts.Add("The response has no separately verified mix-name field; review the requested version.");
        var reference = new EnrichmentReference("sonovault", id.ToString(CultureInfo.InvariantCulture));
        var metadata = new EnrichmentMetadata(Name: title, MixName: mixName, Isrc: isrc, Bpm: bpm,
            MusicalKey: key is null ? null : new(key, "Camelot"),
            Genre: genres.Length == 1 ? genres[0] : null, ExternalReferences: [reference], Credits: credits,
            Genres: genres, Subgenres: subgenres);
        var fields = new List<EnrichmentField> { EnrichmentField.Name, EnrichmentField.ExternalReferences };
        if (mixName is not null) fields.Add(EnrichmentField.MixName);
        if (isrc is not null) fields.Add(EnrichmentField.Isrc);
        if (bpm is not null) fields.Add(EnrichmentField.Bpm);
        if (key is not null) fields.Add(EnrichmentField.MusicalKey);
        if (genres.Length > 0) fields.Add(EnrichmentField.Genre);
        if (subgenres.Length > 0) fields.Add(EnrichmentField.Subgenre);
        if (credits.Length > 0) fields.Add(EnrichmentField.Credits);
        return new("sonovault", reference.ExternalId, null, null, EnrichmentEntityKind.Recording,
            new(basis, "SonoVault lookup candidate; the lookup basis is not proof of canonical identity.", conflicts),
            metadata, fields.Select(field => new EnrichmentFieldEvidence(field, "sonovault", reference.ExternalId,
                null, retrieved, mixName is not null && (field is EnrichmentField.Name or EnrichmentField.MixName)
                    ? $"Separated from SonoVault title \"{rawTitle}\" using the catalog's matching title and mix suffix; not an independent mix-name field."
                    : null)).ToArray());
    }

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind != JsonValueKind.Null ? field.GetString() : null;

    private static string[] Classifications(JsonElement track, string property)
    {
        if (!track.TryGetProperty(property, out var values) || values.ValueKind == JsonValueKind.Null) return [];
        return values.EnumerateArray().Select(value =>
        {
            var name = value.GetString();
            return string.IsNullOrWhiteSpace(name)
                ? throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid genre classification.")
                : name;
        }).ToArray();
    }
}
