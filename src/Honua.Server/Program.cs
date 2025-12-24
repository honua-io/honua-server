// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using DbUp;
// ✅ DEPENDENCY INVERSION: Server uses Core abstractions only
using Honua.Server.Features.Admin;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.OData;
using Honua.Server.Features.OgcFeatures;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;
using Serilog.Enrichers.Span;

// CLEAN ARCHITECTURE COMPOSITION ROOT
// This is the application layer that wires dependencies:
// - Core (abstractions): IDatabaseHealthChecker interface
// - Infrastructure (implementations): PostgresDatabaseHealthChecker
// - Server (composition): Registers IDatabaseHealthChecker → PostgresDatabaseHealthChecker
// Dependency flow: Server → (Core + Infrastructure), Infrastructure → Core

var builder = WebApplication.CreateBuilder(args);

// Skip Aspire configuration during testing to avoid connection string conflicts
var isTestEnvironment = builder.Environment.IsEnvironment("Test");
var useAspire = !isTestEnvironment && !builder.Environment.IsDevelopment();

if (useAspire)
{
    // Add Aspire service defaults (OTel, health, resilience)
    builder.AddServiceDefaults();

    // Add Npgsql with connection from Aspire
    builder.AddNpgsqlDataSource("honua");

    // Add Redis if configured
    builder.AddRedisDistributedCache("redis");
}

// Configure Serilog for structured logging with AOT compatibility
builder.Host.UseSerilog((context, services, config) =>
{
    var isDevelopment = context.HostingEnvironment.IsDevelopment();

    config
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", Serilog.Events.LogEventLevel.Information)
        .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithSpan()  // OpenTelemetry trace/span IDs
        .Enrich.WithProperty("Application", "Honua")
        .Enrich.WithProperty("Version", typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");

    if (isDevelopment)
    {
        // Development: Human-readable console output
        config.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
    }
    else
    {
        // Production: Compact JSON for log aggregation
        config.WriteTo.Console(formatter: new Serilog.Formatting.Compact.CompactJsonFormatter());
    }
});

// COMPOSITION ROOT: Register Infrastructure implementations for Core abstractions
// This is the only place where Server directly references Infrastructure
// Rest of Server code uses only Core abstractions (IFeatureStore, IDatabaseHealthChecker)
// Skip infrastructure registration in test environment - WebAppFixture handles it
if (!isTestEnvironment)
{
    RegisterInfrastructureServices(builder.Services, builder.Configuration);
}
else
{
    builder.Services.AddScoped<Honua.Core.Queries.Filters.ISqlFilterTranslator>(_ =>
        new Honua.Postgres.Queries.Filters.PostgresSqlFilterTranslator(
            useJsonAttributes: true,
            attributesColumn: "attributes",
            geometryColumn: "geometry",
            primaryKeyColumn: "objectid"));
}

// Configure limits with validation
ConfigureLimits(builder.Services, builder.Configuration);

// Configure tile options
ConfigureTileOptions(builder.Services, builder.Configuration);

// Register health check services
builder.Services.AddScoped<Honua.Server.Features.HealthCheck.IReadinessCheckService,
    Honua.Server.Features.HealthCheck.ReadinessCheckService>();

// Register shared Infrastructure services
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Services.IGeometryConverter,
    Honua.Server.Features.Infrastructure.Services.GeometryConverter>();

// Register FeatureServer services - they will use the shared geometry converter
builder.Services.AddScoped<Honua.Server.Features.FeatureServer.Services.IQueryFormatter,
    Honua.Server.Features.FeatureServer.Services.QueryFormatter>();
builder.Services.AddScoped<Honua.Server.Features.FeatureServer.Services.IFeatureQueryValidator,
    Honua.Server.Features.FeatureServer.Services.FeatureQueryValidator>();
builder.Services.AddScoped<Honua.Server.Features.FeatureServer.Services.FeatureServerServices>();
builder.Services.AddScoped<Honua.Server.Features.FeatureServer.FeatureServerHandler>();

// OData services use existing FeatureServer services

// Configure authentication options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.ApiKeyAuthenticationOptions>(options =>
{
    options.IsDevelopmentMode = builder.Environment.IsDevelopment();
    options.AdminPassword = builder.Configuration["HONUA_ADMIN_PASSWORD"];
    options.DevAuthBypass = builder.Configuration["HONUA_DEV_AUTH"];
});

// Configure authentication and authorization
builder.Services.AddApiKeyAuthentication();
// Configure security headers
ConfigureSecurityHeaders(builder.Services);

// Configure output caching for metadata endpoints
ConfigureOutputCaching(builder.Services);
// Configure response compression
ConfigureResponseCompression(builder.Services);

// Configure JSON serialization for ASP.NET Core (needed for minimal API body binding)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        Honua.Server.Features.FeatureServer.Models.FeatureServerJsonContext.Default,
        Honua.Server.Features.OData.Models.ODataJsonContext.Default,
        Honua.Server.Features.OgcFeatures.OgcJsonContext.Default);
});

var app = builder.Build();

// Add security headers middleware (first in pipeline for all requests)
app.UseSecurityHeaders();

// Add response compression middleware (early in pipeline)
app.UseResponseCompression();

// Add correlation ID middleware early in pipeline (before request logging)
app.UseCorrelationId();

// Add limits enforcement middleware (after correlation ID, before request logging)
app.UseLimitsEnforcement();

// Add authentication and authorization middleware
app.UseApiKeyAuthentication();

// Enable output caching middleware
app.UseOutputCache();

// Configure Serilog request logging with custom enrichment
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        diagnosticContext.Set("Protocol", httpContext.Request.Protocol);

        if (httpContext.User.Identity?.IsAuthenticated == true)
            diagnosticContext.Set("UserId", httpContext.User.FindFirst("sub")?.Value);
    };

    // Exclude health check endpoints from request logging (configured in appsettings.json)
    options.GetLevel = (httpContext, elapsed, ex) => ex != null
        ? Serilog.Events.LogEventLevel.Error
        : httpContext.Request.Path.StartsWithSegments("/healthz")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
});

// Log application startup
Honua.Server.Features.Infrastructure.Logging.Log.ApplicationStarting(app.Logger,
    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
    app.Environment.EnvironmentName);

// Run database migrations on startup
await RunDatabaseMigrationsAsync();

// Configure health endpoints
app.MapHealthEndpoints();

// Configure admin endpoints
app.MapAdminEndpoints();

// Configure FeatureServer endpoints
app.MapFeatureServerEndpoints();

// Configure FeatureServer attachment endpoints
app.MapAttachmentEndpoints();

// Configure OGC API Features endpoints
app.MapOgcFeaturesEndpoints();

// Configure OData v4 endpoints
app.MapODataEndpoints();

// Configure file import endpoints
app.MapImportEndpoints();

// Map health endpoints for Aspire dashboard (only when Aspire is enabled)
if (useAspire)
{
    app.MapDefaultEndpoints();
}

app.Run();

// Composition Root: Register Infrastructure implementations
// This is the only method in Server that directly references Infrastructure
// All other code uses Core abstractions only
static void RegisterInfrastructureServices(IServiceCollection services, IConfiguration configuration)
{
    // Register PostgreSQL services (the only direct Infrastructure reference)
    Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, configuration);
}

// Configure limits with validation
static void ConfigureLimits(IServiceCollection services, IConfiguration configuration)
{
    // Bind configuration with validation
    services.Configure<Honua.Core.Configuration.LimitsOptions>(options =>
    {
        configuration.GetSection(Honua.Core.Configuration.LimitsOptions.SectionName).Bind(options);

        // Validate configuration during startup
        var validationErrors = Honua.Core.Configuration.LimitsOptionsValidator.Validate(options);
        if (validationErrors.Count != 0)
        {
            var errorMessage = "Invalid limits configuration:" + Environment.NewLine +
                              string.Join(Environment.NewLine, validationErrors);
            throw new InvalidOperationException(errorMessage);
        }
    });
}

// Configure security headers policy
static void ConfigureSecurityHeaders(IServiceCollection services)
{
    services.AddSecurityHeaderPolicies()
        .SetDefaultPolicy(policy =>
        {
            // Add required security headers per MVP Plan
            policy.AddDefaultSecurityHeaders() // Adds X-Content-Type-Options, X-Frame-Options, Referrer-Policy
                .AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds: 63072000) // 2 years HSTS
                .AddContentSecurityPolicy(builder =>
                {
                    // Comprehensive CSP for API - matches test expectations
                    builder.AddDefaultSrc().Self();
                    builder.AddScriptSrc().Self();
                    builder.AddStyleSrc().Self().UnsafeInline(); // Allow inline styles for minimal API responses
                    builder.AddImgSrc().Self().Data(); // Allow data: URIs for inline images
                    builder.AddConnectSrc().Self();
                    builder.AddFontSrc().Self();
                    builder.AddMediaSrc().Self();
                    builder.AddObjectSrc().None();
                    builder.AddFrameAncestors().None(); // frame-ancestors 'none'
                    builder.AddFormAction().Self();
                })
                .AddCustomHeader("Cross-Origin-Opener-Policy", "same-origin") // COOP: same-origin
                .AddCustomHeader("Cross-Origin-Embedder-Policy", "require-corp") // COEP: require-corp
                .AddCustomHeader("Permissions-Policy",
                    "camera=(), microphone=(), geolocation=(), payment=(), usb=(), " +
                    "magnetometer=(), gyroscope=(), accelerometer=(), ambient-light-sensor=(), " +
                    "autoplay=(), encrypted-media=(), fullscreen=(), picture-in-picture=()"); // Restrictive permissions
        });
}

// Configure response compression for GeoJSON and JSON responses
static void ConfigureResponseCompression(IServiceCollection services)
{
    // MIME types for geospatial data formats
    string[] additionalMimeTypes = [
        "application/geo+json",    // GeoJSON format
        "application/json"         // Standard JSON responses
    ];

    services.AddResponseCompression(options =>
    {
        // Enable compression for HTTPS requests
        options.EnableForHttps = true;

        // Configure compression providers
        options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
        options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();

        // Add MIME types for geospatial data formats
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(additionalMimeTypes);
    });

    // Configure Brotli compression for fastest performance (low latency)
    services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(options =>
    {
        options.Level = System.IO.Compression.CompressionLevel.Fastest;
    });

    // Configure Gzip compression for fastest performance (fallback)
    services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
    {
        options.Level = System.IO.Compression.CompressionLevel.Fastest;
    });
}

// Database migration helper
async Task RunDatabaseMigrationsAsync()
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(connectionString))
    {
        // Skip migrations if no connection string is configured
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseConnectionStringNotConfigured(app.Logger);
        return;
    }

    Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsStarting(app.Logger);

    try
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .WithTransaction()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, result.Error.Message, result.Error);
            // Don't throw - let the app start and rely on health checks to indicate readiness
            return;
        }

        if (result.Scripts.Any())
        {
            Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsCompleted(app.Logger, result.Scripts.Count());
            // Log individual script names for debugging
            foreach (var script in result.Scripts)
            {
                Honua.Server.Features.Infrastructure.Logging.Log.MigrationScriptApplied(app.Logger, script.Name);
            }
        }
        else
        {
            Honua.Server.Features.Infrastructure.Logging.Log.NoDatabaseMigrationsToApply(app.Logger);
        }
    }
    catch (Exception ex)
    {
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, ex.Message, ex);
        // Don't throw - let the app start and rely on health checks to indicate readiness
    }
}

// Configure output caching for metadata endpoints
static void ConfigureOutputCaching(IServiceCollection services)
{
    services.AddOutputCache(options =>
    {
        // Service metadata caching policy
        options.AddPolicy("ServiceMetadata", policy =>
        {
            policy.Expire(TimeSpan.FromMinutes(5));
            policy.SetVaryByRouteValue("serviceId");
            policy.SetVaryByQuery("f"); // Support for format parameter if used
            policy.Tag("service-metadata", "metadata");
        });

        // Layer metadata caching policy
        options.AddPolicy("LayerMetadata", policy =>
        {
            policy.Expire(TimeSpan.FromMinutes(5));
            policy.SetVaryByRouteValue("serviceId", "layerId");
            policy.SetVaryByQuery("f"); // Support for format parameter if used
            policy.Tag("layer-metadata", "metadata");
        });

        // OGC API Features landing page caching policy
        options.AddPolicy("OgcLandingPage", policy =>
        {
            policy.Expire(TimeSpan.FromMinutes(30));
            policy.SetVaryByQuery("f");
            policy.SetVaryByHeader("Accept");
            policy.Tag("ogc-metadata", "metadata");
        });

        // OGC API Features conformance caching policy
        options.AddPolicy("OgcConformance", policy =>
        {
            policy.Expire(TimeSpan.FromHours(1));
            policy.SetVaryByQuery("f");
            policy.SetVaryByHeader("Accept");
            policy.Tag("ogc-metadata", "metadata");
        });

        // OGC API Features collections list caching policy
        options.AddPolicy("OgcCollections", policy =>
        {
            policy.Expire(TimeSpan.FromMinutes(10));
            policy.SetVaryByQuery("f");
            policy.SetVaryByHeader("Accept");
            policy.Tag("ogc-metadata", "metadata");
        });

        // OGC API Features single collection caching policy
        options.AddPolicy("OgcCollection", policy =>
        {
            policy.Expire(TimeSpan.FromMinutes(10));
            policy.SetVaryByRouteValue("collectionId");
            policy.SetVaryByQuery("f");
            policy.SetVaryByHeader("Accept");
            policy.Tag("ogc-metadata", "metadata");
        });

        options.AddPolicy("OgcOpenApi", policy =>
        {
            policy.Expire(TimeSpan.FromHours(1));
            policy.SetVaryByQuery("f");
            policy.SetVaryByHeader("Accept");
            policy.Tag("ogc-metadata", "metadata");
        });

        // MVT tile caching policy
        options.AddPolicy("MvtTile", policy =>
        {
            policy.Expire(TimeSpan.FromHours(1)); // Cache tiles for 1 hour by default
            policy.SetVaryByRouteValue("layerId", "z", "x", "y");
            policy.SetVaryByQuery("where"); // Support for WHERE clause filtering
            policy.Tag("mvt-tiles", "tiles");
        });

        // OData features caching policy (temporarily disabled for Issue 46 performance testing)
        // options.AddPolicy("ODataFeatures", policy =>
        // {
        //     policy.Expire(TimeSpan.FromMinutes(10)); // Cache feature queries for 10 minutes
        //     policy.SetVaryByRouteValue("layerId");
        //     policy.SetVaryByQuery("$filter", "$select", "$top", "$skip", "$orderby", "$count");
        //     policy.Tag("odata-features", "features");
        // });

        // Note: No default base policy - endpoints must explicitly opt into caching for security
    });
}

// Configure tile options with validation
static void ConfigureTileOptions(IServiceCollection services, IConfiguration configuration)
{
    // Bind configuration with default values
    services.Configure<Honua.Core.Features.Tiles.TileOptions>(options =>
    {
        configuration.GetSection(Honua.Core.Features.Tiles.TileOptions.SectionName).Bind(options);
    });
}

// Make Program accessible to WebApplicationFactory
/// <summary>
/// Application entry point for test hosting.
/// </summary>
public partial class Program { }
