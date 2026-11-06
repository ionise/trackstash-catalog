using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Api.Beatport;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static string BrowseRelease(long id, string name, long labelId, string? image = null, string slug = "a-release") => JsonSerializer.Serialize(new
    {
        id, name, slug, type = new { name = "EP" }, catalog_number = $"CAT{id}", publish_date = "2026-10-01", track_count = 2,
        artists = new[] { new { id = 7, name = "Artist" } }, remixers = Array.Empty<object>(),
        label = new { id = labelId, name = $"Label {labelId}", slug = "label" },
        image = image is null ? null : new { uri = image, dynamic_uri = (string?)null },
    });

    private async Task CatalogSql(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task BeatportBrowse_AnnotatesReleasesWithCatalogLabelAndPurchaseStatusWithoutWrites()
    {
        await CreatePurchaseSource();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        await CatalogSql("""
            INSERT INTO label (label_id, name) VALUES ('catalog-label', 'Test label');
            INSERT INTO label_external_ref (label_id, source, external_id) VALUES ('catalog-label', 'beatport', '42');
            INSERT INTO release (release_id, name) VALUES ('catalog-release', 'Owned');
            INSERT INTO release_external_ref (release_id, source, external_id) VALUES ('catalog-release', 'beatport', '11');
            """);
        broker.Pages["/v4/catalog/releases/?page=2&per_page=10&order_by=name&name=deep%20house&label_id=42"] = $$"""
            {"count": 13, "results": [
              {{BrowseRelease(10, "In purchases", 42, "https://geo-media.beatport.com/image_size/1400x1400/a.jpg")}},
              {{BrowseRelease(11, "In catalog", 42, "https://evil.example/a.jpg")}},
              {{BrowseRelease(500, "New to me", 99)}},
              {"name": "no id"}
            ]}
            """;
        var catalogHash = SHA256.HashData(await File.ReadAllBytesAsync(_path));

        var page = (await client.GetFromJsonAsync<BeatportReleasePage>("/api/v1/beatport/releases?q=%20deep%20house%20&labelId=42&sort=name&page=2&pageSize=10"))!;

        Assert.Equal((13, 2, 10), (page.Total, page.Page, page.PageSize));
        Assert.Equal([10L, 11L, 500L], page.Items.Select(item => item.Id));
        var purchased = page.Items[0];
        Assert.True(purchased.InPurchaseHistory);
        Assert.True(purchased.PurchaseSourceComplete);
        Assert.Empty(purchased.CatalogReleaseIds);
        Assert.Equal(["catalog-label"], purchased.CatalogLabelIds);
        Assert.Equal("https://geo-media.beatport.com/image_size/1400x1400/a.jpg", purchased.ImageUrl);
        Assert.Equal(("EP", "CAT10", "2026-10-01", 2), (purchased.Type, purchased.CatalogNumber, purchased.PublishDate, purchased.TrackCount));
        Assert.Equal("https://www.beatport.com/release/a-release/10", purchased.BeatportUrl);
        Assert.Equal(["catalog-release"], page.Items[1].CatalogReleaseIds);
        Assert.Null(page.Items[1].ImageUrl);
        Assert.False(page.Items[2].InPurchaseHistory);
        Assert.Empty(page.Items[2].CatalogLabelIds);
        Assert.Equal(catalogHash, SHA256.HashData(await File.ReadAllBytesAsync(_path)));
    }

    [Fact]
    public async Task BeatportBrowse_ReleaseDetailOrdersTracksAndMapsMissingReleases()
    {
        await CreatePurchaseSource();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        broker.Pages["/v4/catalog/releases/500/"] = BrowseRelease(500, "New to me", 99, slug: "bad slug/..");
        broker.Pages["/v4/catalog/tracks/?release_id=500&per_page=100"] = """
            {"count": 2, "results": [
              {"id": 2, "number": 2, "name": "B", "mix_name": "Dub", "isrc": "ISRC2", "length_ms": 300000, "bpm": 124, "key": {"name": "A Minor"}, "genre": {"name": "Deep House"}, "slug": "b"},
              {"id": 1, "number": 1, "name": "A", "mix_name": "Original Mix", "artists": [{"id": 7, "name": "Artist"}], "remixers": [{"id": 8, "name": "Remixer"}], "slug": "a"}
            ]}
            """;

        var detail = (await client.GetFromJsonAsync<BeatportReleaseDetail>("/api/v1/beatport/releases/500"))!;
        Assert.Equal("https://www.beatport.com/release/-/500", detail.Release.BeatportUrl);
        Assert.Equal([1L, 2L], detail.Tracks.Select(track => track.Id));
        Assert.Equal(2, detail.TrackTotal);
        Assert.Equal(("Remixer", "https://www.beatport.com/track/a/1"), (detail.Tracks[0].Remixers.Single().Name, detail.Tracks[0].BeatportUrl));
        Assert.Equal((124, "A Minor", "Deep House", 300000), (detail.Tracks[1].Bpm, detail.Tracks[1].Key, detail.Tracks[1].Genre, detail.Tracks[1].LengthMs));

        var missing = await client.GetAsync("/api/v1/beatport/releases/404");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("beatport_release_not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task BeatportBrowse_SearchesLabelsAndReportsCatalogLabels()
    {
        await CreatePurchaseSource();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        await CatalogSql("""
            INSERT INTO label (label_id, name) VALUES ('catalog-label', 'Test label');
            INSERT INTO label_external_ref (label_id, source, external_id) VALUES ('catalog-label', 'beatport', '42');
            """);
        broker.Pages["/v4/catalog/labels/?name=test&page=1&per_page=25"] = """
            {"count": 2, "results": [{"id": 42, "name": "Test label", "slug": "test-label"}, {"id": 43, "name": "Test label two"}]}
            """;
        broker.Pages["/v4/catalog/labels/42/"] = """{"id": 42, "name": "Test label", "slug": "test-label"}""";

        var labels = (await client.GetFromJsonAsync<BeatportLabelPage>("/api/v1/beatport/labels?q=test"))!;
        Assert.Equal(2, labels.Total);
        Assert.Equal(["catalog-label"], labels.Items[0].CatalogLabelIds);
        Assert.Empty(labels.Items[1].CatalogLabelIds);
        Assert.Equal("https://www.beatport.com/label/test-label/42", labels.Items[0].BeatportUrl);
        var label = (await client.GetFromJsonAsync<BeatportLabelItem>("/api/v1/beatport/labels/42"))!;
        Assert.Equal("Test label", label.Name);
        Assert.Equal(["catalog-label"], label.CatalogLabelIds);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/beatport/labels/44")).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/beatport/releases?sort=random", "sort")]
    [InlineData("/api/v1/beatport/releases?pageSize=51", "pageSize")]
    [InlineData("/api/v1/beatport/releases?page=0", "page")]
    [InlineData("/api/v1/beatport/releases?labelId=-1", "labelId")]
    [InlineData("/api/v1/beatport/releases?q=a&q=b", "q")]
    [InlineData("/api/v1/beatport/labels", "q")]
    [InlineData("/api/v1/beatport/releases/abc", null)]
    public async Task BeatportBrowse_RejectsInvalidQueriesBeforeCallingTheBroker(string url, string? field)
    {
        await CreatePurchaseSource();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using var _ = app;
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        if (field is not null) Assert.Contains(field, problem.ToString());
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task BeatportBrowse_ReportsSignInAndConfigurationProblems()
    {
        await CreatePurchaseSource();
        var (app, client, broker) = await StartWithBrokerAsync();
        await using (app)
        {
            broker.Pages["/unused"] = "{}";
            broker.SignedIn = false;
            var response = await client.GetAsync("/api/v1/beatport/releases?q=x");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("beatport_sign_in_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        var (unconfigured, client2, _) = await StartWithBrokerAsync(configured: false);
        await using (unconfigured)
        {
            var response = await client2.GetAsync("/api/v1/beatport/releases?q=x");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("beatport_broker_not_configured", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }
}
