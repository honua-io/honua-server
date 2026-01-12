// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
// ✅ DEPENDENCY INVERSION: Server uses Core abstractions only
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.FileStorage;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;
using Serilog.Enrichers.Span;
using StackExchange.Redis;

// CLEAN ARCHITECTURE COMPOSITION ROOT
// This is the application layer that wires dependencies:
// - Core (abstractions): IDatabaseHealthChecker interface
// - Infrastructure (implementations): PostgresDatabaseHealthChecker
// - Server (composition): Registers IDatabaseHealthChecker → PostgresDatabaseHealthChecker
// Dependency flow: Server → (Core + Infrastructure), Infrastructure → Core

var builder = WebApplication.CreateBuilder(args);

var useTestSchemaHeaders = builder.Configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");
var forwardedHeadersEnabled = ConfigureForwardedHeaders(builder.Services, builder.Configuration);
ResolveEnvironmentSecretReferences(builder.Configuration);
var isTestEnvironment = builder.Environment.IsEnvironment("Test");

// Load optional security configuration without overriding environment-specific settings.
AddSecurityConfiguration(builder.Configuration, builder.Environment);

// Enable Aspire integrations only when Aspire configuration is present.
var useAspire = builder.Configuration.GetSection("Aspire").Exists();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? builder.Configuration["Aspire:StackExchange:Redis:ConnectionString"];

if (useAspire)
{
    // Add Aspire service defaults (OTel, health, resilience)
    builder.AddServiceDefaults();

    // Add Npgsql with connection from Aspire
    builder.AddNpgsqlDataSource("DefaultConnection");

    // Add Redis if configured
    if (!string.IsNullOrWhiteSpace(redisConnectionString))
    {
        builder.AddRedisDistributedCache("redis");
    }
}
else
{
    var tracingSection = builder.Configuration.GetSection(TracingOptions.SectionName);
    var tracingEnabled = tracingSection.GetValue<bool>(nameof(TracingOptions.Enabled));
    if (tracingSection.Exists() || tracingEnabled)
    {
        builder.AddTelemetryDefaults();
    }

    if (!string.IsNullOrWhiteSpace(redisConnectionString))
    {
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = "honua:";
        });
    }
}

if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.TryAddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(redisConnectionString));
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
}, preserveStaticLogger: false, writeToProviders: true);

// COMPOSITION ROOT: Register Infrastructure implementations for Core abstractions
// This is the only place where Server directly references Infrastructure
// Rest of Server code uses only Core abstractions (IFeatureReader/Writer, ITileProvider, IRelationshipStore, IDatabaseHealthChecker)
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

// Configure deployment mode options
builder.Services.Configure<DeploymentOptions>(
    builder.Configuration.GetSection(DeploymentOptions.SectionName));

// Configure tile options
ConfigureTileOptions(builder.Services, builder.Configuration);

// Configure caching options and register cache services
ConfigureCaching(builder.Services, builder.Configuration);

// Configure cloud file storage for imports and attachments
builder.Services.AddCloudFileStorage(builder.Configuration);

// Configure file upload security limits
builder.Services.Configure<FileUploadSecurityOptions>(
    builder.Configuration.GetSection(FileUploadSecurityOptions.SectionName));

// Register configuration validators to ensure application fails fast on invalid configuration
RegisterConfigurationValidators(builder.Services);

// Register health check services
builder.Services.AddSingleton<Honua.Server.Features.HealthCheck.MigrationState>();
builder.Services.AddScoped<Honua.Server.Features.HealthCheck.IReadinessCheckService,
    Honua.Server.Features.HealthCheck.ReadinessCheckService>();

// Register configuration documentation service for self-documenting admin endpoint
builder.Services.AddScoped<Honua.Server.Features.Admin.Services.ConfigurationDocumentationService>();

// Register shared Infrastructure services
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Services.IGeometryConverter,
    Honua.Server.Features.Infrastructure.Services.GeometryConverter>();

// Register shared validation services
builder.Services.AddValidationServices();

// Register feature services (FeatureServer, OGC, OData, Observability)
builder.Services.AddServerFeatures(builder.Configuration);

// Register Esri import job manager and background service
builder.Services.AddSingleton<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(sp =>
    new Honua.Server.Features.Import.UniversalProgressStore(
        sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Import.UniversalProgressStore>>()));
builder.Services.AddSingleton<Honua.Core.Features.Import.Abstractions.IDistributedImportJobManager>(sp =>
    new Honua.Server.Features.Import.RedisImportJobManager(
        sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(),
        sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Import.RedisImportJobManager>>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddHostedService<Honua.Server.Features.Import.EsriImportBackgroundService>();

// Register OData services and handlers
// Configure authentication options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.ApiKeyAuthenticationOptions>(options =>
{
    options.IsDevelopmentMode = builder.Environment.IsDevelopment();
    options.IsTestMode = builder.Environment.IsEnvironment("Test");
    options.AdminPassword = builder.Configuration["HONUA_ADMIN_PASSWORD"];
    options.DevAuthBypass = builder.Configuration["HONUA_DEV_AUTH"];
});

// Configure OIDC authentication options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.OidcAuthenticationOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Authentication.OidcAuthenticationOptions.SectionName));

// Configure authentication and authorization
builder.Services.AddApiKeyAuthentication();

// Add OIDC authentication if enabled
builder.Services.AddOidcAuthentication(builder.Configuration);
builder.Services.AddOidcAuthorization(builder.Configuration);
// Configure security headers
builder.Services.AddSecurityHeaders(builder.Configuration);
// Configure CORS policies
builder.Services.AddCorsPolicies(builder.Configuration, builder.Environment);

// Configure API versioning for admin endpoints
builder.Services.AddApiVersioning(options =>
{
    // Use URL-based versioning to maintain current /api/v1/ URLs
    options.ApiVersionReader = Asp.Versioning.ApiVersionReader.Combine(
        new Asp.Versioning.UrlSegmentApiVersionReader());

    // Default version for unversioned endpoints
    options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = false;

    // Use versioning conventions for better AOT compatibility
    options.UnsupportedApiVersionStatusCode = 400;
});

// Configure JSON serialization for ASP.NET Core (needed for minimal API body binding)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        Honua.Server.Features.FeatureServer.Models.FeatureServerJsonContext.Default,
        Honua.Server.Features.OData.Models.ODataJsonContext.Default,
        Honua.Server.Features.OgcFeatures.OgcJsonContext.Default,
        Honua.Server.Features.OgcTiles.OgcTilesJsonContext.Default,
        Honua.Server.Features.Admin.Models.SecureConnectionJsonContext.Default,
        Honua.Server.Features.Infrastructure.Monitoring.MetricsJsonContext.Default,
        Honua.Server.Features.Import.ImportJsonContext.Default,
        Honua.Server.Features.Import.EsriImportApiJsonContext.Default,
        Honua.Server.Features.Admin.OperationsProgressJsonContext.Default,
        Honua.Server.Features.Admin.Models.MetadataJsonContext.Default,
        Honua.Server.Features.Admin.Models.TableDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.ConfigurationJsonContext.Default,
        Honua.Server.Features.HealthCheck.HealthJsonContext.Default,
        Honua.Server.Features.Infrastructure.Models.ProblemJsonContext.Default,
        Honua.Server.Features.Infrastructure.Middleware.LimitsEnforcementJsonContext.Default,
        Honua.Server.Features.Infrastructure.Security.CspViolationJsonContext.Default);
});

// Add comprehensive IOptions configuration validation
builder.Services.AddConfigurationOptionsValidation();

var app = builder.Build();

if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (!string.IsNullOrEmpty(path))
    {
        var trimmedPath = path.TrimEnd('/');
        if (trimmedPath.Contains("/admin/connections//", StringComparison.OrdinalIgnoreCase) &&
            trimmedPath.EndsWith("/tables", StringComparison.OrdinalIgnoreCase))
        {
            await Honua.Server.Features.Infrastructure.Models.ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Connection ID is required")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }
    }

    if (!string.IsNullOrEmpty(path) && path.Contains("//", StringComparison.Ordinal))
    {
        var normalized = path;
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/");
        }

        context.Request.Path = new PathString(normalized);
    }

    await next().ConfigureAwait(false);
});

var configurationErrors = ConfigurationValidationService.ValidateConfiguration(
    app.Configuration,
    app.Logger,
    app.Environment.IsDevelopment() ||
    app.Environment.IsEnvironment("Test") ||
    app.Configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS"));

if (configurationErrors.Count > 0)
{
    var errorDetails = string.Join(Environment.NewLine, configurationErrors);
    ConfigurationLog.ConfigurationValidationFailed(app.Logger, configurationErrors.Count, errorDetails);
    throw new InvalidOperationException("Configuration validation failed. See logs for details.");
}

ConfigurationLog.ConfigurationValidationSucceeded(app.Logger);

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

// Add authentication and authorization middleware early to short-circuit unauthorized requests
app.UseApiKeyAuthentication();

// Add limits enforcement middleware (after auth, before request logging)
app.UseLimitsEnforcement();

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

// Configure secure connection management endpoints
app.MapSecureConnectionEndpoints();

// Configure admin metadata endpoints (v1)
app.MapMetadataEndpoints();

// Configure security endpoints (CSP violation reporting)
app.MapCspViolationReportEndpoint();

// Configure FeatureServer, OGC, and OData endpoints
app.MapServerFeatureEndpoints();

// Configure file import endpoints
app.MapImportEndpoints();

// Configure Esri service import endpoints
app.MapEsriImportEndpoints();

// Map health endpoints for Aspire dashboard (only when Aspire is enabled)
if (useAspire)
{
    app.MapDefaultEndpoints();
}

// Map metrics endpoints for monitoring APIs
app.MapMetricsEndpoints();
app.MapDatabasePerformanceEndpoints();

app.Run();

// Composition Root: Register Infrastructure implementations
// This is the only method in Server that directly references Infrastructure
// All other code uses Core abstractions only
static void RegisterInfrastructureServices(IServiceCollection services, IConfiguration configuration)
{
    var provider = configuration.GetValue<string>("DataSource:Provider");
    if (string.IsNullOrWhiteSpace(provider) ||
        provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("postgis", StringComparison.OrdinalIgnoreCase))
    {
        Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, configuration);
    }
    else
    {
        throw new InvalidOperationException($"Unsupported data source provider '{provider}'.");
    }

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

            // Apply caching decorator if enabled
            var cacheOptions = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            ILayerCatalog catalog = innerCatalog;
            if (cacheOptions.Enabled)
            {
                var cacheService = sp.GetRequiredService<ICacheService>();
                var options = sp.GetRequiredService<IOptions<CacheOptions>>();
                catalog = new CachingLayerCatalog(catalog, cacheService, options);
            }

            // Always wrap with monitoring for catalog metadata queries
            var performanceMonitor = sp.GetRequiredService<IPerformanceMonitor>();
            var logger = sp.GetRequiredService<ILogger<MonitoredLayerCatalogDecorator>>();
            return new MonitoredLayerCatalogDecorator(catalog, performanceMonitor, logger);
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

        var validator = new Honua.Core.Configuration.LimitsOptionsValidator();
        var validationResult = validator.Validate(Options.DefaultName, options);
        if (validationResult.Failed)
        {
            var failures = validationResult.Failures ?? [];
            var errorMessage = "Invalid limits configuration:" + Environment.NewLine +
                              string.Join(Environment.NewLine, failures);
            throw new InvalidOperationException(errorMessage);
        }
    });
}


// Database migration helper
async Task RunDatabaseMigrationsAsync()
{
    var migrationState = app.Services.GetRequiredService<Honua.Server.Features.HealthCheck.MigrationState>();

    if (builder.Configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS"))
    {
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsSkipped(app.Logger);
        migrationState.MarkSkipped();
        return;
    }

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(connectionString))
    {
        // Skip migrations if no connection string is configured
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseConnectionStringNotConfigured(app.Logger);
        migrationState.MarkSkipped();
        return;
    }

    Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsStarting(app.Logger);

    try
    {
        var migrationRunner = app.Services.GetRequiredService<IDatabaseMigrationRunner>();
        var result = await migrationRunner.RunMigrationsAsync(
            connectionString,
            Assembly.GetExecutingAssembly(),
            app.Lifetime.ApplicationStopping);

        if (!result.Successful)
        {
            var errorMessage = result.ErrorMessage ?? "Database migration failed.";
            var error = result.Error ?? new InvalidOperationException(errorMessage);
            Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, errorMessage, error);
            // Don't throw - let the app start and rely on health checks to indicate readiness
            migrationState.MarkFailed(errorMessage);
            return;
        }

        var scriptCount = result.AppliedScripts.Count;
        if (scriptCount > 0)
        {
            Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsCompleted(app.Logger, scriptCount);
            // Log individual script names for debugging
            foreach (var script in result.AppliedScripts)
            {
                Honua.Server.Features.Infrastructure.Logging.Log.MigrationScriptApplied(app.Logger, script);
            }
        }
        else
        {
            Honua.Server.Features.Infrastructure.Logging.Log.NoDatabaseMigrationsToApply(app.Logger);
        }

        migrationState.MarkSucceeded();
    }
    catch (Exception ex)
    {
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, ex.Message, ex);
        // Don't throw - let the app start and rely on health checks to indicate readiness
        migrationState.MarkFailed(ex.Message);
    }
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

static bool ConfigureForwardedHeaders(IServiceCollection services, IConfiguration configuration)
{
    var enabled = configuration.GetValue<bool>("ForwardedHeaders:Enabled");
    if (!enabled)
    {
        return false;
    }

    services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedProto |
                                   ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;

        var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        foreach (var proxy in knownProxies)
        {
            if (IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }
    });

    return true;
}

static void ResolveEnvironmentSecretReferences(ConfigurationManager configuration)
{
    ResolveEnvironmentSecretReference(configuration, "ConnectionStrings:DefaultConnection");
    ResolveEnvironmentSecretReference(configuration, "ConnectionStrings:redis");
    ResolveEnvironmentSecretReference(configuration, "Aspire:StackExchange:Redis:ConnectionString");
}

static void ResolveEnvironmentSecretReference(ConfigurationManager configuration, string key)
{
    var value = configuration[key];
    var resolved = SecretReferenceResolver.ResolveEnvironmentReference(value, key);
    if (!string.Equals(value, resolved, StringComparison.Ordinal))
    {
        configuration[key] = resolved;
    }
}

// Configure caching services with Redis and in-memory fallback
static void ConfigureCaching(IServiceCollection services, IConfiguration configuration)
{
    // Bind cache configuration with validation
    services.AddOptions<CacheOptions>()
        .Bind(configuration.GetSection(CacheOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddMemoryCache();

    // Register RedisCacheService (handles both Redis and fallback modes)
    // IDistributedCache is optionally provided by Aspire's AddRedisDistributedCache
    services.AddSingleton<RedisCacheService>(sp =>
    {
        var distributedCache = sp.GetService<IDistributedCache>();
        var options = sp.GetRequiredService<IOptions<CacheOptions>>();
        var logger = sp.GetRequiredService<ILogger<RedisCacheService>>();
        var performanceMonitor = sp.GetRequiredService<IPerformanceMonitor>();
        var redis = sp.GetService<IConnectionMultiplexer>();
        return new RedisCacheService(distributedCache, options, logger, performanceMonitor, redis);
    });

    // Register interfaces pointing to the singleton
    services.AddSingleton<ICacheService>(sp => sp.GetRequiredService<RedisCacheService>());
    services.AddSingleton<ICacheHealthChecker>(sp => sp.GetRequiredService<RedisCacheService>());

    services.AddSingleton<IResponseCache>(sp =>
    {
        var innerCache = new MemoryResponseCache(
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<ILogger<MemoryResponseCache>>());
        return new MonitoredResponseCacheDecorator(
            innerCache,
            sp.GetRequiredService<IPerformanceMonitor>(),
            sp.GetRequiredService<ILogger<MonitoredResponseCacheDecorator>>());
    });

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

// Registers configuration validators for all options classes to ensure fail-fast behavior
// on invalid configuration. Prevents the application from starting with invalid settings.
static void RegisterConfigurationValidators(IServiceCollection services)
{
    // Register IValidateOptions<T> implementations for each configuration class
    // These will be invoked by the options framework during application startup
    // and will cause the app to fail to start if configuration is invalid

    services.AddSingleton<IValidateOptions<LimitsOptions>>(new LimitsOptionsValidator());
    services.AddSingleton<IValidateOptions<CacheOptions>>(new CacheOptionsValidator());
    services.AddSingleton<IValidateOptions<CloudStorageOptions>>(new CloudStorageOptionsValidator());
    services.AddSingleton<IValidateOptions<OidcAuthenticationOptions>>(new OidcAuthenticationOptionsValidator());
    services.AddSingleton<IValidateOptions<FileUploadSecurityOptions>>(new FileUploadSecurityOptionsValidator());
}


// Configure unified operations progress endpoints
app.MapOperationsProgressEndpoints();
