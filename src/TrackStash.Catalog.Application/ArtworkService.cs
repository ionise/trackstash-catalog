using System.Security.Cryptography;
using System.Buffers.Binary;
using SkiaSharp;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

public sealed record ProcessedArtwork(string MediaType, int Width, int Height, byte[] Thumbnail);
public sealed record ArtworkUpload(byte[] Content, string Filename, string? SourceUrl, string? Attribution,
    string UsageRights, string Role, string? Caption, bool IsPreferred, string? ReplaceEntryId = null, ArtworkSourceIdentity? Source = null);
public sealed record ArtworkItemDetail(ArtworkEntry Entry, ArtworkAsset Asset);
public sealed record ArtworkDetail(string EntityId, string Version, IReadOnlyList<ArtworkItemDetail> Items);
public sealed record ArtworkWriteResult(LabelUpdateStatus Status, ArtworkDetail? Gallery = null, string? CreatedAssetId = null);
public sealed record ArtworkContent(byte[] Bytes, string MediaType);
public sealed class ArtworkUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

public interface IArtworkImageProcessor
{
    ValueTask<ProcessedArtwork> ProcessAsync(byte[] content, CancellationToken ct = default);
}

public sealed class ArtworkImageProcessor : IArtworkImageProcessor
{
    private static readonly SemaphoreSlim DecodeSlots = new(2);

    public async ValueTask<ProcessedArtwork> ProcessAsync(byte[] content, CancellationToken ct = default)
    {
        if (content.Length == 0 || content.Length > ArtworkRules.MaximumBytes)
            throw new ArgumentException("Upload a JPEG or PNG of at most 10 MiB.", nameof(content));
        await DecodeSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var data = SKData.CreateCopy(content);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
                throw new ArgumentException("Only JPEG and PNG images are supported.", nameof(content));
            if (codec.EncodedFormat == SKEncodedImageFormat.Png) ValidateStaticPng(content);
            var info = codec.Info;
            if (info.Width < 1 || info.Height < 1 || info.Width > 16384 || info.Height > 16384
                || (long)info.Width * info.Height > ArtworkRules.MaximumPixels || codec.FrameCount > 1)
                throw new ArgumentException("Use one static image of at most 25 megapixels, with sides no larger than 16384 pixels.", nameof(content));
            using var bitmap = new SKBitmap(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new ArgumentException("The image could not be fully decoded.", nameof(content));
            ct.ThrowIfCancellationRequested();
            var rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var width = rotated ? info.Height : info.Width;
            var height = rotated ? info.Width : info.Height;
            var scale = Math.Min(1d, 512d / Math.Max(width, height));
            using var thumbnail = new SKBitmap(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            using (var canvas = new SKCanvas(thumbnail))
            {
                canvas.Clear(SKColors.White);
                canvas.Scale((float)scale);
                switch (codec.EncodedOrigin)
                {
                    case SKEncodedOrigin.TopRight: canvas.Translate(width, 0); canvas.Scale(-1, 1); break;
                    case SKEncodedOrigin.BottomRight: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
                    case SKEncodedOrigin.BottomLeft: canvas.Translate(0, height); canvas.Scale(1, -1); break;
                    case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                    case SKEncodedOrigin.RightTop: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
                    case SKEncodedOrigin.RightBottom: canvas.Translate(width, height); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                    case SKEncodedOrigin.LeftBottom: canvas.Translate(0, height); canvas.RotateDegrees(270); break;
                }
                canvas.DrawBitmap(bitmap, 0, 0);
            }
            using var image = SKImage.FromBitmap(thumbnail);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return new(codec.EncodedFormat == SKEncodedImageFormat.Jpeg ? "image/jpeg" : "image/png", width, height, encoded.ToArray());
        }
        finally { DecodeSlots.Release(); }
    }

    private static void ValidateStaticPng(byte[] bytes)
    {
        var offset = 8;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > bytes.Length - offset - 12) break;
            var type = bytes.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8)) throw new ArgumentException("Animated PNG is not supported.", nameof(bytes));
            if (type.SequenceEqual("IEND"u8)) return;
            offset += (int)length + 12;
        }
        throw new ArgumentException("Incomplete PNG image.", nameof(bytes));
    }
}

public interface IArtworkService
{
    ValueTask<ArtworkDetail?> GetArtistAsync(string id, CancellationToken ct = default);
    ValueTask<ArtworkWriteResult> UploadArtistAsync(string id, string version, ArtworkUpload upload, CancellationToken ct = default);
    ValueTask<ArtworkWriteResult> ReplaceArtistAsync(string id, string version, IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default);
    ValueTask<ArtworkDetail?> GetLabelAsync(string id, CancellationToken ct = default);
    ValueTask<ArtworkWriteResult> UploadLabelAsync(string id, string expectedVersion, ArtworkUpload upload, CancellationToken ct = default);
    ValueTask<ArtworkWriteResult> ReplaceLabelAsync(string id, string expectedVersion, IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default);
    ValueTask<ArtworkContent?> ReadAsync(string assetId, bool thumbnail, CancellationToken ct = default);
    ValueTask<ArtworkDetail?> GetReleaseAsync(string id, CancellationToken ct = default);
    ValueTask<ArtworkWriteResult> UploadReleaseAsync(string id, string expectedVersion, ArtworkUpload upload, CancellationToken ct = default);
    ValueTask<ArtworkWriteResult> ReplaceReleaseAsync(string id, string expectedVersion, IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default);
}

public sealed class ArtworkService(IStorageProvider storage, IArtworkContentStore files, IArtworkImageProcessor images) : IArtworkService
{
    private sealed record GalleryState(string Id, string Version, IReadOnlyList<ArtworkEntry> Items);
    private async ValueTask RequireReleaseSchema(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 11)
            throw new ReleaseArtworkUnavailableException("Release artwork requires schema 11. Migrate only a separately approved catalog.");
    }
    private static async ValueTask<GalleryState?> GalleryAsync(IUnitOfWork unit, string id, bool release, CancellationToken ct, bool artist = false)
    {
        if (artist)
        {
            var gallery = await unit.Artwork.GetArtistGalleryAsync(id, ct).ConfigureAwait(false);
            return gallery is null ? null : new(id, gallery.Version, gallery.Items);
        }
        if (release)
        {
            var gallery = await unit.Artwork.GetReleaseGalleryAsync(id, ct).ConfigureAwait(false);
            return gallery is null ? null : new(id, gallery.Version, gallery.Items);
        }
        var label = await unit.Artwork.GetLabelGalleryAsync(id, ct).ConfigureAwait(false);
        return label is null ? null : new(id, label.Version, label.Items);
    }
    private static async ValueTask<LabelUpdateStatus> SaveGalleryAsync(IUnitOfWork unit, string id, string version, IReadOnlyList<ArtworkEntry> items, bool release, CancellationToken ct, ArtworkSourceIdentity? source = null, bool artist = false)
        => artist ? (await unit.Artwork.ReplaceArtistGalleryAsync(id, version, items, ct).ConfigureAwait(false)).Status
            : release ? (await unit.Artwork.ReplaceReleaseGalleryAsync(id, version, items, ct, source).ConfigureAwait(false)).Status
            : (await unit.Artwork.ReplaceLabelGalleryAsync(id, version, items, ct).ConfigureAwait(false)).Status;
    public ValueTask<ArtworkDetail?> GetLabelAsync(string id, CancellationToken ct = default) => GetAsync(id, false, ct);
    public ValueTask<ArtworkDetail?> GetReleaseAsync(string id, CancellationToken ct = default) => GetAsync(id, true, ct);
    public ValueTask<ArtworkDetail?> GetArtistAsync(string id, CancellationToken ct = default) => GetAsync(id, false, ct, true);
    private async ValueTask RequireArtistSchema(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct).ConfigureAwait(false) < 12)
            throw new ArtistProfileUnavailableException();
    }
    private async ValueTask<ArtworkDetail?> GetAsync(string id, bool release, CancellationToken ct, bool artist = false)
    {
        if (artist) await RequireArtistSchema(ct).ConfigureAwait(false);
        if (release) await RequireReleaseSchema(ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var gallery = await GalleryAsync(unit, id, release, ct, artist).ConfigureAwait(false);
        return gallery is null ? null : await DetailsAsync(unit, gallery, ct).ConfigureAwait(false);
    }

    private static async ValueTask<ArtworkDetail> DetailsAsync(IUnitOfWork unit, GalleryState gallery, CancellationToken ct)
    {
        var items = new List<ArtworkItemDetail>();
        foreach (var item in gallery.Items)
            items.Add(new(item, await unit.Artwork.GetAssetAsync(item.AssetId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Artwork association has no asset.")));
        return new(gallery.Id, gallery.Version, items);
    }

    public ValueTask<ArtworkWriteResult> UploadLabelAsync(string id, string expectedVersion, ArtworkUpload upload, CancellationToken ct = default)
        => UploadAsync(id, expectedVersion, upload, false, ct);
    public ValueTask<ArtworkWriteResult> UploadReleaseAsync(string id, string expectedVersion, ArtworkUpload upload, CancellationToken ct = default)
        => UploadAsync(id, expectedVersion, upload, true, ct);
    public ValueTask<ArtworkWriteResult> UploadArtistAsync(string id, string version, ArtworkUpload upload, CancellationToken ct = default)
        => UploadAsync(id, version, upload, false, ct, true);
    private async ValueTask<ArtworkWriteResult> UploadAsync(string id, string expectedVersion, ArtworkUpload upload, bool release, CancellationToken ct, bool artist = false)
    {
        if (artist) await RequireArtistSchema(ct).ConfigureAwait(false);
        if (release) await RequireReleaseSchema(ct).ConfigureAwait(false);
        ArtworkRules.ValidateSource(upload.Source);
        if (!release && upload.Source is not null) throw new ArgumentException("Provider candidate attachment is currently release-only.", nameof(upload));
        if (string.IsNullOrWhiteSpace(upload.UsageRights) || upload.UsageRights.Length > 2000
            || upload.Attribution?.Length > 1000 || upload.Filename.Length > 255)
            throw new ArgumentException("Record usage rights (up to 2000 characters), attribution (1000), and filename (255).", nameof(upload));
        if (upload.SourceUrl is not null) LabelProfile.ValidateUrl(upload.SourceUrl, nameof(upload.SourceUrl));
        var assetId = Guid.NewGuid().ToString("N");
        var entry = new ArtworkEntry(upload.ReplaceEntryId ?? Guid.NewGuid().ToString("N"), assetId, upload.Role, upload.Caption, upload.IsPreferred);
        ArtworkRules.ValidateGallery([entry], release, artist);
        var processed = await images.ProcessAsync(upload.Content, ct).ConfigureAwait(false);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var before = await GalleryAsync(unit, id, release, ct, artist).ConfigureAwait(false);
        if (before is null) return new(LabelUpdateStatus.NotFound);
        if (before.Version != expectedVersion) return new(LabelUpdateStatus.Stale);
        if (upload.ReplaceEntryId is not null && !before.Items.Any(item => item.Id == upload.ReplaceEntryId))
            throw new ArgumentException("The artwork entry to replace is not in this gallery.", nameof(upload.ReplaceEntryId));
        var entries = before.Items.Select(i => i.Id == upload.ReplaceEntryId ? entry
            : i with { IsPreferred = i.IsPreferred && !entry.IsPreferred });
        if (upload.ReplaceEntryId is null) entries = entries.Append(entry);
        var orderedEntries = entries.ToArray();
        ArtworkRules.ValidateGallery(orderedEntries, release, artist);
        var asset = new ArtworkAsset(assetId, Convert.ToHexString(SHA256.HashData(upload.Content)), processed.MediaType,
            upload.Content.Length, processed.Width, processed.Height, Path.GetFileName(upload.Filename.Replace('\\', '/')),
            upload.SourceUrl, upload.Attribution, upload.UsageRights, DateTimeOffset.UtcNow);
        await files.SaveAsync(assetId, upload.Content, processed.Thumbnail, ct).ConfigureAwait(false);
        try
        {
            await unit.Artwork.AddAssetAsync(asset, ct).ConfigureAwait(false);
            var status = await SaveGalleryAsync(unit, id, expectedVersion, orderedEntries, release, ct, upload.Source, artist).ConfigureAwait(false);
            if (status != LabelUpdateStatus.Updated) throw new InvalidOperationException("Artwork gallery changed within its transaction.");
            var detail = await DetailsAsync(unit, (await GalleryAsync(unit, id, release, ct, artist).ConfigureAwait(false))!, ct).ConfigureAwait(false);
            await unit.CommitAsync(ct).ConfigureAwait(false);
            return new(LabelUpdateStatus.Updated, detail, assetId);
        }
        catch
        {
            await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await files.DeleteUncommittedAsync(assetId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<ArtworkWriteResult> ReplaceLabelAsync(string id, string expectedVersion, IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default)
        => ReplaceAsync(id, expectedVersion, items, false, ct);
    public ValueTask<ArtworkWriteResult> ReplaceReleaseAsync(string id, string expectedVersion, IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default)
        => ReplaceAsync(id, expectedVersion, items, true, ct);
    public ValueTask<ArtworkWriteResult> ReplaceArtistAsync(string id, string version, IReadOnlyList<ArtworkEntry> items, CancellationToken ct = default)
        => ReplaceAsync(id, version, items, false, ct, true);
    private async ValueTask<ArtworkWriteResult> ReplaceAsync(string id, string expectedVersion, IReadOnlyList<ArtworkEntry> items, bool release, CancellationToken ct, bool artist = false)
    {
        if (artist) await RequireArtistSchema(ct).ConfigureAwait(false);
        if (release) await RequireReleaseSchema(ct).ConfigureAwait(false);
        ArtworkRules.ValidateGallery(items, release, artist);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false);
        var before = await GalleryAsync(unit, id, release, ct, artist).ConfigureAwait(false);
        if (before is null) return new(LabelUpdateStatus.NotFound);
        if (before.Version != expectedVersion) return new(LabelUpdateStatus.Stale);
        if (items.Any(item => !before.Items.Any(old => old.Id == item.Id && old.AssetId == item.AssetId)))
            throw new ArgumentException("Only existing gallery entries can be edited. Upload new artwork separately.", nameof(items));
        var status = await SaveGalleryAsync(unit, id, expectedVersion, items, release, ct, artist: artist).ConfigureAwait(false);
        if (status != LabelUpdateStatus.Updated) return new(status);
        var detail = await DetailsAsync(unit, (await GalleryAsync(unit, id, release, ct, artist).ConfigureAwait(false))!, ct).ConfigureAwait(false);
        await unit.CommitAsync(ct).ConfigureAwait(false);
        return new(status, detail);
    }

    public async ValueTask<ArtworkContent?> ReadAsync(string assetId, bool thumbnail, CancellationToken ct = default)
    {
        ArtworkRules.ValidateAssetId(assetId);
        ArtworkAsset? asset;
        await using (var unit = await storage.BeginUnitOfWorkAsync(ct).ConfigureAwait(false))
            asset = await unit.Artwork.GetAssetAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null) return null;
        try
        {
            if (thumbnail && asset.Width is not null)
            {
                var bytes = await files.ReadAsync(assetId, true, ct).ConfigureAwait(false);
                var decoded = await images.ProcessAsync(bytes, ct).ConfigureAwait(false);
                return new(decoded.Thumbnail, "image/jpeg");
            }
            var original = await files.ReadAsync(assetId, false, ct).ConfigureAwait(false);
            if (original.LongLength != asset.ByteLength || Convert.ToHexString(SHA256.HashData(original)) != asset.ContentHash)
                throw new ArtworkUnavailableException("Stored artwork failed its integrity check.");
            // Legacy uploads retain their bytes, but must pass today's validation before being served.
            var processed = await images.ProcessAsync(original, ct).ConfigureAwait(false);
            return thumbnail ? new(processed.Thumbnail, "image/jpeg") : new(original, processed.MediaType);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
        {
            throw new ArtworkUnavailableException("Artwork is missing or unsupported. Check the configured artwork root and original asset.", exception);
        }
    }
}
