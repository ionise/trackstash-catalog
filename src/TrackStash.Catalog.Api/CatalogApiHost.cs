using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Catalog.Application;
using TrackStash.Core.Sqlite;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

public static class CatalogApiHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args, ApplicationName = typeof(CatalogApiHost).Assembly.FullName,
        });
        configure?.Invoke(builder);
        var key = builder.Configuration["CatalogApi:ApiKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32 || key.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Set CatalogApi:ApiKey to a secret of at least 32 characters without whitespace.");
        var port = builder.Configuration.GetValue("CatalogApi:Port", 5187);
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("CatalogApi:Port must be between 1 and 65535.");
        if (builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
            throw new InvalidOperationException("Custom Kestrel endpoints are not supported. Use CatalogApi:Port; this host is loopback-only.");
        var browserOrigins = new BrowserOrigins(builder.Configuration.GetSection("CatalogApi:AllowedOrigins").Get<string[]>() ?? []);
        builder.Services.AddSingleton(browserOrigins);
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(browserOrigins.Allowed).WithMethods("GET", "POST", "PATCH", "DELETE")
                .WithHeaders("Authorization", "Content-Type", "If-Match")
                .WithExposedHeaders("ETag", "Location")));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port);
            options.Limits.MaxRequestBodySize = 16 * 1024;
        });
        builder.Services.AddSingleton<IStorageProviderFactory, SqliteStorageProviderFactory>();
        builder.Services.AddSingleton<IArtworkContentStore>(_ => new ManagedArtworkStore(builder.Configuration["CatalogApi:ArtworkRoot"]));
        builder.Services.TryAddSingleton<IArtworkImageProcessor, ArtworkImageProcessor>();
        builder.Services.TryAddScoped<IArtworkService, ArtworkService>();
        builder.Services.AddSingleton<IStorageProvider>(services =>
        {
            var path = builder.Configuration["CatalogApi:DatabasePath"];
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new InvalidOperationException("CatalogApi:DatabasePath must name an existing, absolute catalog database path.");
            return services.GetRequiredService<IStorageProviderFactory>()
                .Create(new StorageProviderDescriptor("sqlite", DatabasePath: path,
                    ArtworkRoot: services.GetRequiredService<IArtworkContentStore>().Root));
        });
        builder.Services.TryAddScoped<ILabelService, LabelService>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddScoped<ILabelMergeService, LabelMergeService>();
        builder.Services.TryAddScoped<ILabelRelationshipService, LabelRelationshipService>();
        builder.Services.TryAddScoped<ILabelGroupService, LabelGroupService>();
        builder.Services.TryAddSingleton<IPurchaseReleaseSource>(_ =>
        {
            var path = builder.Configuration["CatalogApi:PurchaseSourceDatabasePath"];
            if (string.IsNullOrWhiteSpace(path))
                path = builder.Configuration["CatalogApi:ScannerDatabasePath"] ?? Environment.GetEnvironmentVariable("TRACKSTASH_SCAN_DATABASE_PATH");
            if (string.IsNullOrWhiteSpace(path)) return new UnconfiguredPurchaseReleaseSource();
            if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) == Path.GetFullPath(builder.Configuration["CatalogApi:DatabasePath"]!))
                throw new InvalidOperationException("Purchase source must be an absolute scanner inventory path, separate from the catalog.");
            return new SqlitePurchaseReleaseSource(path);
        });
        builder.Services.TryAddScoped<IPurchaseReleaseInspector, PurchaseReleaseInspector>();
        builder.Services.TryAddScoped<IReleaseImportService, ReleaseImportService>();
        builder.Services.TryAddScoped<IReleaseService, ReleaseService>();
        builder.Services.AddSingleton<ICatalogBackupService>(services => new CatalogBackupService(
            new SqliteCatalogSnapshotSource(builder.Configuration["CatalogApi:DatabasePath"]!),
            services.GetRequiredService<IArtworkContentStore>(), builder.Configuration["CatalogApi:BackupRoot"]));
        builder.Services.AddSingleton<BackupSources>();
        builder.Services.AddSingleton<BackupJobs>();
        builder.Services.AddHostedService(services => services.GetRequiredService<BackupJobs>());
        builder.Services.AddHostedService<CatalogReadinessCheck>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = false;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "TrackStash Catalog", Version = "v1" });
            options.MapType<Microsoft.AspNetCore.Mvc.ProblemDetails>(() => new OpenApiSchema
            {
                Type = "object",
                Required = new HashSet<string> { "status", "title", "code", "traceId" },
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["type"] = new() { Type = "string" },
                    ["title"] = new() { Type = "string" },
                    ["status"] = new() { Type = "integer" },
                    ["detail"] = new() { Type = "string", Nullable = true },
                    ["instance"] = new() { Type = "string" },
                    ["code"] = new() { Type = "string", Description = "Stable API error code." },
                    ["traceId"] = new() { Type = "string", Description = "Correlation identifier for server logs." },
                    ["canonicalId"] = new() { Type = "string", Description = "Active label ID for a label_retired conflict." },
                    ["canonicalUri"] = new() { Type = "string", Description = "Survivor resource URI for a label_retired conflict." },
                    ["errors"] = new()
                    {
                        Type = "object", AdditionalProperties = new OpenApiSchema
                        {
                            Type = "array", Items = new OpenApiSchema { Type = "string" },
                        },
                    },
                },
            });
            options.MapType<UpdateLabelRequest>(() => new OpenApiSchema
            {
                Type = "object", AdditionalPropertiesAllowed = false,
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["name"] = new() { Type = "string", Nullable = false, MinLength = 1 },
                    ["sortName"] = new() { Type = "string", Nullable = true },
                    ["profile"] = new()
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = nameof(LabelProfileResponse) },
                    },
                },
            });
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer",
                Description = "Local catalog API key. Configure it out of source control.",
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
            });
            options.OperationFilter<LabelOperationFilter>();
            options.SchemaFilter<LabelProfileSchemaFilter>();
            options.SchemaFilter<LabelGroupSchemaFilter>();
        });
        var app = builder.Build();
        // Resolve configuration before accepting requests; no migrations or seeding run here.
        _ = app.Services.GetRequiredService<IStorageProvider>();
        _ = app.Services.GetRequiredService<IPurchaseReleaseSource>();
        app.UseExceptionHandler();
        app.UseMiddleware<LocalAccessMiddleware>();
        app.UseCors();
        app.UseMiddleware<BearerKeyMiddleware>(key);
        app.UseStatusCodePages(async context =>
        {
            var status = context.HttpContext.Response.StatusCode;
            var code = status switch { 415 => "unsupported_media_type", 413 => "body_too_large", _ => "http_error" };
            await ApiErrors.WriteAsync(context.HttpContext, status, code, "Request could not be completed");
        });
        app.UseSwagger(options => options.RouteTemplate = "openapi/{documentName}.json");
        app.MapLabels();
        app.MapLabelMerges();
        app.MapArtwork();
        app.MapLabelRelationships();
        app.MapLabelGroups();
        app.MapPurchaseReleases();
        app.MapReleaseImports();
        app.MapReleases();
        app.MapBackups();
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(webRoot, "admin", "index.html")))
        {
            var files = new PhysicalFileProvider(webRoot);
            app.Lifetime.ApplicationStopped.Register(files.Dispose);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html",
                new StaticFileOptions { FileProvider = files }).ExcludeFromDescription();
        }
        return app;
    }
}
