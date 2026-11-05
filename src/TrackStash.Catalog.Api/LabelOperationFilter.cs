using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TrackStash.Catalog.Api;

internal sealed class LabelOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var statuses = context.ApiDescription.HttpMethod is "POST" or "PATCH"
            ? new[] { "401", "403", "413", "500" } : ["401", "403", "500"];
        foreach (var status in statuses)
            operation.Responses.TryAdd(status, new OpenApiResponse
            {
                Description = status switch
                {
                    "401" => "Bearer key required", "403" => "Loopback/origin policy rejected",
                    "413" => operation.OperationId == "UploadLabelArtwork" ? "Upload exceeds 10 MiB plus 64 KiB envelope" : "Body exceeds 16 KiB", _ => "Unexpected failure",
                },
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new() { Schema = context.SchemaGenerator.GenerateSchema(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), context.SchemaRepository) },
                },
            });
        if (context.ApiDescription.HttpMethod is "PATCH" or "DELETE" || operation.OperationId is "ApplyLabelMerge" or "UploadLabelArtwork")
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match", In = ParameterLocation.Header, Required = true,
                Description = "Exactly one strong ETag returned by GET or POST. Missing: 428; stale: 412.",
                Schema = new OpenApiSchema { Type = "string" },
            });
        if (context.ApiDescription.RelativePath?.Trim('/') == "api/v1/labels" && context.ApiDescription.HttpMethod == "GET")
        {
            foreach (var name in new[] { "q", "page", "pageSize", "sort", "order", "normalizedName", "source", "externalId" })
            {
                var schema = new OpenApiSchema { Type = name is "page" or "pageSize" ? "integer" : "string" };
                if (name is "page" or "pageSize")
                {
                    schema.Minimum = 1;
                    schema.Default = new OpenApiInteger(name == "page" ? 1 : 25);
                    if (name == "pageSize") schema.Maximum = 100;
                }
                if (name is "sort" or "order")
                    schema.Enum = (name == "sort" ? new[] { "name", "sortName", "id" } : ["asc", "desc"])
                        .Select(x => (IOpenApiAny)new OpenApiString(x)).ToList();
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = name, In = ParameterLocation.Query, Schema = schema,
                    Description = name switch
                    {
                        "q" => "Strict-normalized substring of label/alias names.",
                        "externalId" => "Exact external ID; requires source.",
                        "source" => "Exact case-sensitive provider name.",
                        _ => "See the label query contract.",
                    },
                });
            }
        }
        if (operation.OperationId is "QueryLabelGroups" or "GetGroupMembers" or "GetLabelGroupMemberships" or "GetRelatedLabels")
        {
            var groups = operation.OperationId == "QueryLabelGroups";
            var related = operation.OperationId == "GetRelatedLabels";
            var names = groups ? new[] { "page", "pageSize", "q", "kind", "sort", "order" }
                : related ? ["page", "pageSize", "kind"] : ["page", "pageSize", "kind", "status"];
            foreach (var name in names)
            {
                var schema = new OpenApiSchema { Type = name is "page" or "pageSize" ? "integer" : "string" };
                if (name is "page" or "pageSize")
                {
                    schema.Minimum = 1;
                    schema.Default = new OpenApiInteger(name == "page" ? 1 : 25);
                    if (name == "pageSize") schema.Maximum = 100;
                }
                var choices = name switch
                {
                    "kind" => related ? new[] { "family", "organisation", "parent" } : ["family", "organisation"],
                    "status" => ["current", "former", "unknown", "all"],
                    "sort" => ["name", "sortName", "id"],
                    "order" => ["asc", "desc"],
                    _ => Array.Empty<string>(),
                };
                if (choices.Length > 0) schema.Enum = choices.Select(x => (IOpenApiAny)new OpenApiString(x)).ToList();
                if (name == "status") schema.Default = new OpenApiString("current");
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = name, In = ParameterLocation.Query, Schema = schema,
                    Description = name switch
                    {
                        "q" => "Strict-normalized substring of group names.",
                        "status" => "Default current; counts include every status independently of this filter.",
                        "kind" => related ? "Filter supporting reasons; parent means shared recorded imprint parent." : "Family or organisation; membership does not imply ownership.",
                        _ => "One value only; pages are stable and bounded to 100 results.",
                    },
                });
            }
        }
        foreach (var response in operation.Responses.Where(x => x.Key is "200" or "201"))
        {
            if (operation.OperationId is "GetLabel" or "CreateLabel" or "UpdateLabel" or "CreateLabelMergePreview" or "GetLabelMergePreview"
                or "GetLabelArtwork" or "UploadLabelArtwork" or "ReplaceLabelArtwork"
                or "GetLabelRelationships" or "ReplaceLabelRelationships" or "SaveLabelEdge"
                or "GetLabelGroup" or "CreateLabelGroup" or "UpdateLabelGroup"
                or "GetGroupMembership" or "CreateGroupMembership" or "UpdateGroupMembership")
                response.Value.Headers["ETag"] = new OpenApiHeader
                {
                    Description = "Opaque strong entity/review tag. Send the original tag in If-Match when editing or confirming a merge.",
                    Schema = new OpenApiSchema { Type = "string" },
                };
            if (response.Key == "201")
                response.Value.Headers["Location"] = new OpenApiHeader
                {
                    Description = "URI of the created resource or merge review.", Schema = new OpenApiSchema { Type = "string" },
                };
        }
        if (operation.Responses.TryGetValue("308", out var redirect))
            redirect.Headers["Location"] = new OpenApiHeader
            {
                Description = "Canonical survivor URI for this retired label/child resource.",
                Schema = new OpenApiSchema { Type = "string" },
            };
    }
}
