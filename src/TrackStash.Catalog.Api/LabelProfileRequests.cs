using System.Text.Json;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

internal static class LabelProfileRequests
{
    public static LabelProfile Read(JsonElement element)
    {
        var fields = Object(element, ["description", "countryCode", "city", "foundedYear", "lifecycleStatus", "notes", "favourite", "reviewStatus", "links"]);
        if (fields["links"].ValueKind != JsonValueKind.Array) throw Invalid("links must be an array.");
        var links = fields["links"].EnumerateArray().Select(link =>
        {
            var item = Object(link, ["kind", "url", "title", "sourceUrl"]);
            return new LabelWebLink
            {
                Kind = Required(item, "kind"), Url = Required(item, "url"),
                Title = Nullable(item, "title"), SourceUrl = Nullable(item, "sourceUrl"),
            };
        }).ToArray();
        var profile = new LabelProfile
        {
            Description = Nullable(fields, "description"), CountryCode = Nullable(fields, "countryCode"),
            City = Nullable(fields, "city"), FoundedYear = Year(fields["foundedYear"]),
            LifecycleStatus = Required(fields, "lifecycleStatus"), Notes = Nullable(fields, "notes"),
            Favourite = fields["favourite"].ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false,
                _ => throw Invalid("favourite must be a boolean."),
            },
            ReviewStatus = Required(fields, "reviewStatus"), Links = links,
        };
        profile.Validate();
        return profile;
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("Profile and link values must be objects.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw Invalid("Unknown or duplicate profile/link property.");
        if (allowed.Any(name => !fields.ContainsKey(name)))
            throw Invalid("A profile replacement must include every profile field; each link must include kind, url, title, and sourceUrl. Use null for empty optional text.");
        return fields;
    }

    private static string Required(Dictionary<string, JsonElement> fields, string field)
        => LabelRequests.RequiredString(fields, field);
    private static string? Nullable(Dictionary<string, JsonElement> fields, string field)
        => LabelRequests.NullableString(fields[field], field);
    private static int? Year(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null
        : element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var year) ? year
        : throw Invalid("foundedYear must be an integer or null.");
    private static ApiRequestException Invalid(string message) => new(400, "validation_failed", message, "profile");
}
