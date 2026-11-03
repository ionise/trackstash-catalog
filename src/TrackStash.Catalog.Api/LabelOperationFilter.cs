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
                    "413" => "Body exceeds 16 KiB", _ => "Unexpected failure",
                },
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new() { Schema = context.SchemaGenerator.GenerateSchema(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), context.SchemaRepository) },
                },
            });
        if (context.ApiDescription.HttpMethod == "PATCH")
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
        foreach (var response in operation.Responses.Where(x => x.Key is "200" or "201"))
        {
            if (operation.OperationId is "GetLabel" or "CreateLabel" or "UpdateLabel")
                response.Value.Headers["ETag"] = new OpenApiHeader
                {
                    Description = "Opaque strong aggregate entity tag. Send it back in If-Match when editing.",
                    Schema = new OpenApiSchema { Type = "string" },
                };
            if (response.Key == "201")
                response.Value.Headers["Location"] = new OpenApiHeader
                {
                    Description = "URI of the created label.", Schema = new OpenApiSchema { Type = "string" },
                };
        }
    }
}
