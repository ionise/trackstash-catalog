using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static async Task<(MediaLibrarySettingsResponse Settings, string Tag)> LibrarySettings(MediaFixture fixture)
    {
        var response = await fixture.Client.GetAsync("/api/v1/media-libraries");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return ((await response.Content.ReadFromJsonAsync<MediaLibrarySettingsResponse>())!, response.Headers.ETag!.ToString());
    }
    private static async Task<HttpResponseMessage> SaveLibraries(MediaFixture fixture, IEnumerable<MediaLibrarySetting> libraries,
        string? tag, bool confirmed = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/media-libraries") {
            Content = JsonContent.Create(new { libraries, confirmed })
        };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }
    [Fact]
    public async Task Media_LibraryNamesPersistAndRenamePreservesRecordingReviewAndSourceIdentity()
    {
        await using var fixture = await StartMedia();
        var (settings, tag) = await LibrarySettings(fixture);
        var original = Assert.Single(settings.Libraries);
        Assert.Equal("Test", original.Name);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "libraries.json")));
        var (review, reviewTag) = await PrepareMedia(fixture);
        var changed = new[] { original with { Name = "My entire collection" } };
        var save = await SaveLibraries(fixture, changed, tag);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.NotEqual(tag, save.Headers.ETag!.ToString());
        var page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?library=Test"))!;
        Assert.Equal("My entire collection", page.LibraryNames!["Test"]);
        Assert.Equal("Test", Assert.Single(page.Items).Library);
        Assert.Equal(HttpStatusCode.OK, (await AcceptMedia(fixture, review, reviewTag)).StatusCode);
        var restarted = new MediaLibrarySettings(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["CatalogApi:MediaLibrarySettingsPath"] = Path.Combine(fixture.Root, "libraries.json"),
            ["CatalogApi:MediaLibraries:Ignored"] = "/other"
        }).Build());
        Assert.Equal("My entire collection", Assert.Single(restarted.Read().Libraries).Name);
        Assert.Equal(original.Id, Assert.Single(restarted.Read().Libraries).Id);
    }
    [Fact]
    public async Task Media_LibrariesCanBeAddedDisabledAndRelocatedWithoutScanningOrChangingCatalog()
    {
        await using var fixture = await StartMedia();
        var (settings, tag) = await LibrarySettings(fixture);
        var original = Assert.Single(settings.Libraries);
        var added = new MediaLibrarySetting("second-location", "Future archive", Path.Combine(fixture.Root, "Archive"), true);
        await ScannerSql(fixture, $"""
            INSERT INTO media_file SELECT 2,'{added.Root}/copy.aiff',content_hash,duration_seconds,size_bytes,scanned_utc,last_modified_utc FROM media_file WHERE media_file_id=1;
            INSERT INTO metadata SELECT 2,title,subtitle,artist,album,isrc FROM metadata WHERE media_file_id=1;
            """);
        Assert.Equal(HttpStatusCode.OK, (await SaveLibraries(fixture, [original, added], tag)).StatusCode);
        var page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media?library=second-location"))!;
        Assert.Equal(2, Assert.Single(page.Items).Id);
        Assert.Equal("Future archive", page.LibraryNames!["second-location"]);
        var (current, currentTag) = await LibrarySettings(fixture);
        Assert.Equal(HttpStatusCode.OK, (await SaveLibraries(fixture,
            current.Libraries.Select(l => l.Id == original.Id ? l with { Enabled = false } : l), currentTag)).StatusCode);
        page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media"))!;
        Assert.Equal(2, Assert.Single(page.Items).Id);
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media?library=Test"), 400, "validation_failed");
        (current, currentTag) = await LibrarySettings(fixture);
        Assert.Equal(HttpStatusCode.OK, (await SaveLibraries(fixture,
            current.Libraries.Select(l => l.Id == added.Id ? l with { Root = Path.Combine(fixture.Root, "Relocated") } : l), currentTag)).StatusCode);
        page = (await fixture.Client.GetFromJsonAsync<MediaInventoryPage>("/api/v1/media"))!;
        Assert.Empty(page.Items);
        Assert.Equal(0, await ImportCount("media_file"));
        Assert.True(File.Exists(fixture.File));
    }
    [Fact]
    public async Task Media_LibrarySettingsRequireAuthConfirmationOriginalEtagAndPreservedIds()
    {
        await using var fixture = await StartMedia();
        using var anonymous = fixture.App.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/media-libraries")).StatusCode);
        var (settings, tag) = await LibrarySettings(fixture);
        await ProblemAsync(await SaveLibraries(fixture, settings.Libraries, null), 428, "precondition_required");
        await ProblemAsync(await SaveLibraries(fixture, settings.Libraries, tag, confirmed: false), 400, "validation_failed");
        await ProblemAsync(await SaveLibraries(fixture, [], tag), 400, "validation_failed");
        var renamed = settings.Libraries.Select(l => l with { Name = "Renamed" }).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await SaveLibraries(fixture, renamed, tag)).StatusCode);
        await ProblemAsync(await SaveLibraries(fixture, settings.Libraries, tag), 412, "media_settings_changed");
        Assert.Equal("Renamed", Assert.Single((await LibrarySettings(fixture)).Settings.Libraries).Name);
    }
    [Theory]
    [InlineData("relative")]
    [InlineData("root")]
    [InlineData("duplicateName")]
    [InlineData("duplicateRoot")]
    [InlineData("duplicateId")]
    [InlineData("blankName")]
    [InlineData("tooMany")]
    public async Task Media_LibrarySettingsRejectInvalidOrAmbiguousEntries(string change)
    {
        await using var fixture = await StartMedia();
        var (settings, tag) = await LibrarySettings(fixture);
        var original = Assert.Single(settings.Libraries);
        var entries = change switch {
            "relative" => new[] { original with { Root = "relative/library" } },
            "root" => new[] { original with { Root = "/" } },
            "duplicateName" => new[] { original, new("new", original.Name.ToLowerInvariant(), "/other", true) },
            "duplicateRoot" => new[] { original, new("new", "Other", original.Root, true) },
            "duplicateId" => new[] { original, original with { Name = "Other", Root = "/other" } },
            "blankName" => new[] { original with { Name = " " } },
            _ => Enumerable.Range(0, 33).Select(i => new MediaLibrarySetting("id-" + i, "Name " + i, "/location/" + i, true)).ToArray()
        };
        await ProblemAsync(await SaveLibraries(fixture, entries, tag), 400, "validation_failed");
        Assert.False(File.Exists(Path.Combine(fixture.Root, "libraries.json")));
    }
    [Fact]
    public async Task Media_LibrarySettingsRejectUnknownRepeatedFieldsAndDoNotHideCorruptSavedConfig()
    {
        await using var fixture = await StartMedia();
        var (settings, tag) = await LibrarySettings(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/media-libraries") {
            Content = new StringContent("""{"libraries":[{"id":"Test","id":"Other","name":"Test","root":"/somewhere","enabled":true}],"confirmed":true}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        await ProblemAsync(await fixture.Client.SendAsync(request), 400, "validation_failed");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "libraries.json"), "broken");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media-libraries"), 409, "media_settings_invalid");
        await ProblemAsync(await fixture.Client.GetAsync("/api/v1/media"), 409, "media_settings_invalid");
        await ProblemAsync(await SaveLibraries(fixture, settings.Libraries, tag), 409, "media_settings_invalid");
        Assert.Equal("broken", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "libraries.json")));
    }
}
