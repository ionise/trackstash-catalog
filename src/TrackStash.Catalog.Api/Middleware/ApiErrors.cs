using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TrackStash.Core.Storage;
using TrackStash.Catalog.Application;

namespace TrackStash.Catalog.Api.Middleware;

internal sealed class ApiRequestException(int status, string code, string message, string? field = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Field { get; } = field;
}

internal static class ApiErrors
{
    public static ProblemDetails Create(HttpContext context, int status, string code, string title,
        string? detail = null, string? field = null)
    {
        var problem = new ProblemDetails
        {
            Status = status, Title = title, Detail = detail, Instance = context.Request.Path,
            Type = $"https://httpstatuses.com/{status}",
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (field is not null)
            problem.Extensions["errors"] = new Dictionary<string, string[]> { [field] = [detail ?? title] };
        return problem;
    }

    public static async Task WriteAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(Create(context, status, code, title),
            options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail, field) = exception switch
        {
            ArtworkUnavailableException => (409, "artwork_unavailable", "Artwork unavailable",
                (string?)"The stored image is missing, damaged or unsupported. Check artwork storage and restore its backup if needed.", (string?)null),
            RetiredLabelException => (409, "label_retired", "Label is retired", (string?)"Review the canonical label before writing.", (string?)null),
            ApiRequestException request => (request.Status, request.Code, "Request rejected", (string?)request.Message, request.Field),
            JsonException => (400, "invalid_json", "Invalid JSON", null, null),
            BadHttpRequestException bad => (bad.StatusCode,
                bad.StatusCode == 413 ? "body_too_large" : "invalid_request", "Invalid request", null, null),
            ArgumentException argument => (400, "validation_failed", "Invalid label request",
                "Check the supplied field value.", MapField(argument.ParamName)),
            _ => (500, "internal_error", "An unexpected error occurred", null, null),
        };
        if (status >= 500 || exception is ArtworkUnavailableException)
            logger.LogError(exception, "Catalog API request failed; trace {TraceId}", context.TraceIdentifier);
        else
            logger.LogWarning("Catalog API rejected request with {Code}; trace {TraceId}", code, context.TraceIdentifier);
        context.Response.StatusCode = status;
        var problem = ApiErrors.Create(context, status, code, title, detail, field);
        if (exception is RetiredLabelException retired)
        {
            problem.Extensions["canonicalId"] = retired.CanonicalId;
            problem.Extensions["canonicalUri"] = $"/api/v1/labels/{Uri.EscapeDataString(retired.CanonicalId)}";
        }
        await context.Response.WriteAsJsonAsync(problem,
            options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }

    private static string? MapField(string? field) => field switch
    {
        "Search" => "q", "SortBy" => "sort", null => null,
        _ => JsonNamingPolicy.CamelCase.ConvertName(field),
    };
}
