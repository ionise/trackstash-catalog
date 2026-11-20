using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using TrackStash.Catalog.Api.Authentication;
using TrackStash.Catalog.Api.Middleware;

namespace TrackStash.Catalog.Api;

/// <summary>Credentials for opening a native administrator session.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSessionRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

/// <summary>Native login availability and the current browser's authentication state.</summary>
public sealed record SessionResponse(
    bool Available,
    bool Authenticated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProxyUser = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProxyLogoutPath = null);

internal sealed class SessionEndpoint;

internal static class SessionEndpoints
{
    private const int MaxBodyBytes = 4096;

    internal static void MapSessions(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/session").WithTags("Session").WithMetadata(new SessionEndpoint());
        group.MapGet("", GetSession)
            .WithName("GetSession")
            .WithSummary("Get native session state")
            .WithDescription("Reports native login availability and current native or trusted-proxy authentication.");
        group.MapPost("", CreateSessionAsync)
            .WithName("CreateSession")
            .WithSummary("Sign in as the native administrator")
            .WithDescription("Requires the exact same Origin. Sets the session cookie on success.")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(429);
        group.MapDelete("", DeleteSession)
            .WithName("DeleteSession")
            .WithSummary("Sign out of the native session")
            .WithDescription("Requires the exact same Origin. Revokes the presented session and clears its cookie.")
            .ProducesProblem(403);
    }

    private static Ok<SessionResponse> GetSession(HttpContext context, IAdminSessions sessions, TrustedProxyIdentity proxyIdentity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var username = proxyIdentity.GetUsername(context.Request);
        return TypedResults.Ok(new SessionResponse(sessions.Available,
            username is not null || sessions.IsAuthenticated(context.Request), username,
            username is null ? null : proxyIdentity.LogoutPath));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> CreateSessionAsync(
        HttpContext context, IAdminSessions sessions, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CancellationToken ct)
    {
        if (!sessions.Available) return Problem(context, 404, "session_unavailable", "Native login is not available");
        var request = context.Request;
        if (!request.HasJsonContentType()) return Problem(context, 415, "unsupported_media_type", "Expected JSON");
        if (request.ContentLength > MaxBodyBytes) return TooLarge(context);

        CreateSessionRequest? input;
        var buffer = new byte[MaxBodyBytes + 1];
        try
        {
            var bytes = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
            if (bytes > MaxBodyBytes) return TooLarge(context);
            input = JsonSerializer.Deserialize<CreateSessionRequest>(buffer.AsSpan(0, bytes), json.Value.SerializerOptions);
        }
        catch (JsonException)
        {
            // JSON parser diagnostics can contain request data; do not log them.
            return InvalidRequest(context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        if (input?.Username is not { Length: > 0 and <= AdminCredentials.MaxUsernameLength }
            || input.Password is not { Length: > 0 and <= AdminCredentials.MaxPasswordLength })
            return InvalidRequest(context);

        ct.ThrowIfCancellationRequested();
        if (sessions.ReserveLoginAttempt() is var retryAfter and > 0)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Problem(context, 429, "login_throttled", "Too many sign-in attempts");
        }
        if (!sessions.VerifyCredentials(input.Username, input.Password))
            return Problem(context, 401, "invalid_credentials", "Invalid username or password");

        ct.ThrowIfCancellationRequested();
        sessions.SignIn(context);
        return TypedResults.NoContent();
    }

    private static NoContent DeleteSession(HttpContext context, IAdminSessions sessions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        sessions.SignOut(context);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult InvalidRequest(HttpContext context)
        => Problem(context, 400, "invalid_login_request", "Invalid sign-in request");

    private static ProblemHttpResult TooLarge(HttpContext context)
        => Problem(context, 413, "body_too_large", "Request body is too large");

    private static ProblemHttpResult Problem(HttpContext context, int status, string code, string title)
        => TypedResults.Problem(ApiErrors.Create(context, status, code, title));
}

internal sealed class SessionSecurityOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<SessionEndpoint>().Any()) return;
        operation.Security = [new OpenApiSecurityRequirement()]; // Explicit anonymous alternative overrides global auth.
        if (context.ApiDescription.HttpMethod == "POST")
        {
            // Describe the body without installing a routing content-type constraint; the handler
            // checks origin and controls body reading and generic errors itself.
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = context.SchemaGenerator.GenerateSchema(typeof(CreateSessionRequest), context.SchemaRepository),
                    },
                },
            };
        }
    }
}
