using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TrackStash.Catalog.Api;

internal sealed class LabelProfileSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(LabelProfileResponse) && context.Type != typeof(LabelWebLinkResponse)) return;
        schema.AdditionalPropertiesAllowed = false;
        schema.Required = schema.Properties.Keys.ToHashSet(StringComparer.Ordinal);
        if (context.Type == typeof(LabelProfileResponse))
        {
            Choices("lifecycleStatus", ["unknown", "active", "inactive", "closed"]);
            Choices("reviewStatus", ["unreviewed", "in_progress", "reviewed"]);
            schema.Properties["countryCode"].Pattern = "^[A-Z]{2}$";
            schema.Properties["foundedYear"].Minimum = 1;
            schema.Properties["foundedYear"].Maximum = 9999;
            schema.Properties["links"].MaxItems = 100;
        }
        else Choices("kind", ["official", "bandcamp", "discogs", "musicbrainz", "resident_advisor", "beatport", "social", "other"]);

        void Choices(string property, string[] choices)
            => schema.Properties[property].Enum = choices.Select(c => (IOpenApiAny)new OpenApiString(c)).ToList();
    }
}
