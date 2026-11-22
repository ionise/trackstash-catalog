using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Api;

/// <summary>Choose whether to search by tags instead of a valid ISRC.</summary>
public sealed record MediaProviderLookupRequest(bool SearchByMetadata);
/// <summary>Choose a remote scanner snapshot and lookup strategy.</summary>
public sealed record RemoteMediaProviderLookupRequest(RemoteInventoryKey Key, string Version, bool SearchByMetadata);

internal static class MediaProviderLookup
{
    internal static string ReleaseSourceVersion(EnrichmentCandidate candidate)
    {
        var metadata = candidate.Metadata;
        if (metadata.ReleaseDetails is { } details)
            metadata = metadata with { ReleaseDetails = details with {
                Tracks = details.Tracks.Select(track => track with {
                    RecordingCandidate = track.RecordingCandidate is null ? null : track.RecordingCandidate with { Evidence = [] }
                }).ToArray()
            } };
        var source = new { candidate.ProviderId, candidate.CandidateId, candidate.EditionId, Metadata = metadata };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source)))).ToLowerInvariant();
    }

    internal static async Task<EnrichmentLookup> FindSonoVaultReleaseAsync(string id, string? edition,
        IEnumerable<IEnrichmentProvider> providers, CancellationToken ct)
    {
        var configured = providers.Where(p => p.Descriptor.Id == "sonovault" &&
            p.Descriptor.SupportedEntityKinds.Contains(EnrichmentEntityKind.Release)).ToArray();
        if (configured.Length != 1)
            throw new EnrichmentRequestException("enrichment_provider_unavailable", "Exactly one SonoVault Release provider must be configured.");
        var query = new EnrichmentQuery(EnrichmentEntityKind.Release, "unmatched-media", "lookup-only",
            null, null, [], null, null, null, null, null, [], LookupCandidateId: id, LookupEditionId: edition);
        var lookup = await configured[0].FindAsync(query, ct);
        return lookup with { SourceVersion = lookup.Candidates.Count == 1 ? ReleaseSourceVersion(lookup.Candidates[0]) : null };
    }

    private static bool MetadataMode(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("searchByMetadata", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ApiRequestException(400, "validation_failed", "Choose whether to search by metadata.");
        return value.GetBoolean();
    }

    private static async Task<EnrichmentLookup> FindAsync(string provider, string? title, string? mix, string? artist,
        string? isrc, double? duration, bool metadataMode, IEnumerable<IEnrichmentProvider> providers, CancellationToken ct)
    {
        if (provider is not ("beatport" or "sonovault"))
            throw new ApiRequestException(400, "validation_failed", "Choose Beatport or SonoVault.");
        var matches = providers.Where(p => p.Descriptor.Id == provider &&
            p.Descriptor.SupportedEntityKinds.Contains(EnrichmentEntityKind.Recording)).ToArray();
        if (matches.Length != 1)
            throw new EnrichmentRequestException("enrichment_provider_unavailable", "Exactly one compatible enrichment provider must be configured.");
        string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        title = Clean(title); mix = Clean(mix); artist = Clean(artist);
        if (new[] { title, mix, artist }.Any(value => value?.Length > 200))
            throw new ApiRequestException(400, "validation_failed", "Scanner title, mix and Artist must be at most 200 characters for provider lookup.");
        var validIsrc = !metadataMode && MediaService.Isrc(isrc) ? isrc!.Trim().ToUpperInvariant() : null;
        long? milliseconds = duration is >= 0 and <= 86400 && double.IsFinite(duration.Value)
            ? (long)Math.Round(duration.Value * 1000) : null;
        var query = new EnrichmentQuery(EnrichmentEntityKind.Recording, "unmatched-media", "lookup-only",
            title, mix, artist is null ? [] : [artist], validIsrc, null, null, null, milliseconds, []);
        try { return await matches[0].FindAsync(query, ct); }
        catch (ArgumentException)
        {
            throw new EnrichmentRequestException("enrichment_query_incomplete",
                "Provider lookup requires a valid ISRC or suitable title and Artist tags. Refresh the scanner metadata or search for a Release instead.");
        }
    }

    internal static void MapMediaProviderLookup(this WebApplication app)
    {
        app.MapGet("/api/v1/media/provider-release/sonovault/{id}", async (string id, HttpContext context,
            IEnumerable<IEnrichmentProvider> providers, CancellationToken ct) =>
        {
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var releaseId) || releaseId <= 0 ||
                context.Request.Query.Any(p => p.Key != "editionId" || p.Value.Count != 1))
                throw new ApiRequestException(400, "validation_failed", "Choose a positive SonoVault Release ID and optional edition.");
            var edition = context.Request.Query["editionId"].FirstOrDefault();
            if (edition is not null && (!long.TryParse(edition, NumberStyles.None, CultureInfo.InvariantCulture, out var editionId) || editionId <= 0))
                throw new ApiRequestException(400, "validation_failed", "SonoVault edition ID must be positive.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await FindSonoVaultReleaseAsync(id, edition, providers, ct));
        }).WithName("LookupSonoVaultMediaRelease").WithTags("Media")
          .WithSummary("Inspect a SonoVault Release and optional edition reported for a track")
          .WithDescription("Read-only provider evidence; a representative Release does not establish a specific edition.")
          .Produces<EnrichmentLookup>().ProducesProblem(400).ProducesProblem(502).ProducesProblem(503);

        app.MapPost("/api/v1/media/{id:long}/provider-lookup/{provider}", async (long id, string provider,
            HttpContext context, IMediaService media, IEnumerable<IEnrichmentProvider> providers, CancellationToken ct) =>
        {
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["searchByMetadata"]);
            var mode = MetadataMode(fields);
            var item = await media.LookupItemAsync(id, ct);
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await FindAsync(provider, item.Title, item.Mix, item.Artist, item.Isrc,
                item.DurationSeconds, mode, providers, ct));
        }).WithName("LookupLocalMediaProvider").WithTags("Media")
          .WithSummary("Find provider Recording candidates using server-held scanner tags")
          .WithDescription("Read-only. No paths, audio, fingerprints or catalog writes are sent to the provider.")
          .Accepts<MediaProviderLookupRequest>("application/json").Produces<EnrichmentLookup>()
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(502).ProducesProblem(503);

        app.MapPost("/api/v1/remote-inventory/provider-lookup/{provider}", async (string provider,
            HttpContext context, IRemoteInventoryService inventory, IEnumerable<IEnrichmentProvider> providers, CancellationToken ct) =>
        {
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["key", "version", "searchByMetadata"]);
            var mode = MetadataMode(fields);
            if (!fields.TryGetValue("key", out var key) || key.ValueKind != JsonValueKind.Object ||
                key.EnumerateObject().Any(p => p.Name is not ("ownerId" or "deviceId" or "mediaFileId")) ||
                key.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                !key.TryGetProperty("ownerId", out var owner) || owner.ValueKind != JsonValueKind.String ||
                !key.TryGetProperty("deviceId", out var device) || device.ValueKind != JsonValueKind.String ||
                !key.TryGetProperty("mediaFileId", out var mediaId) || mediaId.ValueKind != JsonValueKind.Number || !mediaId.TryGetInt64(out var id) ||
                !fields.TryGetValue("version", out var version) || version.ValueKind != JsonValueKind.String ||
                version.GetString() is not { Length: 64 } expected)
                throw new ApiRequestException(400, "validation_failed", "Choose a remote scanner record and its original snapshot version.");
            var (item, actual) = await inventory.LookupItemAsync(new(owner.GetString()!, device.GetString()!, id), ct);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new ApiRequestException(412, "enrichment_state_changed", "Device inventory changed; reload before searching.");
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await FindAsync(provider, item.Title, item.Subtitle, item.Artist, item.Isrc,
                item.DurationSeconds, mode, providers, ct));
        }).WithName("LookupRemoteMediaProvider").WithTags("Remote inventory")
          .WithSummary("Find provider Recording candidates using a remote scanner snapshot")
          .WithDescription("Rejects a stale snapshot. Device audio is not read; no path or fingerprint is sent.")
          .Accepts<RemoteMediaProviderLookupRequest>("application/json").Produces<EnrichmentLookup>()
          .ProducesProblem(400).ProducesProblem(404).ProducesProblem(412).ProducesProblem(502).ProducesProblem(503);
    }
}
