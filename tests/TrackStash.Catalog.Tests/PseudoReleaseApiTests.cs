using System.Net;
using System.Net.Http.Json;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    [Fact]
    public async Task PseudoRelease_CreatesAndLinksAtomicallyAndReplays()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET title='Private edit',artist='New act',isrc=NULL");
        var (plan, _) = await MatchPlan(fixture, 1);
        var request = new CreatePseudoReleaseRequest(Guid.NewGuid().ToString("N"), plan.Version, "My edits", null,
            [new(1, null, null, "Private edit", null, null, "New act")]);
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var outcome = (await response.Content.ReadFromJsonAsync<PseudoReleaseOutcome>())!;
        Assert.Equal(1, outcome.Recordings);
        Assert.Equal(1, outcome.MediaFiles);
        Assert.Equal(1, await ImportCount("media_file_recording_match"));
        var release = (await fixture.Client.GetFromJsonAsync<ReleaseResponse>("/api/v1/releases/" + outcome.ReleaseId))!;
        Assert.Equal("NO CAT", release.CatalogNumber);
        Assert.Contains(release.Labels, l => l.Name == "Not on Label");
        File.Delete(fixture.File);
        var replay = await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(outcome, await replay.Content.ReadFromJsonAsync<PseudoReleaseOutcome>());
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases",
            request with { Title = "Different" }), 409, "pseudo_operation_conflict");
    }

    [Fact]
    public async Task PseudoRelease_RejectsStaleScannerWithoutWrites()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET isrc=NULL");
        var (plan, _) = await MatchPlan(fixture, 1);
        await ScannerSql(fixture, "UPDATE metadata SET title='Changed'");
        var request = new CreatePseudoReleaseRequest(Guid.NewGuid().ToString("N"), plan.Version, "My edits", "CAT-1",
            [new(1, null, null, "Private edit", null, null, null)]);
        var before = await ImportCount("release");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request), 412, "enrichment_state_changed");
        Assert.Equal(before, await ImportCount("release"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
    }

    [Fact]
    public async Task PseudoRelease_CopiesShareOneRecordingAndCustomCatalogNumber()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture);
        await ScannerSql(fixture, "UPDATE metadata SET title='Private edit',artist='New act',isrc=NULL");
        var (plan, _) = await MatchPlan(fixture, 1, 2);
        var request = new CreatePseudoReleaseRequest(Guid.NewGuid().ToString("N"), plan.Version, "My edits", "MINE-001",
            [new(1, null, null, "Private edit", null, null, "New act"), new(2, null, 1, null, null, null, null)]);
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var outcome = (await response.Content.ReadFromJsonAsync<PseudoReleaseOutcome>())!;
        Assert.Equal((1, 2), (outcome.Recordings, outcome.MediaFiles));
        Assert.Equal(2, await ImportCount("media_file_recording_match"));
        var release = (await fixture.Client.GetFromJsonAsync<ReleaseResponse>("/api/v1/releases/" + outcome.ReleaseId))!;
        Assert.Equal("MINE-001", release.CatalogNumber);
    }

    [Fact]
    public async Task PseudoRelease_AmbiguousArtistRollsBackReleaseAndLinks()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET title='Private edit',isrc=NULL");
        var (plan, _) = await MatchPlan(fixture, 1);
        var request = new CreatePseudoReleaseRequest(Guid.NewGuid().ToString("N"), plan.Version, "My edits", null,
            [new(1, null, null, "Private edit", null, null, "Zed Primary")]);
        var before = await ImportCount("release");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request), 409, "pseudo_artist_ambiguous");
        Assert.Equal(before, await ImportCount("release"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
    }

    [Fact]
    public async Task PseudoRelease_ChangedBytesFailWithoutCreatingCatalogEntities()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET isrc=NULL");
        var (plan, _) = await MatchPlan(fixture, 1);
        await File.WriteAllBytesAsync(fixture.File, [4, 3, 2, 1]);
        var request = new CreatePseudoReleaseRequest(Guid.NewGuid().ToString("N"), plan.Version, "My edits", null,
            [new(1, null, null, "Private edit", null, null, null)]);
        var before = await ImportCount("release");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request), 409, "media_changed");
        Assert.Equal(before, await ImportCount("release"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
    }

    [Fact]
    public async Task PseudoRelease_IdenticalBytesCannotBeLinkedToDifferentNewRecordings()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture, identicalBytes: true);
        await ScannerSql(fixture, "UPDATE metadata SET isrc=NULL");
        var (plan, _) = await MatchPlan(fixture, 1, 2);
        var request = new CreatePseudoReleaseRequest(Guid.NewGuid().ToString("N"), plan.Version, "My edits", null,
            [new(1, null, null, "First", null, null, null), new(2, null, null, "Second", null, null, null)]);
        var before = await ImportCount("release");
        await ProblemAsync(await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request), 409, "media_identity_conflict");
        Assert.Equal(before, await ImportCount("release"));
        Assert.Equal(0, await ImportCount("media_file_recording_match"));
    }

    [Fact]
    public async Task PseudoRelease_ReusesLegacyNotOnLabelRatherThanCreatingDuplicate()
    {
        await using var fixture = await StartMedia();
        await ImportSql("INSERT INTO label(label_id,name) VALUES('pseudo-label','Not on Label')");
        await ScannerSql(fixture, "UPDATE metadata SET isrc=NULL");
        var (plan, _) = await MatchPlan(fixture, 1);
        var before = await ImportCount("label");
        var request = new CreatePseudoReleaseRequest(Guid.NewGuid().ToString("N"), plan.Version, "My edits", null,
            [new(1, null, null, "Private edit", null, null, null)]);
        Assert.Equal(HttpStatusCode.Created, (await fixture.Client.PostAsJsonAsync("/api/v1/media/pseudo-releases", request)).StatusCode);
        Assert.Equal(before, await ImportCount("label"));
    }
}
