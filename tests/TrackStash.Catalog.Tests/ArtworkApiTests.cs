using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using TrackStash.Catalog.Api;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Tests;

public sealed partial class LabelApiTests
{
    private static byte[] ImageBytes(int width = 24, int height = 16, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }
    private async Task<HttpResponseMessage> UploadAsync(string id, string? etag, byte[]? bytes = null,
        string rights = "Permission recorded", string filename = "logo.png", string role = "logo", bool preferred = true, string? replacement = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes ?? ImageBytes()), "file", filename);
        form.Add(new StringContent(rights), "usageRights");
        form.Add(new StringContent(role), "role");
        form.Add(new StringContent(preferred ? "true" : "false"), "isPreferred");
        form.Add(new StringContent("https://example.test/label"), "sourceUrl");
        form.Add(new StringContent("Artist name"), "attribution");
        form.Add(new StringContent(filename), "caption");
        if (replacement is not null) form.Add(new StringContent(replacement), "replaceEntryId");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/labels/{id}/artwork") { Content = form };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }
    private async Task<HttpResponseMessage> GallerySaveAsync(string id, string etag, IEnumerable<ArtworkItemResponse> items)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/labels/{id}/artwork")
        {
            Content = JsonContent.Create(new { items = items.Select(i => new { i.Id, i.AssetId, i.Role, i.Caption, i.IsPreferred }) }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task ArtworkApi_UploadsProtectedOriginalAndThumbnailWithProvenanceOutsideDatabase()
    {
        var (label, etag) = await CreateAsync();
        var bytes = ImageBytes(1200, 800);
        var upload = await UploadAsync(label.Id, etag, bytes, filename: "../logo.png");
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.NotEqual(etag, upload.Headers.ETag!.ToString());
        var gallery = (await upload.Content.ReadFromJsonAsync<LabelArtworkResponse>())!;
        var item = Assert.Single(gallery.Items);
        Assert.Equal(item.OriginalUrl, upload.Headers.Location!.OriginalString);
        Assert.Equal("logo.png", item.OriginalFilename);
        Assert.Equal("image/png", item.MediaType);
        Assert.Equal(1200, item.Width);
        Assert.Equal(800, item.Height);
        Assert.Equal("Permission recorded", item.UsageRights);
        Assert.Equal("Artist name", item.Attribution);
        Assert.Equal("https://example.test/label", item.SourceUrl);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_path + ".artwork", item.AssetId, "original")));
        var original = await _client.GetAsync(item.OriginalUrl);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", original.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(original.Headers.GetValues("X-Content-Type-Options")));
        var thumbnail = await _client.GetByteArrayAsync(item.ThumbnailUrl);
        using var decoded = SKBitmap.Decode(thumbnail);
        Assert.Equal(512, decoded.Width);
        Assert.InRange(decoded.Height, 340, 342);
        using var anonymous = _app.GetTestClient();
        await ProblemAsync(await anonymous.GetAsync(item.ThumbnailUrl), 401, "unauthorized");
        await ProblemAsync(await anonymous.GetAsync(item.OriginalUrl), 401, "unauthorized");
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('artwork_asset') WHERE lower(type)='blob'";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ArtworkApi_ReordersEditsDetachesAndRejectsStaleUploadsWithoutOrphans()
    {
        var (label, originalTag) = await CreateAsync();
        var one = await UploadAsync(label.Id, originalTag);
        var firstTag = one.Headers.ETag!.ToString();
        var first = (await one.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0];
        await ProblemAsync(await UploadAsync(label.Id, originalTag), 412, "stale_version");
        Assert.Single(Directory.GetDirectories(_path + ".artwork"));
        var two = await UploadAsync(label.Id, firstTag, ImageBytes(format: SKEncodedImageFormat.Jpeg), filename: "gallery.jpg", preferred: false);
        var tag = two.Headers.ETag!.ToString();
        var items = (await two.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items;
        Assert.True(items[0].IsPreferred);
        Assert.False(items[1].IsPreferred);
        var reordered = new[] { items[1] with { Caption = "Second first", Role = "image", IsPreferred = true }, items[0] with { IsPreferred = false } };
        var saved = await GallerySaveAsync(label.Id, tag, reordered);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Second first", (await saved.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0].Caption);
        await ProblemAsync(await GallerySaveAsync(label.Id, tag, []), 412, "stale_version");
        var cleared = await GallerySaveAsync(label.Id, saved.Headers.ETag!.ToString(), []);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(first.OriginalUrl)).StatusCode);
        Assert.Equal(2, Directory.GetDirectories(_path + ".artwork").Length);
        await ProblemAsync(await PatchAsync(label.Id, """{"name":"Stale scalar edit"}""", originalTag), 412, "stale_version");
    }

    [Theory]
    [InlineData("html")]
    [InlineData("svg")]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("rights")]
    [InlineData("role")]
    public async Task ArtworkApi_RejectsInvalidUploadsWithoutChangingVersionOrCreatingFiles(string kind)
    {
        var (label, etag) = await CreateAsync();
        var bytes = kind switch
        {
            "html" => Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),
            "svg" => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
            "empty" => [],
            "truncated" => ImageBytes()[..30],
            _ => ImageBytes(),
        };
        await ProblemAsync(await UploadAsync(label.Id, etag, bytes, rights: kind == "rights" ? "" : "Unverified permission",
            role: kind == "role" ? "front_cover" : "logo"), 400, "validation_failed");
        Assert.Equal(etag, (await _client.GetAsync($"/api/v1/labels/{label.Id}")).Headers.ETag!.ToString());
        Assert.False(Directory.Exists(_path + ".artwork"));
    }

    [Fact]
    public async Task ArtworkApi_EnforcesPreconditionsBodyLimitsAndMissingStorageErrors()
    {
        var (label, etag) = await CreateAsync();
        await ProblemAsync(await UploadAsync(label.Id, null), 428, "precondition_required");
        await ProblemAsync(await UploadAsync("missing", etag), 404, "label_not_found");
        await ProblemAsync(await UploadAsync(label.Id, etag, new byte[10 * 1024 * 1024 + 64 * 1024]), 413, "body_too_large");
        var upload = await UploadAsync(label.Id, etag);
        var item = (await upload.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0];
        File.Delete(Path.Combine(_path + ".artwork", item.AssetId, "original"));
        await ProblemAsync(await _client.GetAsync(item.OriginalUrl), 409, "artwork_unavailable");
        var bad = await _client.GetAsync("/api/v1/artwork-assets/0123456789abcdef0123456789abcdef/original");
        await ProblemAsync(bad, 404, "artwork_not_found");
    }

    [Fact]
    public async Task ArtworkApi_MergePreservesBothGalleriesAndRedirectsRetiredGallery()
    {
        var survivor = await CreateAsync("Kept");
        var retired = await CreateAsync("Duplicate");
        var kept = (await (await UploadAsync(survivor.Label.Id, survivor.ETag, filename: "kept.png")).Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0];
        var moved = (await (await UploadAsync(retired.Label.Id, retired.ETag, filename: "moved.png")).Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0];
        var preview = await MergePreviewAsync(survivor.Label.Id, retired.Label.Id);
        Assert.Empty(preview.Preview.Blockers);
        Assert.Equal(new[] { kept.AssetId, moved.AssetId }, preview.Preview.Artwork.Select(i => i.AssetId));
        Assert.Equal(HttpStatusCode.OK, (await MergeApplyAsync(preview.Preview.OperationId, preview.ETag)).StatusCode);
        var gallery = (await _client.GetFromJsonAsync<LabelArtworkResponse>($"/api/v1/labels/{survivor.Label.Id}/artwork"))!;
        Assert.Equal(new[] { kept.AssetId, moved.AssetId }, gallery.Items.Select(i => i.AssetId));
        Assert.True(gallery.Items[0].IsPreferred);
        Assert.False(gallery.Items[1].IsPreferred);
        var redirect = await _client.GetAsync($"/api/v1/labels/{retired.Label.Id}/artwork");
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        await ProblemAsync(await UploadAsync(retired.Label.Id, retired.ETag), 409, "label_retired");
    }

    [Fact]
    public async Task ArtworkApi_DocumentsMultipartAndImageRoutesWithOriginalPrecondition()
    {
        var schema = await _client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = schema.GetProperty("paths");
        var post = paths.GetProperty("/api/v1/labels/{id}/artwork").GetProperty("post");
        Assert.True(post.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
        Assert.Contains(post.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "If-Match");
        Assert.True(paths.TryGetProperty("/api/v1/artwork-assets/{assetId}/thumbnail", out _));
        Assert.True(paths.TryGetProperty("/api/v1/artwork-assets/{assetId}/original", out _));
    }

    [Fact]
    public async Task ArtworkApi_ReplacesInPlaceAndRetainsOldContentWhileDatabaseFailuresCleanUpFiles()
    {
        var (label, etag) = await CreateAsync();
        var upload = await UploadAsync(label.Id, etag);
        var original = (await upload.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0];
        var replaced = await UploadAsync(label.Id, upload.Headers.ETag!.ToString(), ImageBytes(32, 32),
            filename: "new-logo.png", replacement: original.Id);
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);
        var current = (await replaced.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0];
        Assert.Equal(original.Id, current.Id);
        Assert.NotEqual(original.AssetId, current.AssetId);
        Assert.Equal("new-logo.png", current.Caption);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(original.OriginalUrl)).StatusCode);
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_upload BEFORE INSERT ON label_artwork_revision BEGIN SELECT RAISE(ABORT,'Injected failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await ProblemAsync(await UploadAsync(label.Id, replaced.Headers.ETag!.ToString()), 500, "internal_error");
        Assert.Equal(2, Directory.GetDirectories(_path + ".artwork").Length);
        var read = await _client.GetAsync($"/api/v1/labels/{label.Id}/artwork");
        Assert.Equal(replaced.Headers.ETag.ToString(), read.Headers.ETag!.ToString());
        Assert.Single((await read.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items);
    }

    [Fact]
    public async Task ArtworkApi_RejectsMalformedGalleryFieldsAndCrossOriginImageReads()
    {
        var (label, etag) = await CreateAsync();
        using var malformed = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/labels/{label.Id}/artwork")
        {
            Content = new StringContent("""{"items":[{"id":"one","id":"duplicate","assetId":"asset","role":"logo","caption":null,"isPreferred":true}]}""", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.TryAddWithoutValidation("If-Match", etag);
        await ProblemAsync(await _client.SendAsync(malformed), 400, "validation_failed");
        var upload = await UploadAsync(label.Id, etag);
        var item = (await upload.Content.ReadFromJsonAsync<LabelArtworkResponse>())!.Items[0];
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, item.ThumbnailUrl);
        crossOrigin.Headers.Add("Origin", "https://example.test");
        await ProblemAsync(await _client.SendAsync(crossOrigin), 403, "origin_not_allowed");
        await ProblemAsync(await GallerySaveAsync(label.Id, upload.Headers.ETag!.ToString(),
            [item, item with { Id = "another", IsPreferred = true }]), 400, "validation_failed");
    }
}

public sealed class ArtworkImageProcessorTests
{
    [Fact]
    public async Task AnimatedPng_IsExplicitlyRejectedAndJpegOrientationIsAppliedToThumbnails()
    {
        static byte[] Encode(SKEncodedImageFormat format)
        {
            using var bitmap = new SKBitmap(32, 16);
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, 90);
            return data.ToArray();
        }
        var png = Encode(SKEncodedImageFormat.Png);
        var chunk = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), 8);
        "acTL"u8.CopyTo(chunk.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8, 4), 1);
        uint crc = 0xffffffff;
        for (var index = 4; index < 16; index++)
        {
            crc ^= chunk[index];
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16, 4), ~crc);
        var animated = png[..33].Concat(chunk).Concat(png[33..]).ToArray();
        var processor = new ArtworkImageProcessor();
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(animated).AsTask());
        Assert.Contains("Animated PNG", failure.Message);
        var jpeg = Encode(SKEncodedImageFormat.Jpeg);
        byte[] exif = [0xff,0xe1,0,34, 69,120,105,102,0,0, 73,73,42,0,8,0,0,0,
            1,0, 18,1,3,0,1,0,0,0,6,0,0,0, 0,0,0,0];
        var rotated = await processor.ProcessAsync(jpeg[..2].Concat(exif).Concat(jpeg[2..]).ToArray());
        Assert.Equal(16, rotated.Width);
        Assert.Equal(32, rotated.Height);
        using var thumb = SKBitmap.Decode(rotated.Thumbnail);
        Assert.Equal(16, thumb.Width);
        Assert.Equal(32, thumb.Height);
    }

    [Fact]
    public async Task PixelAndByteLimits_AreMeasuredAtTheirActualThresholds()
    {
        var processor = new ArtworkImageProcessor();
        static byte[] Png(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        var boundary = await processor.ProcessAsync(Png(5000, 5000));
        Assert.Equal(25_000_000L, (long)boundary.Width * boundary.Height);
        var tooLarge = await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(Png(5001, 5000)).AsTask());
        Assert.Contains("25 megapixels", tooLarge.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(new byte[10 * 1024 * 1024 + 1]).AsTask());
        var bytes = Png(1, 1);
        var padded = new byte[10 * 1024 * 1024];
        bytes.CopyTo(padded, 0);
        Assert.Equal(1, (await processor.ProcessAsync(padded)).Width);
    }
}
