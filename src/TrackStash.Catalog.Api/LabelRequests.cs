using System.Globalization;
using System.Text;
using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

internal static class LabelRequests
{
    private static readonly HashSet<string> QueryFields = ["q", "page", "pageSize", "sort", "order", "normalizedName", "source", "externalId"];

    public static LabelQuery Query(HttpRequest request)
    {
        foreach (var field in request.Query)
            if (!QueryFields.Contains(field.Key) || field.Value.Count != 1)
                throw Invalid("Unknown or repeated query parameter.", field.Key);
        var sort = Value("sort") switch
        {
            null or "name" => LabelSortField.Name, "sortName" => LabelSortField.SortName, "id" => LabelSortField.Id,
            _ => throw Invalid("Use name, sortName, or id.", "sort"),
        };
        var descending = Value("order") switch
        {
            null or "asc" => false, "desc" => true,
            _ => throw Invalid("Use asc or desc.", "order"),
        };
        var query = new LabelQuery
        {
            Search = Value("q"), Page = Number("page", 1), PageSize = Number("pageSize", 25),
            NormalizedName = Value("normalizedName"), Source = Value("source"), ExternalId = Value("externalId"),
            SortBy = sort, Descending = descending,
        };
        query.Validate();
        return query;

        string? Value(string name) => request.Query.TryGetValue(name, out var value) ? value[0] : null;
        int Number(string name, int fallback) => Value(name) is not { } value ? fallback
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
            : throw Invalid("Use a positive integer.", name);
    }

    public static async Task<CreateLabelRequest> CreateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var fields = await FieldsAsync(request, "application/json", cancellationToken);
        if (!fields.TryGetValue("name", out var name) || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()))
            throw Invalid("A nonblank name is required.", "name");
        return new CreateLabelRequest
        {
            Name = name.GetString()!,
            SortName = fields.TryGetValue("sortName", out var sort) ? NullableString(sort, "sortName") : null,
        };
    }

    public static async Task<LabelUpdate> PatchAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var fields = await FieldsAsync(request, "application/merge-patch+json", cancellationToken, ["name", "sortName", "profile"]);
        var patch = new LabelUpdate
        {
            SetName = fields.ContainsKey("name"),
            Name = fields.TryGetValue("name", out var name) ? NullableString(name, "name") : null,
            SetSortName = fields.ContainsKey("sortName"),
            SortName = fields.TryGetValue("sortName", out var sort) ? NullableString(sort, "sortName") : null,
            SetProfile = fields.ContainsKey("profile"),
            Profile = fields.TryGetValue("profile", out var profile) ? LabelProfileRequests.Read(profile) : null,
        };
        patch.Validate();
        return patch;
    }

    internal static async Task<Dictionary<string, JsonElement>> FieldsAsync(HttpRequest request, string contentType,
        CancellationToken cancellationToken, string[]? allowedFields = null)
    {
        if (!request.HasJsonContentType() || !string.Equals(request.ContentType?.Split(';')[0].Trim(), contentType, StringComparison.OrdinalIgnoreCase))
            throw new ApiRequestException(415, "unsupported_media_type", $"Use {contentType}.");
        if (request.ContentLength > 16 * 1024)
            throw new ApiRequestException(413, "body_too_large", "Request body exceeds 16 KiB.");
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + read > 16 * 1024)
                throw new ApiRequestException(413, "body_too_large", "Request body exceeds 16 KiB.");
            body.Write(buffer, 0, read);
        }
        using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw Invalid("The request body must be an object.", "body");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!(allowedFields ?? ["name", "sortName"]).Contains(property.Name, StringComparer.Ordinal))
                throw Invalid("This field is not editable.", property.Name);
            if (!fields.TryAdd(property.Name, property.Value.Clone()))
                throw Invalid("Duplicate properties are not allowed.", property.Name);
        }
        return fields;
    }

    internal static string RequiredString(Dictionary<string, JsonElement> fields, string field)
        => fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw Invalid("A nonblank string is required.", field);

    internal static string? NullableString(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        _ => throw Invalid("Use a string or null.", field),
    };

    public static string ETag(string version) => $"\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(version))}\"";

    public static string ExpectedVersion(HttpRequest request)
    {
        var values = request.Headers.IfMatch;
        if (values.Count == 0)
            throw new ApiRequestException(428, "precondition_required", "Supply the label's last-read ETag in If-Match.", "If-Match");
        var value = values.Count == 1 ? values[0] : null;
        if (value is null || value.Length < 3 || value[0] != '"' || value[^1] != '"')
            throw Invalid("Supply exactly one strong ETag returned by this API; wildcards and weak tags are not supported.", "If-Match");
        try
        {
            var version = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value[1..^1]));
            if (string.IsNullOrWhiteSpace(version) || ETag(version) != value)
                throw Invalid("Supply exactly one strong ETag returned by this API.", "If-Match");
            return version;
        }
        catch (FormatException)
        {
            throw Invalid("The ETag is malformed.", "If-Match");
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("The ETag is malformed.", "If-Match");
        }
    }

    private static ApiRequestException Invalid(string message, string field) => new(400, "validation_failed", message, field);
}
