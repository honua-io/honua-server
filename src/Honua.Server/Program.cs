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
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Jobs;
using Honua.Server.Features.Admin.OperateFixtures;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.CloudDemo;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Console;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.Server.Features.Export;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.FileStorage;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;
using Honua.Server.Features.Infrastructure.AuditLog;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Features.Infrastructure.Extensions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.MultiTenancy;
using Honua.Server.Features.Infrastructure.RateLimiting;
using Honua.Server.Features.Infrastructure.Redis;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Mobile.Auth;
using Honua.Server.Features.Mobile.Diagnostics;
using Honua.Server.Features.Mobile.FieldCollection;
using Honua.Server.Features.Orchestration;
using Honua.Server.Features.Studio;
using Honua.Server.Features.PackageReview;
using Honua.Server.Features.Streaming;
using Honua.Server.Startup;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Enrichers.Span;
using StackExchange.Redis;
using Microsoft.AspNetCore.Server.Kestrel.Https;

// CLEAN ARCHITECTURE COMPOSITION ROOT
// This is the application layer that wires dependencies:
// - Core (abstractions): IDatabaseHealthChecker interface
// - Infrastructure (implementations): PostgresDatabaseHealthChecker
// - Server (composition): Registers IDatabaseHealthChecker → PostgresDatabaseHealthChecker
// Dependency flow: Server → (Core + Infrastructure), Infrastructure → Core

var builder = WebApplication.CreateBuilder(args);

var useTestSchemaHeaders = builder.Configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");
var forwardedHeadersEnabled = StartupConfigurationHelpers.ConfigureForwardedHeaders(builder.Services, builder.Configuration);
StartupConfigurationHelpers.ResolveEnvironmentSecretReferences(builder.Configuration);
var isTestEnvironment = builder.Environment.IsEnvironment("Test");
var registerInfrastructureInTestEnvironment =
    builder.Configuration.GetValue<bool>("HONUA_REGISTER_TEST_INFRASTRUCTURE") ||
    string.Equals(
        Environment.GetEnvironmentVariable("HONUA_REGISTER_TEST_INFRASTRUCTURE"),
        "true",
        StringComparison.OrdinalIgnoreCase);
var serveStacOpsDemo = builder.Configuration.GetValue(
    "ServeStacOpsDemo",
    builder.Configuration.GetValue(
        "HONUA_SERVE_STAC_DEMO",
        builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test")));
var serveApiDocs = builder.Configuration.GetValue(
    "ServeApiDocs",
    builder.Configuration.GetValue("HONUA_SERVE_API_DOCS", builder.Environment.IsDevelopment()));
var requiresDurableDistributedEvents = !builder.Environment.IsDevelopment() && !isTestEnvironment;
var stacOpsDemoPathPrefix = new PathString("/samples/stac-ops");
if (serveStacOpsDemo && !builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// Load optional security configuration without overriding environment-specific settings.
StartupConfigurationHelpers.AddSecurityConfiguration(builder.Configuration, builder.Environment);
var clientCertificateMode = builder.Configuration.GetValue<ClientCertificateAuthenticationMode>(
    "Authentication:ClientCertificates:Mode");
if (clientCertificateMode != ClientCertificateAuthenticationMode.Disabled)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ConfigureHttpsDefaults(httpsOptions =>
        {
            httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        });
    });
}
builder.Services.AddDataProtection();

// Enable Aspire integrations only when Aspire configuration is present.
var useAspire = builder.Configuration.GetSection("Aspire").Exists();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? builder.Configuration["Aspire:StackExchange:Redis:ConnectionString"];
var redisCacheEntitled = await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(builder.Configuration);
var redisCacheConnectionString = redisCacheEntitled ? redisConnectionString : null;
var redisInfrastructureConnectionString = RedisConnectionSelector.SelectInfrastructureConnectionString(
    redisConnectionString,
    redisCacheEntitled,
    requiresDurableDistributedEvents);
ConnectionMultiplexer? connectedRedis = null;

if (useAspire)
{
    // Add Aspire service defaults (OTel, health, resilience)
    builder.AddServiceDefaults();

    // Add Npgsql with connection from Aspire
    builder.AddNpgsqlDataSource("DefaultConnection");

    // Add Redis if configured, otherwise fallback to in-memory cache
    if (!string.IsNullOrWhiteSpace(redisCacheConnectionString))
    {
        builder.AddRedisDistributedCache("redis");
    }
    else
    {
        builder.Services.AddDistributedMemoryCache();
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

    // Add Redis if configured, otherwise fallback to in-memory cache
    if (!string.IsNullOrWhiteSpace(redisCacheConnectionString))
    {
        var cacheKeyPrefix = builder.Configuration.GetSection("Cache")["KeyPrefix"] ?? "honua:";
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisCacheConnectionString;
            options.InstanceName = cacheKeyPrefix;
        });
    }
    else
    {
        builder.Services.AddDistributedMemoryCache();
    }
}

if (!string.IsNullOrWhiteSpace(redisInfrastructureConnectionString))
{
    var requireRedisAtStartup = requiresDurableDistributedEvents;
    try
    {
        var redisOptions = ConfigurationOptions.Parse(redisInfrastructureConnectionString, ignoreUnknown: true);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = Math.Max(redisOptions.ConnectRetry, 3);
        redisOptions.ReconnectRetryPolicy ??= new ExponentialRetry(5_000);

        connectedRedis = ConnectionMultiplexer.Connect(redisOptions);
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(connectedRedis);

        if (!connectedRedis.IsConnected)
        {
            if (requireRedisAtStartup)
            {
                throw new InvalidOperationException(
                    "Redis durable coordination is required in this environment, but the Redis multiplexer did not establish an active startup connection.");
            }

            var startupLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Program>();
            ProgramLog.RedisStartupConnectionInactive(startupLogger);
        }
    }
    catch (Exception ex)
    {
        if (requireRedisAtStartup)
        {
            throw new InvalidOperationException(
                "Redis durable coordination is required in this environment, but startup could not establish the Redis multiplexer.",
                ex);
        }

        var startupLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Program>();
        ProgramLog.RedisStartupConnectionFailed(startupLogger, ex);
        // Do not register IConnectionMultiplexer — services that request it via GetService<> will receive null
    }
}

// Configure Serilog for structured logging with AOT compatibility
builder.Host.UseSerilog((context, services, config) =>
{
    var isDevelopment = context.HostingEnvironment.IsDevelopment();
    var benchmarkQuietLogs = context.Configuration.GetValue<bool>("HONUA_BENCHMARK_QUIET_LOGS");

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

    if (benchmarkQuietLogs)
    {
        config
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Serilog.AspNetCore.RequestLoggingMiddleware", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Honua.Server.Features.Infrastructure.Middleware.SecurityHeadersMiddleware", Serilog.Events.LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Server.Features.Infrastructure.Authentication.ApiKeyAuthenticationHandler", Serilog.Events.LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Server.Features.Protocols.Ogc.Api.Features.OgcFeaturesQueryHandler", Serilog.Events.LogEventLevel.Warning);
    }

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
// Skip infrastructure registration in test environment by default - WebAppFixture handles it.
// Standalone Python/JS harnesses can opt back in with HONUA_REGISTER_TEST_INFRASTRUCTURE=true.
if (!isTestEnvironment || registerInfrastructureInTestEnvironment)
{
    InfrastructureCompositionRoot.RegisterInfrastructureServices(builder.Services, builder.Configuration);
}

// PERFORMANCE ENHANCEMENTS: Add advanced monitoring and optimization services
// These enhancements are designed to push server performance from 9.1/10 toward 9.5+/10
builder.Services.AddPerformanceEnhancements(options =>
{
    options.EnableQueryPerformanceMonitoring = true;
    options.EnableResourceLeakDetection = !builder.Environment.IsProduction();
    options.EnableEnhancedExceptionTelemetry = true;
    options.EnableQueryResultCaching = false;
    options.EnableDetailedMetrics = !builder.Environment.IsProduction();
});

// Add query result caching (Server level - requires IMemoryCache)
builder.Services.Configure<Honua.Server.Features.Infrastructure.Caching.QueryResultCacheOptions>(options =>
{
    options.Enabled = builder.Configuration.GetValue<bool>("Cache:ResponseCachingEnabled");
    options.DefaultExpiration = TimeSpan.FromMinutes(5);
    options.MaxCacheSizeBytes = 50 * 1024 * 1024; // 50 MB
    options.MaxCachedItems = 5000;
    options.EnableCompression = true;
    options.CompressionThresholdBytes = 1024;
    options.EnableWarmup = false; // Disable by default
    options.EnableDetailedMetrics = !builder.Environment.IsProduction();
});

builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Caching.IQueryResultCacheManager,
    Honua.Server.Features.Infrastructure.Caching.QueryResultCacheManager>();

if (useTestSchemaHeaders)
{
    builder.Services.AddScoped<SchemaContext>();
    builder.Services.AddScoped<ISchemaContext>(serviceProvider =>
        serviceProvider.GetRequiredService<SchemaContext>());
}

// Configure limits with validation
InfrastructureCompositionRoot.ConfigureLimits(builder.Services, builder.Configuration);

// Configure deployment mode options
builder.Services.Configure<DeploymentOptions>(
    builder.Configuration.GetSection(DeploymentOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<ControlPlaneOptions>, ControlPlaneOptionsValidator>();
builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection(ControlPlaneOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<KubernetesExecutionOptions>()
    .Bind(builder.Configuration.GetSection($"{ControlPlaneOptions.SectionName}:Kubernetes"));
builder.Services.AddResilientHttpClient(
    "import-source",
    "import-source",
    HttpResiliencePolicies.SlowServiceDefaults,
    configureHandler: static () => Honua.Server.Features.Import.ImportHttpClientHelper.CreatePinnedDnsHttpMessageHandler());
builder.Services.AddResilientHttpClient(
    "control-plane-telemetry",
    "control-plane-telemetry",
    HttpResiliencePolicies.FastApiDefaults);
builder.Services.AddResilientHttpClient(
    "control-plane-azure",
    "control-plane-azure",
    HttpResiliencePolicies.FastApiDefaults);
var kubernetesExecutionOptions = builder.Configuration
    .GetSection($"{ControlPlaneOptions.SectionName}:Kubernetes")
    .Get<KubernetesExecutionOptions>() ?? new KubernetesExecutionOptions();
var kubernetesCaBundlePath = kubernetesExecutionOptions.CaBundlePath;
var kubernetesInClusterAutoDetect = kubernetesExecutionOptions.InClusterAutoDetect;
builder.Services.AddResilientHttpClient(
    KubernetesJobClient.HttpClientName,
    "control-plane-kubernetes",
    HttpResiliencePolicies.FastApiDefaults,
    configureHandler: () => KubernetesJobClient.CreatePrimaryHandler(
        kubernetesInClusterAutoDetect,
        kubernetesCaBundlePath));
builder.Services.AddResilientHttpClient(
    AzureBatchDataPlaneClient.HttpClientName,
    "control-plane-azure-batch",
    HttpResiliencePolicies.FastApiDefaults);
// ---- Extracted: control-plane deploy + batch-compute backends (Startup/BatchAndDeployBackendsRegistration.cs)
builder.Services.AddHonuaBatchAndDeployBackends();
// ---- End extracted block

if (connectedRedis != null)
{
    builder.Services.AddSingleton<IWorkflowOperationStore, RedisWorkflowOperationStore>();
    builder.Services.AddSingleton<IWorkflowOperationReconciler, DeployWorkflowReconciler>();
    builder.Services.AddSingleton<IExecutionJobReconciler, ExecutionJobReconciler>();
    if (!isTestEnvironment)
    {
        builder.Services.AddHostedService<DeployWorkflowReconcilerBackgroundService>();
        builder.Services.AddHostedService<ExecutionJobReconcilerBackgroundService>();
    }
}

// Configure tile options
InfrastructureCompositionRoot.ConfigureTileOptions(builder.Services, builder.Configuration);

// Configure caching options and register cache services
InfrastructureCompositionRoot.ConfigureCaching(builder.Services, builder.Configuration, redisCacheEntitled);

// Configure cloud file storage for imports and attachments
builder.Services.AddCloudFileStorage(builder.Configuration);

// Configure file upload security limits
builder.Services.Configure<FileUploadSecurityOptions>(
    builder.Configuration.GetSection(FileUploadSecurityOptions.SectionName));

// Register configuration validators to ensure application fails fast on invalid configuration
StartupConfigurationHelpers.RegisterConfigurationValidators(builder.Services);

// Register health check services
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Monitoring.MigrationState>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Monitoring.DatabaseCompatibilityState>();
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Monitoring.IDeployPreflightProbe,
    Honua.Server.Features.Infrastructure.Monitoring.DeployPreflightProbe>();
builder.Services.AddScoped<Honua.Server.Features.HealthCheck.IReadinessCheckService,
    Honua.Server.Features.HealthCheck.ReadinessCheckService>();
builder.Services.AddProductionHealthChecks(builder.Configuration);

// ---- Extracted: licensing + identity-provider HTTP clients (Startup/LicensingRegistration.cs)
builder.Services.AddHonuaLicensing(builder.Configuration);
// ---- End extracted block

// Register configuration documentation service for self-documenting admin endpoint
builder.Services.AddScoped<Honua.Server.Features.Admin.Services.ConfigurationDocumentationService>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.TryAddScoped<IConsoleJobService, ConsoleJobService>();

// Register control plane IAM services (in-memory implementations until #496, #498, #355 land)
builder.Services.AddSingleton<Honua.Core.Features.Identity.Abstractions.IOidcProviderStore,
    Honua.Server.Features.Admin.Services.InMemoryOidcProviderStore>();
builder.Services.AddSingleton<Honua.Core.Features.Identity.Abstractions.IUserStore,
    Honua.Server.Features.Admin.Services.InMemoryUserStore>();
builder.Services.AddSingleton<Honua.Core.Features.Authorization.Abstractions.IRoleStore,
    Honua.Server.Features.Admin.Services.InMemoryRoleStore>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Authentication.IAdminApiKeyStore>(sp =>
    new Honua.Server.Features.Infrastructure.Authentication.InMemoryAdminApiKeyStore(sp.GetService<TimeProvider>()));
// v1 metadata-resource / manifest-approval / gitops-watch admin surface removed in #1035 cutover.
// V2 admin UX (epic #1046) edits the canonical MetadataV2Graph document directly via IMetadataV2GraphStore.

// Console metadata v2 content + RBAC baseline (#1162). Persistent store lands in #1163.
builder.Services.AddSingleton<Honua.Core.Features.Console.Abstractions.IConsoleContentStore>(sp =>
    new Honua.Server.Features.Console.Services.InMemoryConsoleContentStore(
        sp.GetService<TimeProvider>() ?? TimeProvider.System));
builder.Services.AddScoped<Honua.Core.Features.Console.Abstractions.IConsoleActionEvaluator,
    Honua.Server.Features.Console.Services.ConsoleActionEvaluator>();

// Register shared Infrastructure services
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Services.IGeometryConverter,
    Honua.Server.Features.Infrastructure.Services.GeometryConverter>();
builder.Services.AddScoped<ILayerStyleService, LayerStyleService>();
builder.Services.AddSingleton<Honua.Core.Features.Styling.Abstractions.ISldStyleConverter,
    Honua.Server.Features.Infrastructure.Styling.Sld.SldStyleConverter>();
builder.Services.AddStyleSuggestionCore();

// Configure temporary file service for image exports
builder.Services.Configure<Honua.Server.Features.Infrastructure.Services.TemporaryFileOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Services.TemporaryFileOptions.SectionName));
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Services.FileSystemTemporaryFileService>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Services.ITemporaryFileService,
    Honua.Server.Features.Infrastructure.Services.CloudBackedTemporaryFileService>();
builder.Services.AddHostedService<Honua.Server.Features.Infrastructure.Services.TemporaryFileCleanupService>();

// Register shared validation services
builder.Services.AddValidationServices();

// Register feature services (FeatureServer, OGC, OData, Observability)
builder.Services.AddServerFeatures(builder.Configuration);
builder.Services.AddOperateObservabilityFixtures(builder.Configuration, builder.Environment);
builder.Services.AddAdminRealtime();
if (!isTestEnvironment)
{
    builder.Services.AddOrchestrationBackgroundServices();
}

builder.Services.AddSingleton<Honua.Server.Features.Protocols.GeoServices.FeatureServer.DistributedReplicaStore>(sp =>
    new Honua.Server.Features.Protocols.GeoServices.FeatureServer.DistributedReplicaStore(
        sp.GetService<IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Protocols.GeoServices.FeatureServer.DistributedReplicaStore>>()));
// Replica/change-tracking services are provider-specific: Postgres registers concrete
// implementations; DuckDB and MySQL (both read-only) register no-op stubs via their own
// AddXxxServices extensions. Skip the Postgres registration for those providers so the
// stubs are not overwritten with an implementation that would issue Postgres SQL against
// a non-Postgres connection.
var replicaProvider = builder.Configuration.GetValue<string>("DataSource:Provider");
if (!string.Equals(replicaProvider, "duckdb", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(replicaProvider, DataProviderNames.MySql, StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(replicaProvider, "mariadb", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IReplicaRepository>(sp =>
        new Honua.Postgres.Features.FeatureStore.Services.PostgresReplicaRepository(
            sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider>()));
    builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IChangeTracker>(sp =>
        new Honua.Postgres.Features.FeatureStore.Services.PostgresChangeTracker(
            sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider>()));
}
builder.Services.AddScoped<Honua.Server.Features.Protocols.GeoServices.FeatureServer.IReplicaStore>(sp =>
    new Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services.CachingReplicaStore(
        sp.GetRequiredService<Honua.Server.Features.Protocols.GeoServices.FeatureServer.DistributedReplicaStore>(),
        sp.GetRequiredService<Honua.Core.Features.FeatureStore.Abstractions.IReplicaRepository>()));

// ---- Extracted: import/export job managers, migration evidence, tile operations
//      (Startup/ImportExportTileOperationsRegistration.cs)
builder.Services.AddHonuaImportExportAndTileOperations(builder.Configuration);
// ---- End extracted block


// ---- Extracted: feature-change events, streaming, transactional outbox
//      (Startup/FeatureEventsAndStreamingRegistration.cs)
builder.Services.AddHonuaFeatureEventsAndStreaming(builder.Configuration, requiresDurableDistributedEvents);
// ---- End extracted block
builder.Services.AddCollaborationSessionTransport();

// v1 manifest drift webhook dispatcher removed in #1035 cutover.

// Tile-operation job service + warming/background hosted services are registered by
// AddHonuaImportExportAndTileOperations above.

// Register OData services and handlers
// ApiKeyAuthenticationOptions (incl. admin password complexity, dev-auth bypass
// gating via HONUA_DEV_AUTH_ALLOW_BYPASS) are configured by
// AddHonuaAuthenticationOptions below.

// ---- Extracted: authentication & authorization options (Startup/AuthenticationOptionsRegistration.cs)
builder.Services.AddHonuaAuthenticationOptions(builder.Configuration, builder.Environment);
// ---- End extracted block
// Configure security headers
builder.Services.AddSecurityHeaders(builder.Configuration);
// Configure security audit log sink (#1144)
builder.Services.AddHonuaAuditLog();
// Configure tenant context resolution rail (#1144). Defaults are bound from
// the MultiTenancy configuration section; the inline callback is the wiring
// point for environment-specific overrides.
builder.Services.AddHonuaTenantContext(builder.Configuration, _ => { });
// Configure CORS policies
builder.Services.AddCorsPolicies(builder.Configuration, builder.Environment);
builder.Services.AddInputValidation(builder.Configuration);
// Rate limiting disabled per project requirements

// Configure API versioning for admin endpoints
builder.Services.AddApiVersioning(options =>
{
    // Use URL-based versioning to maintain current /api/v1/ URLs
    options.ApiVersionReader = Asp.Versioning.ApiVersionReader.Combine(
        new Asp.Versioning.UrlSegmentApiVersionReader());

    // Declare the currently published control-plane version. Unversioned admin routes remain unsupported.
    options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = false;

    // Keep versioning metadata explicit without publishing additional major paths.
});

// Configure JSON serialization for ASP.NET Core (needed for minimal API body binding)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models.FeatureServerJsonContext.Default,
        Honua.Server.Features.Protocols.GeoServices.ImageServer.Models.ImageServerJsonContext.Default,
        Honua.Server.Features.Protocols.OData.Models.ODataJsonContext.Default,
        Honua.Server.Features.Protocols.Ogc.Api.Coverages.Models.OgcCoveragesJsonContext.Default,
        Honua.Server.Features.Protocols.Ogc.Api.Features.OgcJsonContext.Default,
        Honua.Server.Features.Protocols.Ogc.Api.Maps.Models.OgcMapsJsonContext.Default,
        Honua.Server.Features.Protocols.Ogc.Api.Records.OgcRecordsJsonContext.Default,
        Honua.Server.Features.Protocols.Ogc.Api.Tiles.OgcTilesJsonContext.Default,
        Honua.Server.Features.Admin.Models.SecureConnectionJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerPublishingJsonContext.Default,
        Honua.Server.Features.Admin.Models.ServiceSettingsJsonContext.Default,
        Honua.Core.Features.Metadata.Domain.V2.MetadataReleaseJsonContext.Default,
        Honua.Server.Features.Admin.Models.MetadataPrevalidationJsonContext.Default,
        Honua.Server.Features.Admin.Models.DeployControlJsonContext.Default,
        Honua.Server.Features.Infrastructure.Monitoring.MetricsJsonContext.Default,
        Honua.Server.Features.Import.ImportJsonContext.Default,
        Honua.Server.Features.Import.RasterImportJsonContext.Default,
        Honua.Server.Features.Import.GeoservicesImportApiJsonContext.Default,
        Honua.Server.Features.Import.OgcWfsImportJsonContext.Default,
        Honua.Server.Features.Import.OgcCoverageImportJsonContext.Default,
        Honua.Server.Features.Import.OgcWcsImportJsonContext.Default,
        Honua.Server.Features.Admin.OperationsProgressJsonContext.Default,
        Honua.Server.Features.Admin.FeatureEventReplayJsonContext.Default,
        Honua.Server.Features.Mobile.Auth.MobileAuthJsonContext.Default,
        Honua.Server.Features.Mobile.Diagnostics.MobileExceptionIngestionJsonContext.Default,
        Honua.Server.Features.Mobile.FieldCollection.FieldCollectionSyncJsonContext.Default,
        Honua.Server.Features.Admin.TileOperations.TileOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerStyleJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerFieldConfigurationJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerValidationJsonContext.Default,
        Honua.Server.Features.Admin.Models.StyleSuggestionJsonContext.Default,
        Honua.Server.Features.Admin.Models.AlertAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.LicenseJsonContext.Default,
        Honua.Server.Features.Admin.Models.OidcProviderJsonContext.Default,
        Honua.Server.Features.Admin.Models.UserManagementJsonContext.Default,
        Honua.Server.Features.Admin.Models.RoleJsonContext.Default,
        Honua.Server.Features.Console.Models.ConsoleJsonContext.Default,
        Honua.Server.Features.Studio.Models.StudioApiJsonContext.Default,
        Honua.Core.Features.Studio.Domain.StudioJsonContext.Default,
        Honua.Server.Features.AnalysisContent.AnalysisContentApiJsonContext.Default,
        Honua.Server.Features.Capabilities.Models.CapabilityManifestJsonContext.Default,
        Honua.Server.Features.Admin.Models.AdminApiKeyJsonContext.Default,
        Honua.Server.Features.Admin.Models.SceneDatasetJsonContext.Default,
        Honua.Server.Features.Admin.Models.SceneGenerationJsonContext.Default,
        Honua.Server.Features.Protocols.Scene.Models.PublicSceneDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.RateLimitJsonContext.Default,
        Honua.Server.Features.Admin.Models.TableDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.ExternalServiceDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.AdminAuthJsonContext.Default,
        Honua.Server.Features.Admin.Models.ClientCertificateJsonContext.Default,
        Honua.Server.Features.Admin.Models.ConfigurationJsonContext.Default,
        Honua.Server.Features.Admin.Models.LicenseAdminJsonContext.Default,
        Honua.Server.Features.Infrastructure.Licensing.LicenseFileJsonContext.Default,
        Honua.Server.Features.Admin.Models.IdentityAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.CacheAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.GeocodingAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.FeatureOverviewJsonContext.Default,
        Honua.Server.Features.Admin.Models.CacheOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.StreamingOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.GeocodingOperationsJsonContext.Default,
        Honua.Server.Features.PackageReview.PackageReviewJsonContext.Default,
        Honua.Server.Features.CloudDemo.CloudDemoJsonContext.Default,
        Honua.Server.Features.HealthCheck.HealthJsonContext.Default,
        Honua.Server.Features.Infrastructure.Models.ProblemJsonContext.Default,
        Honua.Server.Features.Infrastructure.Authentication.ClientCertificates.ClientCertificateInfrastructureJsonContext.Default,
        Honua.Server.Features.Infrastructure.Middleware.LimitsEnforcementJsonContext.Default,
        Honua.Server.Features.Infrastructure.Security.CspViolationJsonContext.Default,
        Honua.Server.Features.Protocols.GeoServices.GeometryService.Models.GeometryServiceJsonContext.Default,
        Honua.Server.Features.Protocols.GeoServices.NAServer.Models.NAServerJsonContext.Default,
        Honua.Server.Features.Export.ExportJsonContext.Default,
        Honua.Server.Features.Protocols.Stac.StacJsonContext.Default,
        Honua.Server.Features.Protocols.Cog.CogJsonContext.Default,
        Honua.Server.Features.Protocols.Coverages.Multidimensional.MultidimensionalCoverageJsonContext.Default,
        Honua.Server.Features.Protocols.Zarr.ZarrJsonContext.Default,
        Honua.Server.Features.Protocols.SpatialAnalytics.Models.SpatialAnalyticsJsonContext.Default,
        Honua.Server.Features.Collaboration.Sessions.CollaborationSessionJsonContext.Default,
        Honua.Server.Features.Collaboration.FeatureLocks.FeatureLockJsonContext.Default,
        Honua.Core.Features.Authorization.Domain.OperatorAuthorizationJsonContext.Default,
        Honua.Server.Features.Admin.ObservabilityJsonContext.Default,
        Honua.Server.Features.Admin.InvestigationJsonContext.Default,
        Honua.Server.Features.Protocols.Ogc.Api.Processes.OgcProcessesJsonContext.Default);
});

// Add comprehensive IOptions configuration validation
builder.Services.AddConfigurationOptionsValidation();

var app = builder.Build();

HostedBlazorAssetHelpers.FilterHostedBlazorStaticAssetEndpoints(
    app,
    allowStacOpsDemoAssets: serveStacOpsDemo);

var activeDbConnectionTracker = app.Services.GetService<IActiveDbConnectionTracker>();
if (activeDbConnectionTracker != null)
{
    EnhancedTelemetry.ConfigureActiveDbConnectionsProvider(activeDbConnectionTracker.GetActiveCount);
}

if (forwardedHeadersEnabled)
{
    app.Use(async (context, next) =>
    {
        context.Items[ClientCertificateHttpContextItems.OriginalProxyPeerIpAddress] =
            context.Connection.RemoteIpAddress;
        await next(context).ConfigureAwait(false);
    });
    app.UseForwardedHeaders();
}

app.UseHostValidation();

// Add HTTPS redirection middleware to enforce HTTPS for all requests
// This ensures API keys and sensitive data are never transmitted over HTTP
// Enable HTTPS redirection in all environments except when explicitly disabled
var disableHttpsRedirection = builder.Configuration.GetValue<bool>("Security:DisableHttpsRedirection");
if (!disableHttpsRedirection)
{
    app.UseHttpsRedirection();
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
    isDevelopment: app.Environment.IsDevelopment(),
    isTest: app.Environment.IsEnvironment("Test"));

if (configurationErrors.Count > 0)
{
    var errorDetails = string.Join(Environment.NewLine, configurationErrors);
    ConfigurationLog.ConfigurationValidationFailed(app.Logger, configurationErrors.Count, errorDetails);
    throw new InvalidOperationException("Configuration validation failed. See logs for details.");
}

ConfigurationLog.ConfigurationValidationSucceeded(app.Logger);

// SECURITY: Surface the development auth bypass state at startup so operators
// reviewing logs immediately notice if admin endpoints are unauthenticated.
{
    var apiKeyOptions = app.Services
        .GetRequiredService<IOptions<Honua.Server.Features.Infrastructure.Authentication.ApiKeyAuthenticationOptions>>()
        .Value;
    var devAuthRequested = string.Equals(apiKeyOptions.DevAuthBypass, "true", StringComparison.OrdinalIgnoreCase);
    var devAuthAcknowledged = string.Equals(apiKeyOptions.DevAuthBypassAcknowledged, "true", StringComparison.OrdinalIgnoreCase);
    var environmentName = app.Environment.EnvironmentName;
    var bypassActive =
        devAuthRequested &&
        devAuthAcknowledged &&
        apiKeyOptions.IsTestMode &&
        string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase);

    if (bypassActive)
    {
        Honua.Server.Features.Infrastructure.Authentication.AuthenticationLog
            .DevelopmentBypassActiveAtStartup(app.Logger, environmentName);
    }
    else if (devAuthRequested)
    {
        Honua.Server.Features.Infrastructure.Authentication.AuthenticationLog
            .DevelopmentBypassRequestedButRejected(app.Logger, environmentName);
    }
}

// Log OIDC configuration state for observability
{
    var oidcOpts = app.Services.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
    if (oidcOpts.Enabled)
    {
        OidcAuthenticationLog.OidcConfigurationLoaded(
            app.Logger,
            oidcOpts.AzureAd?.IsValid == true,
            oidcOpts.Google?.IsValid == true,
            oidcOpts.Generic?.IsValid == true,
            oidcOpts.Okta?.IsValid == true,
            oidcOpts.Auth0?.IsValid == true);
    }
}

// Add security headers middleware (first in pipeline for all requests)
app.UseSecurityHeaders();

// Some standards validators and older HTTP clients send only `Accept-Encoding: *`.
// Prefer gzip/deflate for that wildcard so the response remains broadly decodable.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.AcceptEncoding.Count == 1 &&
        string.Equals(context.Request.Headers.AcceptEncoding[0], "*", StringComparison.Ordinal))
    {
        context.Request.Headers.AcceptEncoding = "gzip, deflate";
    }

    await next(context);
});

// Add response compression middleware (early in pipeline)
app.UseResponseCompression();
app.UseWebSockets();

// The admin web UI lives in the sibling `honua-server-admin` repo and is deployed
// as a standalone Blazor WebAssembly app. This server only exposes the backing
// `/api/v1/admin/*` REST + gRPC surface; the `/admin` static-asset prefix is no
// longer served in-process.

if (serveStacOpsDemo)
{
    HostedBlazorAssetHelpers.ConfigureHostedBlazorAssets(app, stacOpsDemoPathPrefix);
    app.MapGet("/samples/stac-ops", () => Results.Redirect("/samples/stac-ops/index.html"))
        .ExcludeFromDescription();
    HostedBlazorAssetHelpers.MapHostedBlazorFallback(app, stacOpsDemoPathPrefix);
}
else
{
    HostedBlazorAssetHelpers.MapDisabledHostedBlazorPrefix(app, stacOpsDemoPathPrefix);
}

// Map interactive API explorer (Scalar) at /docs when enabled
if (serveApiDocs)
{
    app.MapScalarApiReference("/docs", options =>
    {
        options
            .WithTitle("Honua API Explorer")
            .WithTheme(ScalarTheme.BluePlanet)
            .AddDocument("features", "OGC API Features", "/openapi.json", isDefault: true)
            .AddDocument("coverages", "OGC API Coverages", "/ogc/coverages/openapi.json")
            .AddDocument("tiles", "OGC API Tiles", "/ogc/tiles/openapi.json")
            .AddDocument("maps", "OGC API Maps", "/ogc/maps/openapi.json")
            .AddDocument("processes", "OGC API Processes", "/ogc/processes/openapi.json")
            .AddDocument("admin", "Admin API", "/api/v1/admin/openapi.json");
    });
}

// Add correlation ID middleware early in pipeline (before request logging)
app.UseCorrelationId();

if (useTestSchemaHeaders && (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Test")))
{
    app.UseTestSchemaHeaders();
}

// Add performance monitoring middleware (tracks request duration and memory)
app.UsePerformanceMonitoring();

// Configure Serilog request logging before short-circuiting middleware so every request is observable.
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        diagnosticContext.Set("Protocol", httpContext.Request.Protocol);
        diagnosticContext.Set("HonuaProtocol", RequestTelemetryClassifier.ResolveProtocol(httpContext.Request.Path) ?? "unknown");
        diagnosticContext.Set("HonuaOperation", RequestTelemetryClassifier.ResolveOperation(httpContext) ?? "unknown");

        if (httpContext.Request.RouteValues.TryGetValue("serviceId", out var serviceId) && serviceId != null)
        {
            diagnosticContext.Set("ServiceId", serviceId.ToString()!);
        }
        else if (httpContext.Request.RouteValues.TryGetValue("id", out var id) && id != null)
        {
            diagnosticContext.Set("ServiceId", id.ToString()!);
        }

        if (httpContext.Request.RouteValues.TryGetValue("layerId", out var layerId) && layerId != null)
        {
            diagnosticContext.Set("LayerId", layerId.ToString()!);
        }

        if (httpContext.Request.RouteValues.TryGetValue("taskName", out var taskName) && taskName != null)
        {
            diagnosticContext.Set("TaskName", taskName.ToString()!);
        }

        if (httpContext.Request.RouteValues.TryGetValue("jobId", out var jobId) && jobId != null)
        {
            diagnosticContext.Set("JobId", jobId.ToString()!);
        }

        if (httpContext.Request.RouteValues.TryGetValue("paramName", out var paramName) && paramName != null)
        {
            diagnosticContext.Set("ParamName", paramName.ToString()!);
        }

        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            diagnosticContext.Set("AuthenticatedUser", true);
            diagnosticContext.Set("AuthenticationType", httpContext.User.Identity.AuthenticationType ?? "unknown");
        }
    };

    // Exclude health check endpoints from request logging (configured in appsettings.json)
    options.GetLevel = (httpContext, elapsed, ex) => ex != null
        ? Serilog.Events.LogEventLevel.Error
        : httpContext.Request.Path.StartsWithSegments("/healthz")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
});

// Add global exception handling middleware after request logging.
app.UseGlobalExceptionHandling();

// Capture the original gRPC-Web indicator before UseGrpcWeb rewrites Content-Type
// from application/grpc-web* to application/grpc, so the client-certificate
// enforcement middleware downstream can still distinguish gRPC-Web from native gRPC
// and skip mTLS enforcement for browser/gRPC-Web callers. Only Content-Type is
// authoritative here — it matches what UseGrpcWeb itself uses for protocol
// detection, and treating a client-supplied X-Grpc-Web header as sufficient
// would let an unauthenticated caller bypass required native mTLS by setting a
// single header alongside application/grpc.
app.Use(async (context, next) =>
{
    var contentType = context.Request.ContentType;
    var isGrpcWeb = !string.IsNullOrWhiteSpace(contentType) &&
        contentType.StartsWith("application/grpc-web", StringComparison.OrdinalIgnoreCase);
    context.Items[ClientCertificateHttpContextItems.OriginalGrpcWebRequest] = isGrpcWeb;
    await next(context).ConfigureAwait(false);
});

// Enable gRPC-Web for all gRPC services (before CORS and endpoint mapping)
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

// Add CORS middleware before auth to handle preflight requests
app.UseHonuaCors(app.Environment);

// Validate query, form, and selected header inputs before authentication and endpoint execution.
app.UseInputValidation();

// Validate optional/required client certificates before the regular auth stack so
// required mTLS surfaces can return machine-readable errors instead of TLS handshakes.
app.UseHonuaClientCertificateAuthentication();

// Add authentication and authorization middleware early to short-circuit unauthorized requests
app.UseApiKeyAuthentication();

// Resolve tenant context immediately after authentication so claims (and the
// X-Honua-Tenant override header) are evaluated against the resolved principal
// before any downstream feature handler reads ITenantContext (#1144).
app.UseHonuaTenantContext();

// Audit-log middleware records security-relevant request outcomes. It runs after
// auth so the audit actor is the authenticated principal, and before endpoint
// execution so 401/403/5xx responses are still observed (#1144).
app.UseHonuaAuditLog();

// Rate limiting disabled per project requirements

// Add limits enforcement middleware (after auth, before request logging)
app.UseLimitsEnforcement();

// Map public demo service/layer contract IDs to internal seeded layer IDs and guard demo writes.
app.UseCloudDemoServiceLayerAliases();
app.UseCloudDemoWritableFeatureGuard();

// Enable output caching middleware
app.UseOutputCache();

// Log application startup
var appVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
Honua.Server.Features.Infrastructure.Logging.Log.ApplicationStarting(app.Logger,
    appVersion,
    app.Environment.EnvironmentName);

// Run PostGIS preflight compatibility check (before migrations — migration scripts
// create GEOMETRY columns and GiST indexes that require PostGIS to be installed)
await RunPostGisPreflightCheckAsync();

// Run database migrations on startup
await RunDatabaseMigrationsAsync();

// Configure health endpoints
app.MapHealthEndpoints();
app.MapPrometheusEndpoint();

// Configure admin auth bootstrap endpoint (anonymous - must precede admin group)
app.MapAdminAuthEndpoints();
app.MapMobileAuthEndpoints();

// Configure admin endpoints
app.MapAdminEndpoints();
app.MapExternalServiceDiscoveryEndpoints();
app.MapConfigurationDiscoveryEndpoints();
app.MapAdminObservabilityEndpoints();
app.MapConsoleJobEndpoints();
app.MapAdminRealtimeHub();

// Configure layer publishing endpoints
app.MapLayerPublishingEndpoints();

// Configure service settings endpoints (protocol toggles + MapServer config)
app.MapServiceSettingsEndpoints();

// Configure admin metadata version/manifest endpoints
// v1 admin endpoint mappings removed in #1035 cutover; V2 admin UX (#1046) lives elsewhere.
app.MapMetadataReleaseEndpoints();
app.MapMetadataPrevalidationEndpoints();
app.MapDeployControlEndpoints();

// Configure admin layer style endpoints
app.MapAdminLayerStyleEndpoints();
app.MapAdminLayerFieldConfigurationEndpoints();
app.MapAdminLayerFilterConfigurationEndpoints();
app.MapAdminLayerValidationEndpoints();
app.MapAdminStyleSuggestionEndpoints();
app.MapAdminSldStyleEndpoints();

// Configure admin alerting zone/rule endpoints
app.MapAlertAdminEndpoints();

// Configure Console Operate observability endpoints (#1168)
app.MapObservabilityAlertEndpoints();
app.MapObservabilityAuditEndpoints();
app.MapObservabilityEventEndpoints();
app.MapInvestigationEndpoints();
app.MapOperateObservabilityFixtureEndpoints();

// Configure platform admin endpoints (license, identity, cache, geocoding, features)
app.MapLicenseAdminEndpoints();
app.MapIdentityAdminEndpoints();
app.MapAdminInfoEndpoints();
app.MapCacheAdminEndpoints();
app.MapGeocodingAdminEndpoints();
app.MapFeatureOverviewEndpoints();

// Configure compliance admin endpoints (SOC 2 / FedRAMP readiness, key rotation, report export) (#352)
app.MapComplianceAdminEndpoints();

// Configure secure connection management endpoints
app.MapSecureConnectionEndpoints();
app.MapClientCertificateAdminEndpoints();

// Configure control plane IAM endpoints (#511)
app.MapLicenseEndpoints();
app.MapOidcProviderEndpoints();
app.MapUserManagementEndpoints();
app.MapRoleEndpoints();

// Configure Console metadata v2 content + RBAC endpoints (#1162)
app.MapConsoleSessionEndpoints();
app.MapConsoleContentEndpoints();
app.MapConsoleActionEndpoints();
app.MapStudioPackageEndpoints();
app.MapAdminApiKeyEndpoints();
app.MapPackageReviewEndpoints();

// Configure metadata resource endpoints (ADR-0023)
// v1 MapMetadataResourceEndpoints removed in #1035 cutover.

// Configure operational monitoring endpoints (#512)
app.MapCacheOperationsEndpoints();
app.MapStreamingOperationsEndpoints();
app.MapGeocodingOperationsEndpoints();

// Configure feature-change streaming transport (#501)
app.MapFeatureStreamEndpoints();

// Configure security endpoints (CSP violation reporting)
app.MapCspViolationReportEndpoint();

// Configure FeatureServer, OGC, and OData endpoints
app.MapServerFeatureEndpoints();

// Configure gRPC feature service endpoint (gRPC-Web enabled via middleware)
app.MapGrpcService<Honua.Server.Features.Protocols.Grpc.HonuaFeatureService>();
app.MapGrpcService<Honua.Server.Features.Geoprocessing.HonuaProcessService>();
app.MapGrpcService<Honua.Server.Features.Spec.HonuaSpecService>();
app.MapGrpcHealthChecksService();

// Enable gRPC reflection for dev tooling (grpcurl, grpcui, Postman)
if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

// Configure file import endpoints
app.MapImportEndpoints();
app.MapMigrationScannerEndpoints();
app.MapArcGisMigrationEvidenceEndpoints();
app.MapMigrationPerformanceEvidenceEndpoints();
app.MapRasterImportEndpoints();

// Configure Geoservices service import endpoints
app.MapGeoservicesImportEndpoints();

// Configure GeoServer import endpoints
app.MapGeoServerImportEndpoints();

// Configure OGC API Features collection import endpoints (#1029 slice 2)
app.MapOgcApiFeaturesImportEndpoints();
// Configure OGC WFS data import endpoints (#1016 slice 2)
app.MapOgcWfsImportEndpoints();
// Configure OGC WCS / OGC API Coverages GeoTIFF/COG import endpoints (issue #1030 slice 2)
app.MapOgcCoverageImportEndpoints();

// Configure legacy OGC WCS coverage import endpoints (issue #1030 slice 3)
app.MapOgcWcsImportEndpoints();

// Configure GeoServer migration run admin orchestration endpoints (issue #1015 slice 5)
app.MapMigrationRunAdminEndpoints();

// Configure OGC WMTS tile-cache export endpoints (#1016 slice 4)
app.MapOgcTileCacheExportEndpoints();

if (isTestEnvironment)
{
    app.MapCrossServerConsumeProbeEndpoints();
}

// Configure temporary file serving endpoints
app.MapTemporaryFileEndpoints();

// Configure data export endpoints
app.MapExportEndpoints();

// Configure unified operations progress endpoints
app.MapOperationsProgressEndpoints();
app.MapFeatureChangeEventsEndpoints();
app.MapCollaborationEndpoints();
app.MapMobileExceptionIngestionEndpoints();
app.MapFieldCollectionSyncEndpoints();
app.MapTileOperationsEndpoints();

// Map health endpoints for Aspire dashboard (only when Aspire is enabled)
if (useAspire)
{
    app.MapDefaultEndpoints();
}

// Map metrics endpoints for monitoring APIs
app.MapMetricsEndpoints();
app.MapDatabasePerformanceEndpoints();
app.MapProductionMonitoringEndpoints();

// Map enhanced performance monitoring endpoints
app.MapEnhancedPerformanceEndpoints();

app.Run();

// Composition Root: RegisterInfrastructureServices, ConfigureLimits, ConfigureTileOptions,
// ConfigureCaching now live in Startup/InfrastructureCompositionRoot.cs.
// ConfigureForwardedHeaders, ResolveEnvironmentSecretReferences, IsRedisCacheEntitledAsync,
// AddSecurityConfiguration, RegisterConfigurationValidators now live in
// Startup/StartupConfigurationHelpers.cs. The hosted-Blazor static-asset helpers + filter
// data source live in Startup/HostedBlazorAssetHelpers.cs.

// Database migration helper
async Task RunDatabaseMigrationsAsync()
{
    var migrationState = app.Services.GetRequiredService<Honua.Server.Features.Infrastructure.Monitoring.MigrationState>();

    if (builder.Configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS"))
    {
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsSkipped(app.Logger);
        migrationState.MarkSkipped("Migrations skipped by configuration.");
        return;
    }

    // Resolve secret-backed connection strings (aws:secretsmanager:*, env:*, etc.)
    var secretResolver = app.Services.GetService<Honua.Core.Features.Security.Abstractions.IConnectionSecretResolver>();
    var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
        builder.Configuration, secretResolver, app.Lifetime.ApplicationStopping);
    if (string.IsNullOrEmpty(connectionString))
    {
        // Skip migrations if no connection string is configured
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseConnectionStringNotConfigured(app.Logger);

        if (app.Environment.IsProduction())
        {
            Honua.Server.Features.Infrastructure.Logging.Log.DatabaseConnectionStringMissingInProduction(app.Logger);
        }

        migrationState.MarkSkipped("No database connection string configured.");
        return;
    }

    Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsStarting(app.Logger);
    migrationState.MarkRunning("Applying database migrations.");

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
            migrationState.MarkFailed("Database migrations failed.");

            // In non-Development environments, re-throw so the app fails to start
            // (gives a clear CrashLoopBackOff signal in Kubernetes).
            if (!app.Environment.IsDevelopment())
            {
                throw error;
            }

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

            migrationState.MarkSucceeded($"Applied {scriptCount} migration script(s).");
        }
        else
        {
            Honua.Server.Features.Infrastructure.Logging.Log.NoDatabaseMigrationsToApply(app.Logger);
            migrationState.MarkSucceeded("No pending migration scripts.");
        }
    }
    catch (Exception ex)
    {
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, ex.Message, ex);
        migrationState.MarkFailed("Database migrations failed.");

        // In non-Development environments, re-throw so the app fails to start
        // (gives a clear CrashLoopBackOff signal in Kubernetes).
        if (!app.Environment.IsDevelopment())
        {
            throw;
        }
    }
}

// PostGIS preflight compatibility check
async Task RunPostGisPreflightCheckAsync()
{
    var compatibilityState = app.Services.GetRequiredService<Honua.Server.Features.Infrastructure.Monitoring.DatabaseCompatibilityState>();

    // Resolve secret-backed connection strings (aws:secretsmanager:*, env:*, etc.)
    var secretResolver = app.Services.GetService<Honua.Core.Features.Security.Abstractions.IConnectionSecretResolver>();
    var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
        builder.Configuration, secretResolver, app.Lifetime.ApplicationStopping);
    if (string.IsNullOrEmpty(connectionString))
    {
        // No connection string configured — skip preflight (migrations already handle this case)
        return;
    }

    var checker = app.Services.GetService<IDatabaseCompatibilityChecker>();
    if (checker is null)
    {
        Honua.Server.Features.Infrastructure.Logging.Log.PostGisPreflightCheckSkipped(app.Logger);
        return;
    }

    Honua.Server.Features.Infrastructure.Logging.Log.PostGisPreflightCheckStarting(app.Logger);

    var result = await checker.CheckCompatibilityAsync(connectionString, app.Lifetime.ApplicationStopping);
    compatibilityState.SetResult(result);

    if (result.IsCompatible)
    {
        Honua.Server.Features.Infrastructure.Logging.Log.PostGisPreflightCheckPassed(
            app.Logger, result.EngineVersion, result.PostGisVersion ?? "unknown");
        return;
    }

    var errorMessage = result.ErrorMessage ?? "Database compatibility check failed.";

    if (!app.Environment.IsDevelopment())
    {
        Honua.Server.Features.Infrastructure.Logging.Log.PostGisPreflightCheckFailedCritical(app.Logger, errorMessage);
        throw new InvalidOperationException($"PostGIS preflight check failed: {errorMessage}");
    }

    Honua.Server.Features.Infrastructure.Logging.Log.PostGisPreflightCheckFailedDevelopment(app.Logger, errorMessage);
}
