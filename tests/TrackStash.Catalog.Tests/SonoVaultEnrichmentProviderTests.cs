using System.Net;
using System.Text;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Tests;

public sealed class SonoVaultEnrichmentProviderTests
{
    private const string Track = """
        {"id":7,"title":"Example","isrc":"GBTEST000001","bpm":123.45,"duration":240,
         "key_alphanumeric":"10B","genre":["House"],"releases":[{"label":{"name":"Not a recording label"}}],
         "artists":[{"id":42,"name":"Act","is_primary":true},{"id":43,"name":"Remixer","is_remixer":true}]}
        """;
    private static EnrichmentQuery Query(string? isrc = "GBTEST000001") => new(
        EnrichmentEntityKind.Recording, "recording-1", "version-1", "Example", null, ["Act"],
        isrc, null, null, null, null, []);

    private const string ReleaseResponse = """
        {"id":8,"title":"Example","artist":{"id":42,"name":"Act"},"label":{"id":3,"name":"Known Label"},
         "release_date":"2020-01-01","musicbrainz_release_ids":["edition-mbid"],"musicbrainz_release_group_ids":["group-mbid"],
         "edition":null,"editions":[{"id":12,"source":"discogs","external_id":"99","title":"Example deluxe",
           "format":"digital","release_date":"2024-01-01","date_precision":"year","catalog_no":"ABC1","barcode":"123",
           "label":{"id":3,"name":"Known Label"},"track_count":2}],
         "tracks":[{"id":7,"title":"Example (Remix)","isrc":"GBTEST000001","disc_number":1,"track_number":2,
           "artists":[{"id":42,"name":"Act","is_primary":true}]}]}
        """;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Release_search_uses_title_and_optional_artist_then_ranks_known_conflicts_without_dropping_candidates(bool hasArtist, bool wrongArtist)
    {
        var wrong = wrongArtist ? ReleaseResponse.Replace("\"name\":\"Act\"", "\"name\":\"Other Act\"")
            : ReleaseResponse.Replace("\"Known Label\"", "\"Other Label\"");
        var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = Json($$"""{"results":[{{wrong}},{{ReleaseResponse.Replace("\"id\":8", "\"id\":9")}}],"next_cursor":null}""")
        });
        using var http = new HttpClient(handler);
        var provider = new SonoVaultEnrichmentProvider(http, "test-key");
        var result = await provider.FindAsync(Query() with
        { EntityKind = EnrichmentEntityKind.Release, Name = "Example", ArtistNames = hasArtist ? ["Act"] : [], LabelNames = ["Known Label"] });
        Assert.Contains(EnrichmentEntityKind.Release, provider.Descriptor.SupportedEntityKinds);
        Assert.Equal("/v1/releases/search", handler.Uri!.AbsolutePath);
        Assert.Contains("title=Example&limit=10", handler.Uri.Query);
        Assert.Equal(hasArtist, handler.Uri.Query.Contains("&artist=Act"));
        Assert.DoesNotContain("label", handler.Uri.Query);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("9", result.Candidates[0].CandidateId);
        Assert.Empty(result.Candidates[0].Match.Conflicts);
        Assert.Contains(result.Candidates[1].Match.Conflicts, c => c.Contains(wrongArtist ? "primary Artist differs" : "Label differs"));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    public async Task Release_details_preserve_editions_dates_and_tracklist_without_accepting_group_ids(string? editionId)
    {
        var json = editionId is null ? ReleaseResponse : ReleaseResponse.Replace("\"edition\":null", "\"edition\":12");
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(json) });
        using var http = new HttpClient(handler);
        var query = Query() with { EntityKind = EnrichmentEntityKind.Release, LookupCandidateId = "8", LookupEditionId = editionId,
            LabelNames = ["Known Label"], CatalogNumber = "ABC1" };
        var candidate = Assert.Single((await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(query)).Candidates);
        Assert.Equal("/v1/releases/8" + (editionId is null ? "" : "?edition=12"), handler.Uri!.PathAndQuery);
        Assert.Equal(editionId, candidate.EditionId);
        Assert.Null(candidate.Metadata.ReleaseDate);
        Assert.Equal(editionId is null ? null : "ABC1", candidate.Metadata.CatalogNumber);
        var details = candidate.Metadata.ReleaseDetails!;
        Assert.Equal("2020-01-01", details.ReportedReleaseDate);
        Assert.Equal("year", Assert.Single(details.Editions).DatePrecision);
        Assert.Equal(["edition-mbid"], details.MusicBrainzReleaseIds);
        Assert.Equal(["group-mbid"], details.MusicBrainzReleaseGroupIds);
        Assert.Single(candidate.Metadata.ExternalReferences!);
        Assert.Equal("Example (Remix)", Assert.Single(details.Tracks).Title);
        Assert.Equal(2, details.Tracks[0].TrackNumber);
        Assert.All(candidate.Evidence, e => Assert.Equal("sonovault", e.Source));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("8", null)]
    [InlineData("8", "12")]
    public async Task Release_candidate_and_edition_mismatch_fail_explicitly(string candidateId, string? editionId)
    {
        var json = editionId is null ? ReleaseResponse.Replace("\"id\":8", "\"id\":9") : ReleaseResponse;
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(json) });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<SonoVaultLookupException>(() =>
            new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query() with
            { EntityKind = EnrichmentEntityKind.Release, LookupCandidateId = candidateId, LookupEditionId = editionId }).AsTask());
        Assert.Equal("invalid_response", failure.Code);
    }

    [Theory]
    [InlineData("bad", null)]
    [InlineData("8", "0")]
    [InlineData(null, "12")]
    public async Task Invalid_release_selection_does_not_use_quota(string? candidateId, string? editionId)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query() with
        { EntityKind = EnrichmentEntityKind.Release, LookupCandidateId = candidateId, LookupEditionId = editionId }).AsTask());
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Isrc_lookup_maps_typed_evidence_without_inventing_release_or_analysis_provenance()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(Track) });
        using var http = new HttpClient(handler);
        var result = await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query());
        Assert.Equal("https://api.sonovault.now/v1/tracks/isrc/GBTEST000001", handler.Uri?.AbsoluteUri);
        Assert.Equal("test-key", handler.Key);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(EnrichmentMatchBasis.Isrc, candidate.Match.Basis);
        Assert.Equal(123.45m, candidate.Metadata.Bpm);
        Assert.Equal(new EnrichmentMusicalKey("10B", "Camelot"), candidate.Metadata.MusicalKey);
        Assert.Null(candidate.Metadata.DurationMs);
        Assert.Null(candidate.Metadata.LabelName);
        Assert.Null(candidate.Metadata.ReleaseDate);
        Assert.Equal("remixer", candidate.Metadata.Credits![1].Role);
        Assert.All(candidate.Evidence, evidence => Assert.Equal("sonovault", evidence.Source));
        Assert.Equal("version-1", result.Query.CatalogVersion);
    }

    [Fact]
    public async Task Search_fetches_only_one_page_and_preserves_version_conflicts()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json($$"""{"results":[{{Track}}],"next_cursor":"more"}""") });
        using var http = new HttpClient(handler);
        var query = Query(null) with { Name = "A & B", MixName = "Dub" };
        var result = await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(query);
        Assert.Contains("title=A%20%26%20B%20%28Dub%29", handler.Uri!.AbsoluteUri);
        Assert.Contains("limit=10", handler.Uri.AbsoluteUri);
        Assert.Single(result.Candidates);
        Assert.NotEmpty(result.Candidates[0].Match.Conflicts);
        Assert.Contains(result.Warnings, w => w.Contains("first page"));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Provider_reference_takes_precedence_over_isrc()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(Track) });
        using var http = new HttpClient(handler);
        var result = await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(
            Query() with { ExternalReferences = [new("sonovault", "7")] });
        Assert.Equal("/v1/tracks/7", handler.Uri!.AbsolutePath);
        Assert.Equal(EnrichmentMatchBasis.ExternalReference, result.Candidates[0].Match.Basis);
    }

    [Theory]
    [InlineData("""["House"]""", """["Progressive House"]""")]
    [InlineData("""["House","Electronic"]""", """["Progressive House","Melodic House"]""")]
    [InlineData("[]", "[]")]
    public async Task Genre_and_subgenre_arrays_are_preserved_with_separate_evidence(string genres, string subgenres)
    {
        var json = Track.Replace("\"genre\":[\"House\"]", $"\"genre\":{genres},\"subgenre\":{subgenres}");
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(json) });
        using var http = new HttpClient(handler);
        var result = await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query());
        var candidate = Assert.Single(result.Candidates);
        var expectedGenres = System.Text.Json.JsonSerializer.Deserialize<string[]>(genres)!;
        var expectedSubgenres = System.Text.Json.JsonSerializer.Deserialize<string[]>(subgenres)!;
        Assert.Equal(expectedGenres, candidate.Metadata.Genres);
        Assert.Equal(expectedSubgenres, candidate.Metadata.Subgenres);
        Assert.Equal(expectedGenres.Length == 1 ? expectedGenres[0] : null, candidate.Metadata.Genre);
        Assert.Equal(expectedGenres.Length > 0, candidate.Evidence.Any(e => e.Field == EnrichmentField.Genre));
        Assert.Equal(expectedSubgenres.Length > 0, candidate.Evidence.Any(e => e.Field == EnrichmentField.Subgenre));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("exactly one genre"));
    }

    [Theory]
    [InlineData(EnrichmentEntityKind.Artist, "artists")]
    [InlineData(EnrichmentEntityKind.Label, "labels")]
    public async Task Named_entity_search_is_bounded_preserves_candidates_and_uses_name(EnrichmentEntityKind kind, string resource)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = Json("""{"results":[{"id":8,"name":"A & B"},{"id":9,"name":"Different"}],"next_cursor":"more"}""")
        });
        using var http = new HttpClient(handler);
        var provider = new SonoVaultEnrichmentProvider(http, "test-key");
        var result = await provider.FindAsync(Query() with { EntityKind = kind, Name = "A & B" });
        Assert.Contains(kind, provider.Descriptor.SupportedEntityKinds);
        Assert.Equal($"/v1/{resource}/search?name=A%20%26%20B&limit=10", handler.Uri!.PathAndQuery);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.Equal(kind, c.EntityKind));
        Assert.Empty(result.Candidates[0].Match.Conflicts);
        Assert.NotEmpty(result.Candidates[1].Match.Conflicts);
        Assert.Contains(result.Warnings, w => w.Contains("first page"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Mix names"));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(EnrichmentEntityKind.Artist, "artists")]
    [InlineData(EnrichmentEntityKind.Label, "labels")]
    public async Task Named_entity_id_takes_precedence_and_artist_metadata_has_conservative_evidence(EnrichmentEntityKind kind, string resource)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = Json("""
                {"id":8,"name":"Act","country":"United Kingdom","formation_year":1980,"formation_date":"1980-02-03",
                 "social_links":{"instagram":"act"},"musicbrainz_id":"mbid","wikidata_id":"Q123"}
                """)
        });
        using var http = new HttpClient(handler);
        var result = await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query() with
        {
            EntityKind = kind, Name = "Act", ExternalReferences = [new("sonovault", "8"), new("musicbrainz", "other")]
        });
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal($"/v1/{resource}/8", handler.Uri!.AbsolutePath);
        Assert.Equal(EnrichmentMatchBasis.ExternalReference, candidate.Match.Basis);
        Assert.All(candidate.Evidence, e => Assert.Equal("sonovault", e.Source));
        if (kind == EnrichmentEntityKind.Artist)
        {
            Assert.Equal("United Kingdom", candidate.Metadata.Country);
            Assert.Equal(1980, candidate.Metadata.FormationYear);
            Assert.Equal(new DateOnly(1980, 2, 3), candidate.Metadata.FormationDate);
            Assert.Equal("act", candidate.Metadata.SocialLinks!["instagram"]);
            Assert.Equal(3, candidate.Metadata.ExternalReferences!.Count);
            Assert.Contains(candidate.Match.Conflicts, c => c.Contains("musicbrainz"));
            Assert.Contains(candidate.Evidence, e => e.Field == EnrichmentField.SocialLinks);
        }
        else
        {
            Assert.Null(candidate.Metadata.Country);
            Assert.Null(candidate.Metadata.FormationYear);
            Assert.Null(candidate.Metadata.SocialLinks);
            Assert.Single(candidate.Metadata.ExternalReferences!);
        }
    }

    [Theory]
    [InlineData("""{"id":0,"name":"Act"}""")]
    [InlineData("""{"id":8,"name":""}""")]
    [InlineData("""{"id":8,"name":"Act","formation_date":"1980"}""")]
    [InlineData("""{"id":8,"name":"Act","formation_year":0}""")]
    [InlineData("""{"id":8,"name":"Act","social_links":{"instagram":42}}""")]
    public async Task Invalid_artist_metadata_is_an_explicit_provider_failure(string json)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(json) });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<SonoVaultLookupException>(() =>
            new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query() with
            { EntityKind = EnrichmentEntityKind.Artist, ExternalReferences = [new("sonovault", "8")] }).AsTask());
        Assert.Equal("invalid_response", failure.Code);
    }

    [Theory]
    [InlineData("Flat Surfaces (Kohra Remix)", "Flat Surfaces", "Kohra Remix", true)]
    [InlineData("Flat Surfaces (kohra remix)", "Flat Surfaces", "Kohra Remix", true)]
    [InlineData("Flat Surfaces (Live) (Kohra Remix)", "Flat Surfaces (Live)", "Kohra Remix", true)]
    [InlineData("Flat Surfaces (Other Remix)", "Flat Surfaces", "Kohra Remix", false)]
    [InlineData("Other Title (Kohra Remix)", "Flat Surfaces", "Kohra Remix", false)]
    [InlineData("Flat Surfaces (Live)", "Flat Surfaces (Live)", null, false)]
    [InlineData("Flat Surfaces (Kohra Remix)", "Flat Surfaces", null, false)]
    public async Task Combined_title_is_only_split_when_both_catalog_title_and_mix_match(
        string providerTitle, string catalogTitle, string? catalogMix, bool split)
    {
        var json = Track.Replace("\"Example\"", System.Text.Json.JsonSerializer.Serialize(providerTitle));
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(json) });
        using var http = new HttpClient(handler);
        var candidate = Assert.Single((await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(
            Query() with { Name = catalogTitle, MixName = catalogMix })).Candidates);
        Assert.Equal(split ? catalogTitle : providerTitle, candidate.Metadata.Name);
        if (split)
        {
            Assert.Equal(catalogMix, candidate.Metadata.MixName, ignoreCase: true);
            Assert.Empty(candidate.Match.Conflicts);
            Assert.Contains(candidate.Evidence, e => e.Field == EnrichmentField.MixName && e.Attribution!.Contains(providerTitle));
        }
        else
        {
            Assert.Null(candidate.Metadata.MixName);
            Assert.DoesNotContain(candidate.Evidence, e => e.Field == EnrichmentField.MixName);
            if (catalogMix is not null) Assert.NotEmpty(candidate.Match.Conflicts);
        }
    }

    [Theory]
    [InlineData(401, "authentication_failed")]
    [InlineData(403, "access_denied")]
    [InlineData(429, "rate_limited")]
    [InlineData(500, "upstream_failed")]
    public async Task Upstream_errors_are_explicit_safe_and_not_retried(int status, string code)
    {
        var handler = new Handler(_ => new((HttpStatusCode)status) { Content = Json("""{"detail":"sensitive upstream content"}""") });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SonoVaultLookupException>(() =>
            new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query()).AsTask());
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task No_match_is_distinct_from_malformed_response()
    {
        var handler = new Handler(_ => new(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        Assert.Empty((await new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query())).Candidates);
        handler.Respond = _ => new(HttpStatusCode.OK) { Content = Json("{}") };
        var error = await Assert.ThrowsAsync<SonoVaultLookupException>(() =>
            new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query()).AsTask());
        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task Unconfigured_unsupported_and_cancelled_lookups_do_not_send_requests()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<SonoVaultLookupException>(() => new SonoVaultEnrichmentProvider(http, null).FindAsync(Query()).AsTask());
        var provider = new SonoVaultEnrichmentProvider(http, "test-key");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.FindAsync(Query() with { EntityKind = (EnrichmentEntityKind)99 }).AsTask());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FindAsync(Query(), cancelled.Token).AsTask());
        Assert.Equal(0, handler.Count);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = Json(new string('x', 1024 * 1024 + 1)) });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SonoVaultLookupException>(() =>
            new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query()).AsTask());
        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task Network_failure_is_not_an_empty_success()
    {
        var handler = new Handler(_ => throw new HttpRequestException("Test network error"));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SonoVaultLookupException>(() =>
            new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(Query()).AsTask());
        Assert.Equal("unavailable", error.Code);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Ambiguous_identity_is_rejected_before_spending_quota()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new SonoVaultEnrichmentProvider(http, "test-key").FindAsync(
            Query() with { ExternalReferences = [new("sonovault", "1"), new("sonovault", "2")] }).AsTask());
        Assert.Equal(0, handler.Count);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        public Uri? Uri { get; private set; }
        public string? Key { get; private set; }
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            Uri = request.RequestUri;
            Key = request.Headers.GetValues("x-api-key").Single();
            return Task.FromResult(Respond(request));
        }
    }
}
