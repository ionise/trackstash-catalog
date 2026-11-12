using System.Net;
using System.Net.Http.Json;
using TrackStash.Catalog.Api;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static async Task<HttpResponseMessage> PrepareManual(MediaFixture fixture, string recordingId, long mediaId = 1)
        => await fixture.Client.PostAsJsonAsync("/api/v1/manual-media-reviews", new { mediaId, recordingId });

    private static async Task<HttpResponseMessage> ApplyManual(MediaFixture fixture, string id, string tag, bool confirmed = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/manual-media-reviews/{id}/apply") {
            Content = JsonContent.Create(new { confirmed })
        };
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await fixture.Client.SendAsync(request);
    }

    [Fact]
    public async Task ManualMedia_ConflictingMetadataCanBeExplicitlyLinkedAndReassignedWithHistory()
    {
        await using var fixture = await StartMedia();
        await ScannerSql(fixture, "UPDATE metadata SET title='Wrong title',artist=NULL,isrc=NULL,subtitle=NULL");
        var firstResponse = await PrepareManual(fixture, "t-shared");
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<ManualMediaReview>())!;
        Assert.Contains("Tagged title conflicts.", first.Candidate.Blockers);
        Assert.Null(first.PreviousRecordingId);
        var firstTag = firstResponse.Headers.ETag!.ToString();
        await ProblemAsync(await ApplyManual(fixture, first.Id, firstTag, false), 400, "validation_failed");
        var accepted = await ApplyManual(fixture, first.Id, firstTag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(await accepted.Content.ReadAsStringAsync(), await (await ApplyManual(fixture, first.Id, firstTag)).Content.ReadAsStringAsync());
        var secondResponse = await PrepareManual(fixture, "t-second");
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = (await secondResponse.Content.ReadFromJsonAsync<ManualMediaReview>())!;
        Assert.Equal("t-shared", second.PreviousRecordingId);
        await ProblemAsync(await fixture.Client.GetAsync($"/api/v1/enrichment-reviews/{second.Id}"), 409, "media_review_required");
        Assert.Equal(HttpStatusCode.OK, (await ApplyManual(fixture, second.Id, secondResponse.Headers.ETag!.ToString())).StatusCode);
        Assert.Empty((await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!);
        Assert.Single((await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-second/media"))!);
        Assert.Equal(2, await ImportCount("enrichment_apply_revision"));
        var recovered = (await fixture.Client.GetFromJsonAsync<ManualMediaReview>($"/api/v1/manual-media-reviews/{second.Id}"))!;
        Assert.Equal("t-second", recovered.Outcome!.RecordingId);
        Assert.Equal("t-shared", recovered.PreviousRecordingId);
    }

    [Fact]
    public async Task ManualMedia_StaleReviewCannotOverwriteAnotherAssignment()
    {
        await using var fixture = await StartMedia();
        var first = await PrepareManual(fixture, "t-shared");
        var stale = await PrepareManual(fixture, "t-second");
        var firstReview = (await first.Content.ReadFromJsonAsync<ManualMediaReview>())!;
        var staleReview = (await stale.Content.ReadFromJsonAsync<ManualMediaReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await ApplyManual(fixture, firstReview.Id, first.Headers.ETag!.ToString())).StatusCode);
        await ProblemAsync(await ApplyManual(fixture, staleReview.Id, stale.Headers.ETag!.ToString()), 412, "enrichment_state_changed");
        Assert.Single((await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!);
    }

    [Fact]
    public async Task ManualMedia_ReassignmentRequiresOriginalBytesAndAvailableFile()
    {
        await using var fixture = await StartMedia();
        var first = await PrepareManual(fixture, "t-shared");
        var firstReview = (await first.Content.ReadFromJsonAsync<ManualMediaReview>())!;
        Assert.Equal(HttpStatusCode.OK, (await ApplyManual(fixture, firstReview.Id, first.Headers.ETag!.ToString())).StatusCode);
        await File.WriteAllBytesAsync(fixture.File, [9, 8, 7, 6]);
        await ProblemAsync(await PrepareManual(fixture, "t-second"), 409, "media_changed");
        File.Delete(fixture.File);
        await ProblemAsync(await PrepareManual(fixture, "t-second"), 409, "media_unavailable");
        Assert.Single((await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!);
    }

    [Fact]
    public async Task ManualMedia_DoesNotReassignOnlyOneOfTwoIdenticalLinkedCopies()
    {
        await using var fixture = await StartMedia();
        await AddBrowseMetadata(fixture);
        await AddReconciliationCopy(fixture, identicalBytes: true);
        foreach (var id in new long[] { 1, 2 })
        {
            var prepared = await PrepareManual(fixture, "t-shared", id);
            Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
            var review = (await prepared.Content.ReadFromJsonAsync<ManualMediaReview>())!;
            Assert.Equal(HttpStatusCode.OK, (await ApplyManual(fixture, review.Id, prepared.Headers.ETag!.ToString())).StatusCode);
        }
        await ProblemAsync(await PrepareManual(fixture, "t-second"), 409, "media_identity_conflict");
        Assert.Equal(2, (await fixture.Client.GetFromJsonAsync<LinkedRecordingMedia[]>("/api/v1/recordings/t-shared/media"))!.Length);
    }
}
