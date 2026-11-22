using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TrackStash.Catalog.Api.Beatport;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private sealed class NamedArtworkImages(byte[] image) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("geo-media.beatport.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
        }
    }

    [Theory]
    [InlineData("artists", "artist", "t-primary", "portrait")]
    [InlineData("labels", "label", "t-label", "logo")]
    public async Task BeatportNamedArtwork_CandidatesPreviewAttachAndStaleChecks(string resource, string kind, string id, string role)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        await ImportSql($"INSERT INTO {kind}_external_ref({kind}_id,source,external_id) VALUES('{id}','beatport','10')");
        var images = new NamedArtworkImages(ImageBytes());
        var (app, client, broker) = await StartWithBrokerAsync(artwork: images);
        await using var _ = app;
        var path = $"/api/v1/{resource}/{id}/artwork";
        broker.Pages[$"/v4/catalog/{resource}/10/"] = """{"id":10,"name":"Provider name","image":{"uri":"https://geo-media.beatport.com/image.png"}}""";
        var initial = await client.GetAsync(path);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(ImageBytes()), "file", "existing.png");
        form.Add(new StringContent("Not recorded"), "usageRights");
        form.Add(new StringContent(role), "role");
        form.Add(new StringContent("true"), "isPreferred");
        using var upload = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        upload.Headers.TryAddWithoutValidation("If-Match", initial.Headers.ETag!.ToString());
        var existing = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Created, existing.StatusCode);
        var originalTag = existing.Headers.ETag!.ToString();
        var page = await client.GetFromJsonAsync<TrackStash.Catalog.Api.ReleaseArtworkCandidatesResponse>(path + "-candidates");
        var candidate = Assert.Single(page!.Items);
        Assert.Equal($"{id}:10", candidate.CandidateId);
        Assert.Equal(0, images.Calls);
        var query = $"?provider=beatport&candidateId={Uri.EscapeDataString(candidate.CandidateId)}&revision={candidate.Revision}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "-candidates/preview" + query)).StatusCode);
        Assert.Equal(1, images.Calls);
        async Task<HttpResponseMessage> Attach(string tag, string revision)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path + "-candidates/attach")
            { Content = JsonContent.Create(new { provider = "beatport", candidate.CandidateId, revision, isPreferred = false }) };
            request.Headers.TryAddWithoutValidation("If-Match", tag);
            return await client.SendAsync(request);
        }
        await ProblemAsync(await Attach(originalTag, "changed"), 409, "release_artwork_unavailable");
        var result = await Attach(originalTag, candidate.Revision);
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        var gallery = await result.Content.ReadFromJsonAsync<JsonElement>();
        var items = gallery.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.True(items[0].GetProperty("isPreferred").GetBoolean());
        Assert.False(items[1].GetProperty("isPreferred").GetBoolean());
        Assert.Equal(role, items[1].GetProperty("role").GetString());
        Assert.Equal($"https://www.beatport.com/{kind}/-/10", items[1].GetProperty("sourceUrl").GetString());
        await ProblemAsync(await Attach(originalTag, candidate.Revision), 412, "stale_version");
        Assert.Equal(2, images.Calls);
        broker.Pages[$"/v4/catalog/{resource}/10/"] = """{"id":10,"name":"Provider name","image":{"uri":"https://evil.example/image.png"}}""";
        await ProblemAsync(await client.GetAsync(path + "-candidates"), 409, "release_artwork_unavailable");
        Assert.Equal(2, images.Calls);
    }

    [Theory]
    [InlineData("artists", "artist", "t-primary")]
    [InlineData("labels", "label", "t-label")]
    public async Task BeatportNamedArtwork_RequiresOneLinkedIdentityAndChecksProviderResponse(string resource, string kind, string id)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        var path = $"/api/v1/{resource}/{id}/artwork-candidates";
        var empty = await client.GetFromJsonAsync<TrackStash.Catalog.Api.ReleaseArtworkCandidatesResponse>(path);
        Assert.Empty(empty!.Items);
        Assert.Empty(broker.Requests);
        await ImportSql($"INSERT INTO {kind}_external_ref({kind}_id,source,external_id) VALUES('{id}','beatport','10')");
        broker.Pages[$"/v4/catalog/{resource}/10/"] = """{"id":10,"name":"Provider name","image":null}""";
        Assert.Empty((await client.GetFromJsonAsync<TrackStash.Catalog.Api.ReleaseArtworkCandidatesResponse>(path))!.Items);
        broker.Pages[$"/v4/catalog/{resource}/10/"] = """{"id":11,"name":"Wrong identity","image":{"uri":"https://geo-media.beatport.com/image.jpg"}}""";
        await ProblemAsync(await client.GetAsync(path), 409, "release_artwork_unavailable");
        await ImportSql($"INSERT INTO {kind}_external_ref({kind}_id,source,external_id) VALUES('{id}','beatport','11')");
        var calls = broker.Requests.Count;
        await ProblemAsync(await client.GetAsync(path), 409, "release_artwork_unavailable");
        Assert.Equal(calls, broker.Requests.Count);
    }

    [Fact]
    public async Task BeatportEnrichment_RecordingSearchUsesIsrcOrTitleArtistMixAndLinkedIdFirst()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/tracks/?page=1&per_page=10&order_by=name&isrc=GBTRK0000001"] =
            $$"""{"count":11,"results":[{{BeatportReviewTrack}}]}""";
        broker.Pages["/v4/catalog/tracks/?page=1&per_page=10&order_by=name&name=Bravo%20tune&artists.name=Alpha%20Other&mix_name=Extended%20Mix"] =
            $$"""{"count":1,"results":[{{BeatportReviewTrack}}]}""";
        broker.Pages["/v4/catalog/tracks/7/"] = BeatportReviewTrack;
        var original = await client.GetStringAsync("/api/v1/recordings/t-shared");
        var response = await client.PostAsync("/api/v1/recordings/t-shared/enrichment/beatport", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = await response.Content.ReadFromJsonAsync<JsonElement>();
        var candidate = Assert.Single(lookup.GetProperty("candidates").EnumerateArray());
        Assert.Equal("Isrc", candidate.GetProperty("match").GetProperty("basis").GetString());
        Assert.Equal("Updated tune", candidate.GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal("Dub", candidate.GetProperty("metadata").GetProperty("mixName").GetString());
        Assert.Equal(2, candidate.GetProperty("metadata").GetProperty("credits").GetArrayLength());
        Assert.True(candidate.GetProperty("match").GetProperty("conflicts").GetArrayLength() >= 2);
        Assert.Contains(lookup.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("first ten"));
        Assert.Equal(original, await client.GetStringAsync("/api/v1/recordings/t-shared"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/recordings/t-second/enrichment/beatport", null)).StatusCode);
        Assert.Equal(2, broker.Requests.Count);
        await ImportSql("INSERT INTO recording_external_ref(recording_id,source,external_id) VALUES('t-shared','beatport','007')");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/recordings/t-shared/enrichment/beatport", null)).StatusCode);
        Assert.EndsWith("/v4/catalog/tracks/7/", broker.Requests.Last());
        Assert.Equal(0, await ImportCount("enrichment_review"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
        broker.Pages["/v4/catalog/tracks/7/"] = """{"id":8,"name":"Wrong identity"}""";
        await ProblemAsync(await client.PostAsync("/api/v1/recordings/t-shared/enrichment/beatport", null), 502, "beatport_invalid_response");
        broker.Pages.Remove("/v4/catalog/tracks/7/");
        var missing = await client.PostAsync("/api/v1/recordings/t-shared/enrichment/beatport", null);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Empty((await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidates").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"id":"7","name":"Track"}""")]
    [InlineData("""{"id":7,"name":"Track","bpm":-1}""")]
    [InlineData("""{"id":7,"name":"Track","bpm":"124"}""")]
    [InlineData("""{"id":7,"name":"Track","key":"11A"}""")]
    [InlineData("""{"id":7,"name":"Track","length_ms":1.5}""")]
    [InlineData("""{"id":7,"name":"Track","artists":[false]}""")]
    public async Task BeatportEnrichment_MalformedRecordingCannotBecomeAFrozenProposal(string payload)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/tracks/7/"] = payload;
        var entity = await client.GetFromJsonAsync<JsonElement>("/api/v1/recordings/t-shared");
        var response = await client.PostAsJsonAsync("/api/v1/recordings/t-shared/enrichment/beatport/reviews",
            new { candidateId = "7", catalogVersion = entity.GetProperty("version").GetString() });
        await ProblemAsync(response, 502, "beatport_invalid_response");
        Assert.Equal(0, await ImportCount("enrichment_review"));
        Assert.Equal(0, await ImportCount("enrichment_apply_revision"));
    }

    [Theory]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"count":"0","results":[]}""")]
    [InlineData("""{"count":0,"results":[{"id":7,"name":"Track"}]}""")]
    [InlineData("""{"count":0,"results":null}""")]
    public async Task BeatportEnrichment_MalformedSearchIsAnErrorRatherThanANoMatch(string payload)
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/tracks/?page=1&per_page=10&order_by=name&isrc=GBTRK0000001"] = payload;
        await ProblemAsync(await client.PostAsync("/api/v1/recordings/t-shared/enrichment/beatport", null), 502, "beatport_invalid_response");
        Assert.Single(broker.Requests);
    }

    [Fact]
    public async Task BeatportEnrichment_SearchDetailsAndProviderErrorsRemainReadOnly()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/?page=1&per_page=10&order_by=name&name=New%20release"] =
            $$"""{"count":1,"results":[{{BrowseRelease(10, "New release", 42)}}]}""";
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42);
        broker.Pages["/v4/catalog/tracks/?release_id=10&per_page=100"] = """
            {"count":101,"results":[{"id":7,"number":1,"name":"Track","mix_name":"Dub","artists":[{"id":7,"name":"Artist"}]}]}
            """;
        var before = await client.GetStringAsync("/api/v1/releases/t-new");
        var revisions = await ImportCount("catalog_foundation_revision");
        var search = await client.PostAsync("/api/v1/releases/t-new/enrichment/beatport", null);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var result = await search.Content.ReadFromJsonAsync<EnrichmentLookup>(options);
        Assert.Equal("beatport", result!.ProviderId);
        Assert.Equal("New release", result.Query.Name);
        Assert.Single(broker.Requests);
        var details = await client.PostAsync("/api/v1/releases/t-new/enrichment/beatport/candidates/10", null);
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var lookup = (await details.Content.ReadFromJsonAsync<EnrichmentLookup>(options))!;
        var candidate = Assert.Single(lookup.Candidates);
        Assert.Equal("Track (Dub)", Assert.Single(candidate.Metadata.ReleaseDetails!.Tracks).Title);
        Assert.Contains(lookup.Warnings, w => w.Contains("incomplete"));
        Assert.All(candidate.Evidence, e => Assert.Equal("beatport", e.Source));
        Assert.Equal(3, broker.Requests.Count);
        Assert.Equal(before, await client.GetStringAsync("/api/v1/releases/t-new"));
        Assert.Equal(revisions, await ImportCount("catalog_foundation_revision"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/releases/t-new/enrichment/beatport/candidates/bad", null)).StatusCode);
        broker.SignedIn = false;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/v1/releases/t-new/enrichment/beatport", null)).StatusCode);
    }

    [Fact]
    public async Task BeatportArtwork_LinkedCandidateLookupDoesNotDownloadOrWrite()
    {
        await CreatePurchaseSource();
        await ImportSql(TrackFixture);
        await ImportSql("INSERT INTO release_external_ref(release_id,source,external_id) VALUES('t-new','beatport','10')");
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/10/"] = BrowseRelease(10, "New release", 42, "https://geo-media.beatport.com/cover.jpg");
        var revisions = await ImportCount("release_artwork_revision");
        var response = await client.GetAsync("/api/v1/releases/t-new/artwork-candidates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<TrackStash.Catalog.Api.ReleaseArtworkCandidatesResponse>())!;
        Assert.Contains("beatport", page.Providers);
        Assert.Equal("t-new:10", Assert.Single(page.Items).CandidateId);
        Assert.Single(broker.Requests);
        Assert.Equal(revisions, await ImportCount("release_artwork_revision"));
        Assert.Equal(0, await ImportCount("artwork_asset"));
    }

    [Fact]
    public async Task ArtworkCandidate_PreviewIsReadOnlyAndAttachmentCanPreservePreferredCover()
    {
        var id = await ArtworkRelease();
        var initial = await _client.GetAsync($"/api/v1/releases/{id}/artwork");
        var uploaded = await ReleaseUpload(id, initial.Headers.ETag!.ToString());
        var existing = Assert.Single((await uploaded.Content.ReadFromJsonAsync<TrackStash.Catalog.Api.ReleaseArtworkResponse>())!.Items);
        var provider = new FakeReleaseArtworkProvider();
        await using var app = Build(b => b.Services.AddSingleton<IReleaseArtworkProvider>(provider));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        var revision = await ImportCount("release_artwork_revision");
        var preview = await client.GetAsync($"/api/v1/releases/{id}/artwork-candidates/preview?provider=test-provider&candidateId={id}-cover&revision=revision-1");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType!.MediaType);
        Assert.True(preview.Headers.CacheControl!.NoStore);
        Assert.Equal(revision, await ImportCount("release_artwork_revision"));
        using var attach = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{id}/artwork-candidates/attach")
        {
            Content = JsonContent.Create(new { provider = provider.Name, candidateId = id + "-cover", revision = "revision-1",
                usageRights = "Reviewed permission", confirmed = true, isPreferred = false })
        };
        attach.Headers.TryAddWithoutValidation("If-Match", uploaded.Headers.ETag!.ToString());
        var attached = await client.SendAsync(attach);
        Assert.Equal(HttpStatusCode.Created, attached.StatusCode);
        var gallery = (await attached.Content.ReadFromJsonAsync<TrackStash.Catalog.Api.ReleaseArtworkResponse>())!;
        Assert.Equal(2, gallery.Items.Count);
        Assert.Equal(existing.AssetId, Assert.Single(gallery.Items, i => i.IsPreferred).AssetId);
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/releases/{id}/artwork-candidates/preview?provider=test-provider&candidateId=x&revision=y")).StatusCode);
    }
}

public sealed class BeatportArtworkProviderTests
{
    private sealed class Broker : IBeatportBrokerClient
    {
        public bool Configured => true;
        public string? Json { get; set; }
        public int Calls { get; private set; }
        public Task<JsonElement?> GetCatalogAsync(string path, IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
        {
            Calls++;
            if (Json is null) return Task.FromResult<JsonElement?>(null);
            using var document = JsonDocument.Parse(Json);
            return Task.FromResult<JsonElement?>(document.RootElement.Clone());
        }
        public Task<BeatportConnectionResponse> StatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BeatportConnectionResponse> SignInAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BrokerFetchOutcome> FetchAsync(string entityType, long id, bool refresh, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Images(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("geo-media.beatport.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
        }
    }
    private static ReleaseArtworkSourceQuery Query => new("release-1", "Release", null, [new("beatport", "10", true)]);
    private static string Release(string image) => JsonSerializer.Serialize(new { id = 10, name = "Release", image = new { uri = image } });

    [Fact]
    public async Task CandidatesAreScopedRevalidatedAndOnlyDownloadAfterExplicitRead()
    {
        var broker = new Broker { Json = Release("https://geo-media.beatport.com/cover.jpg") };
        var images = new Images([1, 2, 3]);
        using var http = new HttpClient(images);
        var provider = new BeatportArtworkProvider(broker, http);
        var candidate = Assert.Single(await provider.FindAsync(Query));
        Assert.Equal("release-1:10", candidate.CandidateId);
        Assert.Equal(0, images.Calls);
        Assert.Equal([1, 2, 3], (await provider.ReadAsync(Query, candidate)).Bytes);
        Assert.Equal(1, images.Calls);
        broker.Json = Release("https://geo-media.beatport.com/changed.jpg");
        await Assert.ThrowsAsync<ReleaseArtworkUnavailableException>(() => provider.ReadAsync(Query, candidate).AsTask());
        await Assert.ThrowsAsync<ReleaseArtworkUnavailableException>(() => provider.ReadAsync(Query with { ReleaseId = "other" }, candidate).AsTask());
        Assert.Equal(1, images.Calls);
        Assert.Empty(await provider.FindAsync(Query with { ExternalReferences = [] }));
    }

    [Theory]
    [InlineData("http://geo-media.beatport.com/cover.jpg")]
    [InlineData("https://evil.example/cover.jpg")]
    [InlineData("https://geo-media.beatport.com.evil.example/cover.jpg")]
    [InlineData("https://geo-media.beatport.com:8443/cover.jpg")]
    [InlineData("https://user@geo-media.beatport.com/cover.jpg")]
    public async Task UnapprovedArtworkUrlsNeverDownload(string url)
    {
        var broker = new Broker { Json = Release(url) };
        var images = new Images([]);
        using var http = new HttpClient(images);
        await Assert.ThrowsAsync<ReleaseArtworkUnavailableException>(() => new BeatportArtworkProvider(broker, http).FindAsync(Query).AsTask());
        Assert.Equal(0, images.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, 0)]
    [InlineData(HttpStatusCode.OK, 10485761)]
    public async Task RedirectsAndOversizedArtworkAreExplicitFailures(HttpStatusCode status, int size)
    {
        var broker = new Broker { Json = Release("https://geo-media.beatport.com/cover.jpg") };
        var images = new Images(new byte[size], status);
        using var http = new HttpClient(images);
        var provider = new BeatportArtworkProvider(broker, http);
        var candidate = Assert.Single(await provider.FindAsync(Query));
        await Assert.ThrowsAsync<ReleaseArtworkUnavailableException>(() => provider.ReadAsync(Query, candidate).AsTask());
    }
}
