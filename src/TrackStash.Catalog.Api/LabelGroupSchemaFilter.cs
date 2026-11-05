using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TrackStash.Catalog.Api;

internal sealed class LabelGroupSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var type = context.Type;
        if (type != typeof(CreateLabelGroupRequest) && type != typeof(UpdateLabelGroupRequest)
            && type != typeof(CreateGroupMembershipRequest) && type != typeof(UpdateGroupMembershipRequest)
            && type != typeof(LabelGroupResponse) && type != typeof(LabelGroupMembershipResponse)) return;
        schema.AdditionalPropertiesAllowed = false;
        if (type == typeof(UpdateLabelGroupRequest))
        {
            schema.Required.Clear();
            schema.MinProperties = 1;
            schema.Properties["name"].Nullable = false;
        }
        if (type == typeof(CreateLabelGroupRequest)) schema.Required = new HashSet<string> { "kind", "name" };
        if (type == typeof(CreateGroupMembershipRequest) || type == typeof(UpdateGroupMembershipRequest))
        {
            schema.Required = new HashSet<string> { "labelVersion", "groupVersion", "status", "since", "until", "source", "sourceUrl", "notes" };
            if (type == typeof(CreateGroupMembershipRequest)) schema.Required.UnionWith(["labelId", "groupId"]);
            schema.Properties["source"].MinLength = 1; schema.Properties["source"].MaxLength = 200;
            schema.Properties["notes"].MaxLength = 2000;
            foreach (var date in new[] { "since", "until" })
                schema.Properties[date].Description = "Latest known period, preserving YYYY, YYYY-MM or YYYY-MM-DD precision. Null means unknown. No definitely future dates.";
            schema.Properties["confirmRejoin"].Description = "Explicit review is required when replacing a former period with a current membership.";
        }
        if (schema.Properties.TryGetValue("kind", out var kind))
            kind.Enum = new[] { "family", "organisation" }.Select(x => (IOpenApiAny)new OpenApiString(x)).ToList();
        if (schema.Properties.TryGetValue("status", out var status))
            status.Enum = new[] { "current", "former", "unknown" }.Select(x => (IOpenApiAny)new OpenApiString(x)).ToList();
    }
}
