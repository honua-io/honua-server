// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using DbUp;
// ✅ DEPENDENCY INVERSION: Server uses Core abstractions only
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Caching;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData;
using Honua.Server.Features.OgcFeatures;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration.Json;
using Npgsql;
using Serilog;
using Serilog.Enrichers.Span;

// CLEAN ARCHITECTURE COMPOSITION ROOT
// This is the application layer that wires dependencies:
// - Core (abstractions): IDatabaseHealthChecker interface
// - Infrastructure (implementations): PostgresDatabaseHealthChecker
// - Server (composition): Registers IDatabaseHealthChecker → PostgresDatabaseHealthChecker
// Dependency flow: Server → (Core + Infrastructure), Infrastructure → Core

var builder = WebApplication.CreateBuilder(args);

var useTestSchemaHeaders = builder.Configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");

// Load optional security configuration without overriding environment-specific settings.
AddSecurityConfiguration(builder.Configuration, builder.Environment);

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

if (useTestSchemaHeaders)
{
    builder.Services.AddScoped<SchemaContext>();
    builder.Services.AddScoped<ISchemaContext>(serviceProvider =>
        serviceProvider.GetRequiredService<SchemaContext>());
}

// Configure limits with validation
ConfigureLimits(builder.Services, builder.Configuration);

// Configure tile options
ConfigureTileOptions(builder.Services, builder.Configuration);

// Configure rate limiting options
ConfigureRateLimiting(builder.Services, builder.Configuration);

// Configure caching options and register cache services
ConfigureCaching(builder.Services, builder.Configuration);

// Register health check services
builder.Services.AddScoped<Honua.Server.Features.HealthCheck.IReadinessCheckService,
    Honua.Server.Features.HealthCheck.ReadinessCheckService>();

// Register shared Infrastructure services
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Services.IGeometryConverter,
    Honua.Server.Features.Infrastructure.Services.GeometryConverter>();

// Register shared validation services
builder.Services.AddValidationServices();

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
builder.Services.AddSecurityHeaders(builder.Configuration);
// Configure CORS policies
builder.Services.AddCorsPolicies(builder.Configuration, builder.Environment);

// Configure output caching for metadata endpoints
ConfigureOutputCaching(builder.Services);
// Configure ETag support for cache validation
builder.Services.AddETags();
// Configure performance monitoring services
builder.Services.AddPerformanceMonitoring();
// Configure response compression
ConfigureResponseCompression(builder.Services);

// Configure JSON serialization for ASP.NET Core (needed for minimal API body binding)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        Honua.Server.Features.FeatureServer.Models.FeatureServerJsonContext.Default,
        Honua.Server.Features.OData.Models.ODataJsonContext.Default,
        Honua.Server.Features.Infrastructure.Monitoring.MetricsJsonContext.Default,
        Honua.Server.Features.OgcFeatures.OgcJsonContext.Default);
});

var app = builder.Build();

// Add security headers middleware (first in pipeline for all requests)
app.UseSecurityHeaders();

// Add response compression middleware (early in pipeline)
app.UseResponseCompression();

// Add correlation ID middleware early in pipeline (before request logging)
app.UseCorrelationId();

if (useTestSchemaHeaders)
{
    app.UseTestSchemaHeaders();
}

// Add performance monitoring middleware (tracks request duration and memory)
app.UsePerformanceMonitoring();

// Add global exception handling middleware (after correlation ID for exception logging)
app.UseGlobalExceptionHandling();

// Add CORS middleware before auth to handle preflight requests
app.UseHonuaCors(app.Environment);

// Add limits enforcement middleware (after correlation ID, before request logging)
app.UseLimitsEnforcement();

// Add rate limiting middleware before authentication and caching
app.UseRateLimiting();

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

// Configure security endpoints (CSP violation reporting)
app.MapCspViolationReportEndpoint();

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

// Map metrics endpoints for monitoring APIs
app.MapMetricsEndpoints();

app.Run();

// Composition Root: Register Infrastructure implementations
// This is the only method in Server that directly references Infrastructure
// All other code uses Core abstractions only
static void RegisterInfrastructureServices(IServiceCollection services, IConfiguration configuration)
{
    // Register PostgreSQL services (the only direct Infrastructure reference)
    Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, configuration);

    // Wrap ILayerCatalog with caching decorator
    // This uses the decorator pattern to add caching behavior transparently
    var innerCatalogDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ILayerCatalog));
    if (innerCatalogDescriptor != null)
    {
        services.Remove(innerCatalogDescriptor);

        services.AddScoped<ILayerCatalog>(sp =>
        {
            // Resolve the inner catalog (PostgresLayerCatalog)
            ILayerCatalog innerCatalog;
            if (innerCatalogDescriptor.ImplementationFactory != null)
            {
                innerCatalog = (ILayerCatalog)innerCatalogDescriptor.ImplementationFactory(sp);
            }
            else if (innerCatalogDescriptor.ImplementationType != null)
            {
                innerCatalog = (ILayerCatalog)ActivatorUtilities.CreateInstance(sp, innerCatalogDescriptor.ImplementationType);
            }
            else
            {
                throw new InvalidOperationException("Unable to resolve inner ILayerCatalog implementation");
            }

            // Check if caching is enabled
            var cacheOptions = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            if (!cacheOptions.Enabled)
            {
                return innerCatalog;
            }

            // Wrap with caching decorator
            var cacheService = sp.GetRequiredService<ICacheService>();
            var options = sp.GetRequiredService<IOptions<CacheOptions>>();
            return new CachingLayerCatalog(innerCatalog, cacheService, options);
        });
    }
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
    if (builder.Configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS"))
    {
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsSkipped(app.Logger);
        return;
    }

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
        var migrationConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public",
        }.ConnectionString;

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(migrationConnectionString)
            .JournalToPostgresqlTable("public", "schema_versions")
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

// Configure rate limiting options
static void ConfigureRateLimiting(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<RateLimitOptions>(options =>
    {
        configuration.GetSection(RateLimitOptions.SectionName).Bind(options);
    });
}

// Configure caching services with Redis and in-memory fallback
static void ConfigureCaching(IServiceCollection services, IConfiguration configuration)
{
    // Bind cache configuration
    services.Configure<CacheOptions>(options =>
    {
        configuration.GetSection(CacheOptions.SectionName).Bind(options);
    });

    // Register RedisCacheService (handles both Redis and fallback modes)
    // IDistributedCache is optionally provided by Aspire's AddRedisDistributedCache
    services.AddSingleton<RedisCacheService>(sp =>
    {
        var distributedCache = sp.GetService<IDistributedCache>();
        var options = sp.GetRequiredService<IOptions<CacheOptions>>();
        var logger = sp.GetRequiredService<ILogger<RedisCacheService>>();
        return new RedisCacheService(distributedCache, options, logger);
    });

    // Register interfaces pointing to the singleton
    services.AddSingleton<ICacheService>(sp => sp.GetRequiredService<RedisCacheService>());
    services.AddSingleton<ICacheHealthChecker>(sp => sp.GetRequiredService<RedisCacheService>());

    // Register the CachingLayerCatalog - it will be wired via decorator pattern in RegisterInfrastructureServices
}

static void AddSecurityConfiguration(ConfigurationManager configuration, IHostEnvironment environment)
{
    const string securitySettingsFile = "appsettings.Security.json";
    configuration.AddJsonFile(securitySettingsFile, optional: true, reloadOnChange: true);

    var sources = configuration.Sources;
    var securityIndex = -1;
    for (var i = sources.Count - 1; i >= 0; i--)
    {
        if (sources[i] is JsonConfigurationSource jsonSource &&
            string.Equals(jsonSource.Path, securitySettingsFile, StringComparison.OrdinalIgnoreCase))
        {
            securityIndex = i;
            break;
        }
    }

    if (securityIndex < 0)
    {
        return;
    }

    var securitySource = sources[securityIndex];
    sources.RemoveAt(securityIndex);

    var envSettingsPath = $"appsettings.{environment.EnvironmentName}.json";
    var insertIndex = -1;
    for (var i = 0; i < sources.Count; i++)
    {
        if (sources[i] is JsonConfigurationSource jsonSource &&
            string.Equals(jsonSource.Path, envSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            insertIndex = i;
            break;
        }
    }

    if (insertIndex < 0)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource jsonSource &&
                string.Equals(jsonSource.Path, "appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                insertIndex = i + 1;
                break;
            }
        }
    }

    if (insertIndex < 0)
    {
        insertIndex = 0;
    }

    sources.Insert(insertIndex, securitySource);
}

// Make Program accessible to WebApplicationFactory
/// <summary>
/// Application entry point for test hosting.
/// </summary>
public partial class Program { }
