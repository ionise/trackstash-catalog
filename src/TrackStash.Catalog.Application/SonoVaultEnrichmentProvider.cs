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

/// <summary>Read-only metadata lookups against the official SonoVault API, with no retries or catalog writes.</summary>
public sealed class SonoVaultEnrichmentProvider(HttpClient http, string? apiKey) : IEnrichmentProvider
{
    public const string HttpClientName = "sonovault-enrichment";
    private const int MaximumResponseBytes = 1024 * 1024;
    public EnrichmentProviderDescriptor Descriptor { get; } = new("sonovault", "SonoVault",
        Array.AsReadOnly(new[] { EnrichmentEntityKind.Recording, EnrichmentEntityKind.Artist, EnrichmentEntityKind.Label, EnrichmentEntityKind.Release }));

    public async ValueTask<EnrichmentLookup> FindAsync(EnrichmentQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resource = query.EntityKind switch
        {
            EnrichmentEntityKind.Recording => "tracks",
            EnrichmentEntityKind.Artist => "artists",
            EnrichmentEntityKind.Label => "labels",
            EnrichmentEntityKind.Release => "releases",
            _ => throw new NotSupportedException("SonoVault enrichment does not support this entity kind.")
        };
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new SonoVaultLookupException("not_configured", "Set CatalogApi__SonoVaultApiKey on the catalog server.");
        var ids = query.ExternalReferences.Where(r => r.Source == "sonovault").Select(r => r.ExternalId).Distinct().ToArray();
        if (query.LookupCandidateId is not null)
        {
            if (query.EntityKind is not (EnrichmentEntityKind.Release or EnrichmentEntityKind.Recording))
                throw new ArgumentException("Candidate selection is only supported for Releases and Recordings.");
            ids = [query.LookupCandidateId];
        }
        if (ids.Length > 1) throw new ArgumentException("Choose one SonoVault entity identity before lookup.");
        if (query.LookupEditionId is not null && (query.EntityKind != EnrichmentEntityKind.Release || ids.Length != 1))
            throw new ArgumentException("An edition lookup requires one SonoVault Release ID.");
        string path;
        EnrichmentMatchBasis basis;
        if (ids.Length == 1)
        {
            if (!long.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ArgumentException("The SonoVault entity ID must be a positive integer.");
            path = $"/v1/{resource}/{id}";
            if (query.LookupEditionId is { } editionId)
            {
                if (!long.TryParse(editionId, NumberStyles.None, CultureInfo.InvariantCulture, out var edition) || edition <= 0)
                    throw new ArgumentException("The SonoVault edition ID must be a positive integer.");
                path += $"?edition={edition}";
            }
            basis = EnrichmentMatchBasis.ExternalReference;
        }
        else if (query.EntityKind == EnrichmentEntityKind.Release)
        {
            if (string.IsNullOrWhiteSpace(query.Name) || query.ArtistNames.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("A SonoVault ID or release title is required for lookup.");
            path = $"/v1/releases/search?title={Uri.EscapeDataString(query.Name.Trim())}&limit=10";
            if (query.ArtistNames.Count > 0)
                path += $"&artist={Uri.EscapeDataString(string.Join(", ", query.ArtistNames))}";
            basis = EnrichmentMatchBasis.MetadataSearch;
        }
        else if (query.EntityKind != EnrichmentEntityKind.Recording)
        {
            if (string.IsNullOrWhiteSpace(query.Name))
                throw new ArgumentException("A SonoVault ID or entity name is required for lookup.");
            path = $"/v1/{resource}/search?name={Uri.EscapeDataString(query.Name.Trim())}&limit=10";
            basis = EnrichmentMatchBasis.MetadataSearch;
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
            var candidates = tracks.Select(track => query.EntityKind switch
            {
                EnrichmentEntityKind.Recording => Map(track, query, basis, retrieved),
                EnrichmentEntityKind.Release => MapRelease(track, query, basis, retrieved),
                _ => MapNamedEntity(track, query, basis, retrieved)
            }).ToArray();
            if (query.EntityKind == EnrichmentEntityKind.Release)
            {
                candidates = candidates.OrderBy(c => c.Match.Conflicts.Count).ToArray();
                warnings.Add("Candidates are ordered by known title, primary Artist and Label conflicts; none are discarded. SonoVault search accepts title and Artist, not Label. Name agreement is not proof of identity.");
                warnings.Add("SonoVault may group different editions under one Release ID. Group dates and consensus tracklists are evidence only; no tracks, identities or ownership are added. Search does not fetch details automatically.");
            }
            if (query.EntityKind == EnrichmentEntityKind.Recording)
                warnings.Add("Duration units are not verified and release-specific metadata is not proposed. Mix names are only separated when a trailing parenthesized suffix matches the catalog title and recorded mix.");
            else if (query.EntityKind != EnrichmentEntityKind.Release)
                warnings.Add(query.EntityKind == EnrichmentEntityKind.Label
                    ? "SonoVault's documented Label metadata supplies name and identity only. Releases and artists are not fetched."
                    : "Formation year may mean birth year for a solo act or start year for a group. Social values may be handles, not URLs. Releases are not fetched.");
            return new("sonovault", query, retrieved, candidates, warnings);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new SonoVaultLookupException("timeout", "SonoVault lookup timed out. No automatic retry.", inner: ex); }
        catch (HttpRequestException ex)
        { throw new SonoVaultLookupException("unavailable", "SonoVault could not be reached. No automatic retry.", inner: ex); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid metadata response.", inner: ex); }
    }

    private static EnrichmentCandidate MapRelease(JsonElement release, EnrichmentQuery query,
        EnrichmentMatchBasis basis, DateTimeOffset retrieved)
    {
        var id = release.GetProperty("id").GetInt64();
        var title = Text(release, "title");
        if (id <= 0 || string.IsNullOrWhiteSpace(title))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid Release identity.");
        var expectedId = query.LookupCandidateId ?? (basis == EnrichmentMatchBasis.ExternalReference
            ? query.ExternalReferences.First(r => r.Source == "sonovault").ExternalId : null);
        if (expectedId is not null && id != long.Parse(expectedId, CultureInfo.InvariantCulture))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned a different Release than requested.");
        string? selectedId = null;
        if (release.TryGetProperty("edition", out var selection) && selection.ValueKind != JsonValueKind.Null)
            selectedId = PositiveId(selection);
        if (query.LookupEditionId is not null && (selectedId is null
            || long.Parse(selectedId, CultureInfo.InvariantCulture) != long.Parse(query.LookupEditionId, CultureInfo.InvariantCulture)))
            throw new SonoVaultLookupException("invalid_response", "SonoVault did not return the requested edition.");
        var credits = new List<EnrichmentCredit>();
        if (release.TryGetProperty("artist", out var artist) && artist.ValueKind != JsonValueKind.Null)
            credits.Add(ProviderCredit(artist, "primary"));
        var editions = new List<EnrichmentReleaseEdition>();
        if (release.TryGetProperty("editions", out var editionRows) && editionRows.ValueKind != JsonValueKind.Null)
            foreach (var row in editionRows.EnumerateArray())
            {
                if (editions.Count >= 20)
                    throw new SonoVaultLookupException("invalid_response", "SonoVault returned more editions than the documented limit.");
                var editionId = PositiveId(row.GetProperty("id"));
                var source = Text(row, "source");
                var externalId = Text(row, "external_id");
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(externalId) || editions.Any(e => e.Id == editionId))
                    throw new SonoVaultLookupException("invalid_response", "SonoVault returned invalid edition identity evidence.");
                editions.Add(new(editionId, source, externalId, Text(row, "title"), Text(row, "format"),
                    Text(row, "release_date"), Text(row, "date_precision"), Text(row, "catalog_no"), Text(row, "barcode"),
                    ProviderLabel(row), OptionalPositiveNumber(row, "track_count", allowZero: true)));
            }
        var selected = selectedId is null ? null : editions.SingleOrDefault(e => e.Id == selectedId);
        if (selectedId is not null && selected is null)
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned no evidence for the selected edition.");
        var tracks = new List<EnrichmentReleaseTrack>();
        if (release.TryGetProperty("tracks", out var trackRows) && trackRows.ValueKind != JsonValueKind.Null)
            foreach (var row in trackRows.EnumerateArray())
            {
                var trackTitle = Text(row, "title");
                if (string.IsNullOrWhiteSpace(trackTitle))
                    throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid tracklist title.");
                var trackCredits = row.GetProperty("artists").EnumerateArray().Select(a => ProviderCredit(a,
                    a.TryGetProperty("is_remixer", out var remix) && remix.GetBoolean() ? "remixer"
                        : a.TryGetProperty("is_primary", out var primary) && primary.GetBoolean() ? "primary" : "artist")).ToArray();
                tracks.Add(new(PositiveId(row.GetProperty("id")), trackTitle, Text(row, "isrc"),
                    OptionalPositiveNumber(row, "disc_number"), OptionalPositiveNumber(row, "track_number"), trackCredits));
            }
        var label = selectedId is null ? ProviderLabel(release) : selected!.LabelName;
        var conflicts = new List<string>();
        if (!SameName(query.Name, title))
            conflicts.Add("Returned title differs from the catalog Release title.");
        if (query.ArtistNames.Count > 0 && !credits.Any(c => query.ArtistNames.Any(a => SameName(a, c.Name))))
            conflicts.Add(credits.Count == 0 ? "No primary Artist was returned to compare with the catalog."
                : "Returned primary Artist differs from the known catalog primary Artists.");
        var labels = query.LabelNames ?? (query.LabelName is { } knownLabel ? [knownLabel] : []);
        if (labels.Count > 0 && !labels.Any(l => SameName(l, label)))
            conflicts.Add(label is null ? "No Label was returned to compare with the catalog."
                : "Returned Label differs from the known catalog Labels; editions may have different Labels.");
        if (selected?.CatalogNumber is { } number && query.CatalogNumber is { } knownNumber
            && !SameName(number, knownNumber))
            conflicts.Add("Selected edition catalog number differs from the catalog Release.");
        var details = new EnrichmentReleaseDetails(Text(release, "release_date"), selectedId, editions, tracks,
            TextValues(release, "musicbrainz_release_ids"), TextValues(release, "musicbrainz_release_group_ids"));
        var reference = new EnrichmentReference("sonovault", id.ToString(CultureInfo.InvariantCulture));
        var metadata = new EnrichmentMetadata(Name: title, LabelName: label, CatalogNumber: selected?.CatalogNumber,
            ExternalReferences: [reference], Credits: credits, ReleaseDetails: details);
        var fields = new List<EnrichmentField> { EnrichmentField.Name, EnrichmentField.ExternalReferences, EnrichmentField.ReleaseDetails };
        if (label is not null) fields.Add(EnrichmentField.LabelName);
        if (selected?.CatalogNumber is not null) fields.Add(EnrichmentField.CatalogNumber);
        if (credits.Count > 0) fields.Add(EnrichmentField.Credits);
        return new("sonovault", reference.ExternalId, selectedId, null, EnrichmentEntityKind.Release,
            new(basis, query.LookupCandidateId is null
                ? "Release candidate compared with known title, primary Artists and Labels; agreement does not establish edition identity."
                : "Explicitly requested provider candidate details, not an accepted catalog identity.", conflicts),
            metadata, fields.Select(field => new EnrichmentFieldEvidence(field, "sonovault", reference.ExternalId,
                null, retrieved, field == EnrichmentField.ReleaseDetails
                    ? "Group date, edition evidence and tracklist retained without canonical identity or ownership assertions."
                    : null)).ToArray());
    }

    private static bool SameName(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string PositiveId(JsonElement value)
    {
        var id = value.GetInt64();
        return id > 0 ? id.ToString(CultureInfo.InvariantCulture)
            : throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid provider identity.");
    }

    private static int? OptionalPositiveNumber(JsonElement row, string property, bool allowZero = false)
    {
        if (!row.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var number = value.GetInt32();
        return number > 0 || (allowZero && number == 0) ? number
            : throw new SonoVaultLookupException("invalid_response", "SonoVault returned invalid release numbering or counts.");
    }

    private static EnrichmentCredit ProviderCredit(JsonElement artist, string role)
    {
        var name = Text(artist, "name");
        if (string.IsNullOrWhiteSpace(name))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid Artist credit.");
        return new(name, role, new("sonovault", PositiveId(artist.GetProperty("id"))));
    }

    private static string? ProviderLabel(JsonElement row)
    {
        if (!row.TryGetProperty("label", out var label) || label.ValueKind == JsonValueKind.Null) return null;
        var name = Text(label, "name");
        if (string.IsNullOrWhiteSpace(name))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid Label name.");
        return name;
    }

    private static EnrichmentCandidate MapNamedEntity(JsonElement entity, EnrichmentQuery query,
        EnrichmentMatchBasis basis, DateTimeOffset retrieved)
    {
        var id = entity.GetProperty("id").GetInt64();
        var name = Text(entity, "name");
        if (id <= 0 || string.IsNullOrWhiteSpace(name))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid entity identity.");
        var references = new List<EnrichmentReference> { new("sonovault", id.ToString(CultureInfo.InvariantCulture)) };
        string? country = null;
        int? year = null;
        DateOnly? date = null;
        Dictionary<string, string>? socials = null;
        var fields = new List<EnrichmentField> { EnrichmentField.Name, EnrichmentField.ExternalReferences };
        if (query.EntityKind == EnrichmentEntityKind.Artist)
        {
            country = Text(entity, "country");
            if (entity.TryGetProperty("formation_year", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                year = value.GetInt32();
                if (year is < 1 or > 9999)
                    throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid formation year.");
            }
            if (Text(entity, "formation_date") is { } rawDate)
            {
                if (!DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid formation date.");
                date = parsed;
            }
            if (entity.TryGetProperty("social_links", out var links) && links.ValueKind != JsonValueKind.Null)
            {
                socials = [];
                foreach (var link in links.EnumerateObject())
                {
                    var text = link.Value.GetString();
                    if (string.IsNullOrWhiteSpace(link.Name) || string.IsNullOrWhiteSpace(text))
                        throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid social link.");
                    if (!socials.TryAdd(link.Name, text))
                        throw new SonoVaultLookupException("invalid_response", "SonoVault returned duplicate social link fields.");
                }
            }
            foreach (var source in new[] { "musicbrainz", "wikidata" })
                if (Text(entity, source + "_id") is { } externalId && !string.IsNullOrWhiteSpace(externalId))
                    references.Add(new(source, externalId));
            if (country is not null) fields.Add(EnrichmentField.Country);
            if (year is not null) fields.Add(EnrichmentField.FormationYear);
            if (date is not null) fields.Add(EnrichmentField.FormationDate);
            if (socials?.Count > 0) fields.Add(EnrichmentField.SocialLinks);
        }
        var conflicts = new List<string>();
        if (!string.Equals(query.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            conflicts.Add("Returned name differs from the catalog name; review entity identity.");
        foreach (var reference in references.Skip(1))
            if (query.ExternalReferences.Any(r => r.Source == reference.Source)
                && !query.ExternalReferences.Any(r => r.Source == reference.Source && r.ExternalId == reference.ExternalId))
                conflicts.Add($"Returned {reference.Source} identity differs from the catalog identity.");
        return new("sonovault", references[0].ExternalId, null, null, query.EntityKind,
            new(basis, "Name search and provider IDs are candidate evidence, not proof of canonical identity.", conflicts),
            new(Name: name, ExternalReferences: references, Country: country, FormationYear: year,
                FormationDate: date, SocialLinks: socials),
            fields.Select(field => new EnrichmentFieldEvidence(field, "sonovault", references[0].ExternalId,
                null, retrieved, null)).ToArray());
    }

    private static EnrichmentCandidate Map(JsonElement track, EnrichmentQuery query, EnrichmentMatchBasis basis, DateTimeOffset retrieved)
    {
        var id = track.GetProperty("id").GetInt64();
        var title = Text(track, "title");
        if (id <= 0 || string.IsNullOrWhiteSpace(title))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid Recording identity.");
        if (query.LookupCandidateId is { } requested && id != long.Parse(requested, CultureInfo.InvariantCulture))
            throw new SonoVaultLookupException("invalid_response", "SonoVault returned a different Recording than requested.");
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
        var genres = TextValues(track, "genre");
        var subgenres = TextValues(track, "subgenre");
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

    private static string[] TextValues(JsonElement track, string property)
    {
        if (!track.TryGetProperty(property, out var values) || values.ValueKind == JsonValueKind.Null) return [];
        return values.EnumerateArray().Select(value =>
        {
            var name = value.GetString();
            return string.IsNullOrWhiteSpace(name)
                ? throw new SonoVaultLookupException("invalid_response", "SonoVault returned an invalid metadata label or identifier.")
                : name;
        }).ToArray();
    }
}
