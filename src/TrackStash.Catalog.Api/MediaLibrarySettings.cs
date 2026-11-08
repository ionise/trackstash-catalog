using System.Security.Cryptography;
using System.Text.Json;
using TrackStash.Catalog.Application;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api;

/// <summary>A stable library identity with a user-chosen name, absolute location and visibility.</summary>
public sealed record MediaLibrarySetting(string Id, string Name, string Root, bool Enabled);
/// <summary>Persistent library settings and their original optimistic concurrency version.</summary>
public sealed record MediaLibrarySettingsResponse(IReadOnlyList<MediaLibrarySetting> Libraries, string Version);
/// <summary>Explicit approval of library root access and a replacement settings list preserving existing IDs.</summary>
public sealed record UpdateMediaLibrariesRequest(IReadOnlyList<MediaLibrarySetting> Libraries, bool Confirmed);

public interface IMediaLibrarySettings
{
    MediaLibrarySettingsResponse Read();
    Task<MediaLibrarySettingsResponse> SaveAsync(IReadOnlyList<MediaLibrarySetting> libraries, string version, bool confirmed, CancellationToken ct);
}

public sealed class MediaLibrarySettings(IConfiguration configuration) : IMediaLibrarySettings
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string SettingsPath
    {
        get
        {
            var path = configuration["CatalogApi:MediaLibrarySettingsPath"] ??
                (configuration["CatalogApi:DatabasePath"] + ".media-libraries.json");
            if (!Path.IsPathFullyQualified(path))
                throw Error(409, "media_settings_not_configured", "Configure an absolute MediaLibrarySettingsPath or Catalog database path.");
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(path).LinkTarget is not null)
                throw Error(409, "media_settings_not_configured", "MediaLibrarySettingsPath must name a nonsymlink JSON settings file.");
            return Path.GetFullPath(path);
        }
    }
    private static EnrichmentApplyException Error(int status, string code, string message) => new(status, code, message);
    private static MediaLibrarySettingsResponse Response(IReadOnlyList<MediaLibrarySetting> libraries) =>
        new(libraries, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(libraries))).ToLowerInvariant());
    private static MediaLibrarySetting[] Validate(IReadOnlyList<MediaLibrarySetting> libraries)
    {
        if (libraries.Count > 32 || libraries.Any(l => l is null || string.IsNullOrWhiteSpace(l.Id) || l.Id.Length > 100 ||
            l.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            string.IsNullOrWhiteSpace(l.Name) || l.Name.Length > 100 || l.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(l.Root) || l.Root.Length > 1024 || !Path.IsPathFullyQualified(l.Root) || l.Root.Any(char.IsControl)))
            throw Error(400, "validation_failed", "Use at most 32 libraries with stable IDs, names up to 100 characters and absolute roots up to 1024 characters.");
        MediaLibrarySetting[] normalized;
        try { normalized = libraries.Select(l => l with { Name = l.Name.Trim(), Root = Path.GetFullPath(l.Root) }).ToArray(); }
        catch (ArgumentException) { throw Error(400, "validation_failed", "Invalid library root path."); }
        if (normalized.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length ||
            normalized.Any(l => l.Root == Path.GetPathRoot(l.Root)) ||
            normalized.Where(l => l.Enabled).Select(l => l.Root.TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.Ordinal).Count() != normalized.Count(l => l.Enabled))
            throw Error(400, "validation_failed", "Library IDs and names must be unique; enabled roots must be distinct directories, not filesystem roots.");
        return normalized;
    }
    public MediaLibrarySettingsResponse Read()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                var defaults = configuration.GetSection("CatalogApi:MediaLibraries").GetChildren().Select(section =>
                    new MediaLibrarySetting(section.Key, section.Key, section.Value ?? "", true)).ToArray();
                return Response(Validate(defaults));
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 16 * 1024) throw Error(409, "media_settings_invalid", "Library settings exceed the supported size.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var libraries = JsonSerializer.Deserialize<MediaLibrarySetting[]>(bytes)
                ?? throw Error(409, "media_settings_invalid", "Library settings must contain a library list.");
            try { return Response(Validate(libraries)); }
            catch (EnrichmentApplyException) { throw Error(409, "media_settings_invalid", "Stored library settings are invalid; repair the settings file before continuing."); }
        }
        catch (JsonException) { throw Error(409, "media_settings_invalid", "Library settings JSON is malformed; it has not been replaced with defaults."); }
        catch (IOException) { throw Error(409, "media_settings_unavailable", "Library settings could not be read."); }
        catch (UnauthorizedAccessException) { throw Error(409, "media_settings_unavailable", "Library settings access was denied."); }
    }
    public async Task<MediaLibrarySettingsResponse> SaveAsync(IReadOnlyList<MediaLibrarySetting> libraries, string version, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) throw Error(400, "validation_failed", "Confirm that the configured directories may be accessed by Media verification.");
        var normalized = Validate(libraries);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized);
        if (bytes.Length > 16 * 1024) throw Error(400, "validation_failed", "Combined library settings exceed 16 KiB.");
        await gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using var lease = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Read();
            if (current.Version != version) throw Error(412, "media_settings_changed", "Library settings changed; reload before saving.");
            if (current.Libraries.Any(l => !normalized.Any(n => n.Id == l.Id)))
                throw Error(400, "validation_failed", "Existing library IDs must be preserved. Disable a library instead of deleting it.");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, SettingsPath, overwrite: true);
            return Response(normalized);
        }
        catch (IOException) { throw Error(409, "media_settings_unavailable", "Library settings could not be saved or another process is editing them. No successful save was reported."); }
        catch (UnauthorizedAccessException) { throw Error(409, "media_settings_unavailable", "Library settings write access was denied."); }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }
}

internal static class MediaLibrarySettingsEndpoints
{
    public static void MapMediaLibrarySettings(this WebApplication app)
    {
        app.MapGet("/api/v1/media-libraries", (HttpContext context, IMediaLibrarySettings service, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var result = service.Read();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(result.Version);
            return TypedResults.Ok(result);
        }).WithName("GetMediaLibrarySettings").WithTags("Media").WithSummary("Read configured library names and roots")
          .Produces<MediaLibrarySettingsResponse>().ProducesProblem(409);
        app.MapPatch("/api/v1/media-libraries", async (HttpContext context, IMediaLibrarySettings service, CancellationToken ct) =>
        {
            var version = LabelRequests.ExpectedVersion(context.Request);
            var fields = await LabelRequests.FieldsAsync(context.Request, "application/json", ct, ["libraries", "confirmed"]);
            if (!fields.TryGetValue("libraries", out var list) || list.ValueKind != JsonValueKind.Array ||
                list.GetArrayLength() > 32 || !fields.TryGetValue("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ApiRequestException(400, "validation_failed", "A bounded library list and explicit boolean confirmation are required.");
            var libraries = new List<MediaLibrarySetting>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new ApiRequestException(400, "validation_failed", "Invalid library entry.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in item.EnumerateObject())
                    if (!names.Add(field.Name) || field.Name is not ("id" or "name" or "root" or "enabled"))
                        throw new ApiRequestException(400, "validation_failed", "Unknown or repeated library entry field.");
                string Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : throw new ApiRequestException(400, "validation_failed", "Library ID, name and root are required.");
                if (!item.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ApiRequestException(400, "validation_failed", "Library enabled must be boolean.");
                libraries.Add(new(Text("id"), Text("name"), Text("root"), enabled.GetBoolean()));
            }
            var result = await service.SaveAsync(libraries, version, confirmed.GetBoolean(), ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = LabelRequests.ETag(result.Version);
            return TypedResults.Ok(result);
        }).WithName("UpdateMediaLibrarySettings").WithTags("Media").WithSummary("Explicitly save names and approved roots without rescanning or moving files")
          .Accepts<UpdateMediaLibrariesRequest>("application/json").Produces<MediaLibrarySettingsResponse>()
          .ProducesProblem(400).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
    }
}
