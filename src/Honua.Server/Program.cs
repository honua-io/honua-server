// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
// ✅ DEPENDENCY INVERSION: Server uses Core abstractions only
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Schema;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Export;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.FileStorage;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Extensions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Scalar.AspNetCore;
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
var serveAdminUi = builder.Configuration.GetValue(
    "ServeAdminUI",
    builder.Configuration.GetValue("HONUA_SERVE_ADMIN_UI", true));
var serveApiDocs = builder.Configuration.GetValue(
    "ServeApiDocs",
    builder.Configuration.GetValue("HONUA_SERVE_API_DOCS", builder.Environment.IsDevelopment()));
var adminStaticAssetsManifestPath = Path.Combine(
    AppContext.BaseDirectory,
    "Honua.Admin.staticwebassets.endpoints.json");
if (serveAdminUi && !builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// Load optional security configuration without overriding environment-specific settings.
AddSecurityConfiguration(builder.Configuration, builder.Environment);

// Enable Aspire integrations only when Aspire configuration is present.
var useAspire = builder.Configuration.GetSection("Aspire").Exists();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? builder.Configuration["Aspire:StackExchange:Redis:ConnectionString"];
ConnectionMultiplexer? connectedRedis = null;

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
        var cacheKeyPrefix = builder.Configuration.GetSection("Cache")["KeyPrefix"] ?? "honua:";
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = cacheKeyPrefix;
        });
    }
}

if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    try
    {
        var redisOptions = ConfigurationOptions.Parse(redisConnectionString, ignoreUnknown: true);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = Math.Max(redisOptions.ConnectRetry, 3);
        redisOptions.ReconnectRetryPolicy ??= new ExponentialRetry(5_000);

        connectedRedis = ConnectionMultiplexer.Connect(redisOptions);
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(connectedRedis);

        if (!connectedRedis.IsConnected)
        {
            var startupLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Program>();
            startupLogger.LogWarning(
                "Redis multiplexer initialized without an active connection. The client will continue retrying in the background.");
        }
    }
    catch (Exception ex)
    {
        var startupLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Program>();
        startupLogger.LogWarning(ex, "Failed to connect to Redis at startup. RedisCacheService will operate in fallback mode.");
        // Do not register IConnectionMultiplexer — services that request it via GetService<> will receive null
    }
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
builder.Services.AddSingleton<IValidateOptions<ControlPlaneOptions>, ControlPlaneOptionsValidator>();
builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection(ControlPlaneOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHttpClient("import-source")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddHttpClient("control-plane-telemetry");
builder.Services.AddHttpClient("control-plane-azure");
builder.Services.AddSingleton<IAwsLambdaAliasClient, AwsSdkLambdaAliasClient>();
builder.Services.AddSingleton<IAzureFunctionsSlotClient, AzureManagementFunctionsSlotClient>();
builder.Services.AddSingleton<IAzureContainerAppsRevisionClient, AzureManagementContainerAppsRevisionClient>();
builder.Services.AddSingleton<IDeployTargetRegistry, ConfigurationDeployTargetRegistry>();
builder.Services.AddSingleton<IExecutionJobDefinitionRegistry, ConfigurationExecutionJobDefinitionRegistry>();
builder.Services.AddSingleton<DeployWorkflowService>();
builder.Services.AddSingleton<IDeployTelemetrySignalEvaluator, PrometheusDeployTelemetrySignalEvaluator>();
builder.Services.AddSingleton<KubernetesGitOpsDeployBackend>();
builder.Services.AddSingleton<AwsEcsGitOpsDeployBackend>();
builder.Services.AddSingleton<AzureContainerAppsGitOpsDeployBackend>();
builder.Services.AddSingleton<AzureContainerAppsRevisionDeployBackend>();
builder.Services.AddSingleton<AwsLambdaGitOpsDeployBackend>();
builder.Services.AddSingleton<AzureFunctionsGitOpsDeployBackend>();
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<KubernetesGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsEcsGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsRevisionDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsLambdaGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureFunctionsGitOpsDeployBackend>());
if (connectedRedis != null)
{
    builder.Services.AddSingleton<IWorkflowOperationStore, RedisWorkflowOperationStore>();
    builder.Services.AddSingleton<IOperationReconciler, DeployWorkflowReconciler>();
    if (!isTestEnvironment)
    {
        builder.Services.AddHostedService<DeployWorkflowReconcilerBackgroundService>();
    }
}

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
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Monitoring.MigrationState>();
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Monitoring.IDeployPreflightProbe,
    Honua.Server.Features.Infrastructure.Monitoring.DeployPreflightProbe>();
builder.Services.AddScoped<Honua.Server.Features.HealthCheck.IReadinessCheckService,
    Honua.Server.Features.HealthCheck.ReadinessCheckService>();

// Register license status provider (reads edition from AlertOptions until #338)
builder.Services.AddSingleton<Honua.Core.Features.Licensing.Abstractions.ILicenseStatusProvider,
    Honua.Server.Features.Admin.ConfigurationLicenseStatusProvider>();

// Register named HTTP client for identity provider connectivity tests
builder.Services.AddHttpClient("IdentityProviderTest");

// Register configuration documentation service for self-documenting admin endpoint
builder.Services.AddScoped<Honua.Server.Features.Admin.Services.ConfigurationDocumentationService>();

// Register control plane IAM services (in-memory implementations until #338, #496, #498, #355 land)
builder.Services.AddSingleton<Honua.Core.Features.Licensing.Abstractions.ILicenseManager,
    Honua.Server.Features.Admin.Services.InMemoryLicenseManager>();
builder.Services.AddSingleton<Honua.Core.Features.Identity.Abstractions.IOidcProviderStore,
    Honua.Server.Features.Admin.Services.InMemoryOidcProviderStore>();
builder.Services.AddSingleton<Honua.Core.Features.Identity.Abstractions.IUserStore,
    Honua.Server.Features.Admin.Services.InMemoryUserStore>();
builder.Services.AddSingleton<Honua.Core.Features.Authorization.Abstractions.IRoleStore,
    Honua.Server.Features.Admin.Services.InMemoryRoleStore>();
builder.Services.AddSingleton<Honua.Core.Features.RateLimiting.Abstractions.IRateLimitPolicyStore,
    Honua.Server.Features.Admin.Services.InMemoryRateLimitPolicyStore>();
builder.Services.AddSingleton<IMetadataSchemaRegistry, MetadataSchemaRegistry>();
builder.Services.AddSingleton<IMetadataCompiler, DefaultMetadataCompiler>();

// Register manifest approval workflow services
builder.Services.Configure<Honua.Server.Features.Admin.Models.ManifestApprovalOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Admin.Models.ManifestApprovalOptions.SectionName));
builder.Services.Configure<Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptions>,
    Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptionsValidator>();
builder.Services.AddHttpClient("manifest-approval-webhook");
builder.Services.AddSingleton<Honua.Server.Features.Admin.ManifestApprovalWebhookDispatcher>(sp =>
    new Honua.Server.Features.Admin.ManifestApprovalWebhookDispatcher(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptions>>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Admin.ManifestApprovalWebhookDispatcher>>()));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Honua.Server.Features.Admin.ManifestApprovalWebhookDispatcher>());
builder.Services.AddHostedService(sp =>
    new Honua.Server.Features.Admin.ManifestApprovalExpiryService(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<IOptions<Honua.Server.Features.Admin.Models.ManifestApprovalOptions>>(),
        sp.GetService<Honua.Server.Features.Admin.ManifestApprovalWebhookDispatcher>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Admin.ManifestApprovalExpiryService>>()));
builder.Services.AddScoped<Honua.Server.Features.Admin.ManifestApprovalGate>();

// Register shared Infrastructure services
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Services.IGeometryConverter,
    Honua.Server.Features.Infrastructure.Services.GeometryConverter>();
builder.Services.AddScoped<ILayerStyleService, LayerStyleService>();

// Configure temporary file service for image exports
builder.Services.Configure<Honua.Server.Features.Infrastructure.Services.TemporaryFileOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Services.TemporaryFileOptions.SectionName));
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Services.ITemporaryFileService,
    Honua.Server.Features.Infrastructure.Services.FileSystemTemporaryFileService>();
builder.Services.AddHostedService<Honua.Server.Features.Infrastructure.Services.TemporaryFileCleanupService>();

// Register shared validation services
builder.Services.AddValidationServices();

// Register feature services (FeatureServer, OGC, OData, Observability)
builder.Services.AddServerFeatures(builder.Configuration);
builder.Services.AddSingleton<Honua.Server.Features.FeatureServer.DistributedReplicaStore>(sp =>
    new Honua.Server.Features.FeatureServer.DistributedReplicaStore(
        sp.GetService<IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.FeatureServer.DistributedReplicaStore>>()));
builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IReplicaRepository>(sp =>
    new Honua.Postgres.Features.FeatureStore.Services.PostgresReplicaRepository(
        sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider>()));
builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IChangeTracker>(sp =>
    new Honua.Postgres.Features.FeatureStore.Services.PostgresChangeTracker(
        sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider>()));
builder.Services.AddScoped<Honua.Server.Features.FeatureServer.IReplicaStore>(sp =>
    new Honua.Server.Features.FeatureServer.Services.CachingReplicaStore(
        sp.GetRequiredService<Honua.Server.Features.FeatureServer.DistributedReplicaStore>(),
        sp.GetRequiredService<Honua.Core.Features.FeatureStore.Abstractions.IReplicaRepository>()));

// Register Geoservices import job manager and background service
builder.Services.AddSingleton<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(sp =>
    new Honua.Server.Features.Import.UniversalProgressStore(
        sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Import.UniversalProgressStore>>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddSingleton<Honua.Core.Features.Import.Abstractions.IDistributedImportJobManager>(sp =>
    new Honua.Server.Features.Import.RedisImportJobManager(
        sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(),
        sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Import.RedisImportJobManager>>(),
        sp.GetRequiredService<IHostEnvironment>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddHostedService<Honua.Server.Features.Import.GeoservicesImportBackgroundService>();
builder.Services.AddSingleton<Honua.Server.Features.Import.GeoServerImportJobManager>(sp =>
    new Honua.Server.Features.Import.GeoServerImportJobManager(
        sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(),
        sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Import.GeoServerImportJobManager>>(),
        sp.GetRequiredService<IHostEnvironment>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddHostedService<Honua.Server.Features.Import.GeoServerImportBackgroundService>();

// Register export background service with bounded channel
builder.Services.AddSingleton(System.Threading.Channels.Channel.CreateBounded<Honua.Server.Features.Export.ExportJob>(
    new System.Threading.Channels.BoundedChannelOptions(4)
    {
        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
    }));
builder.Services.AddHostedService<Honua.Server.Features.Export.ExportBackgroundService>();

builder.Services.Configure<Honua.Server.Features.Infrastructure.Events.FeatureChangeEventOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Events.FeatureChangeEventOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions>, Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptionsValidator>();
builder.Services.AddOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions>()
    .Bind(builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>(sp =>
    new Honua.Server.Features.Infrastructure.Events.InMemoryFeatureChangeEventStore(
        sp.GetRequiredService<IOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeEventOptions>>(),
        sp.GetService<IDistributedCache>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventPublisher>(sp =>
    new Honua.Server.Features.Infrastructure.Events.FeatureChangeEventPublisher(
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Infrastructure.Events.FeatureChangeEventPublisher>>()));
builder.Services.AddHttpClient("feature-change-webhook");
builder.Services.AddHostedService(sp =>
    new Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookDispatcher(
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>(),
        sp.GetService<IDistributedCache>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions>>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookDispatcher>>()));

// Register manifest drift webhook dispatcher (#515)
builder.Services.AddSingleton<IValidateOptions<Honua.Server.Features.Admin.ManifestDriftWebhookOptions>, Honua.Server.Features.Admin.ManifestDriftWebhookOptionsValidator>();
builder.Services.AddOptions<Honua.Server.Features.Admin.ManifestDriftWebhookOptions>()
    .Bind(builder.Configuration.GetSection(Honua.Server.Features.Admin.ManifestDriftWebhookOptions.SectionName));
builder.Services.AddHttpClient("manifest-drift-webhook");
builder.Services.AddHostedService(sp =>
    new Honua.Server.Features.Admin.ManifestDriftWebhookDispatcher(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetService<IDistributedCache>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<Honua.Server.Features.Admin.ManifestDriftWebhookOptions>>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Admin.ManifestDriftWebhookDispatcher>>()));

builder.Services.AddSingleton<Honua.Server.Features.Admin.TileOperations.ITileOperationJobService>(sp =>
    new Honua.Server.Features.Admin.TileOperations.TileOperationJobService(
        sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(),
        sp.GetService<IDistributedCache>(),
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Caching.OutputCacheInvalidationService>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<IOptions<Honua.Core.Features.Tiles.TileOptions>>(),
        sp.GetRequiredService<IOptions<LimitsOptions>>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Admin.TileOperations.TileOperationJobService>>()));
builder.Services.AddHostedService<Honua.Server.Features.Admin.TileOperations.TileOperationBackgroundService>();

// Register OData services and handlers
// Configure authentication options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.ApiKeyAuthenticationOptions>(options =>
{
    options.IsDevelopmentMode = builder.Environment.IsDevelopment();
    options.IsTestMode = builder.Environment.IsEnvironment("Test");
    options.AdminPassword = builder.Configuration["HONUA_ADMIN_PASSWORD"];
    options.DevAuthBypass = builder.Configuration["HONUA_DEV_AUTH"];
    options.EnableBasicAuthCompatibility =
        builder.Configuration.GetValue("Authentication:BasicCompatibility:Enabled",
            builder.Configuration.GetValue("HONUA_ENABLE_BASIC_AUTH_COMPAT", false));

    // Enforce HTTPS for basic auth in production - override configuration for security
    var requireHttpsForBasicAuth = builder.Configuration.GetValue("Authentication:BasicCompatibility:RequireHttps",
        builder.Configuration.GetValue("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", true));

    // Always require HTTPS for basic auth in non-development environments
    options.RequireHttpsForBasicAuth = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test")
        ? requireHttpsForBasicAuth
        : true;
});

// Configure OIDC authentication options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.OidcAuthenticationOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Authentication.OidcAuthenticationOptions.SectionName));

// Configure RBAC options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.RbacOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Authentication.RbacOptions.SectionName));

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
        Honua.Server.Features.ImageServer.Models.ImageServerJsonContext.Default,
        Honua.Server.Features.OData.Models.ODataJsonContext.Default,
        Honua.Server.Features.OgcFeatures.OgcJsonContext.Default,
        Honua.Server.Features.OgcMaps.Models.OgcMapsJsonContext.Default,
        Honua.Server.Features.OgcTiles.OgcTilesJsonContext.Default,
        Honua.Server.Features.Admin.Models.SecureConnectionJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerPublishingJsonContext.Default,
        Honua.Server.Features.Admin.Models.ServiceSettingsJsonContext.Default,
        Honua.Server.Features.Admin.Models.DeployControlJsonContext.Default,
        Honua.Server.Features.Infrastructure.Monitoring.MetricsJsonContext.Default,
        Honua.Server.Features.Import.ImportJsonContext.Default,
        Honua.Server.Features.Import.GeoservicesImportApiJsonContext.Default,
        Honua.Server.Features.Admin.OperationsProgressJsonContext.Default,
        Honua.Server.Features.Admin.FeatureEventReplayJsonContext.Default,
        Honua.Server.Features.Admin.TileOperations.TileOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.MetadataResourceJsonContext.Default,
        Honua.Server.Features.Admin.Models.ManifestApprovalJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerStyleJsonContext.Default,
        Honua.Server.Features.Admin.Models.AlertAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.LicenseJsonContext.Default,
        Honua.Server.Features.Admin.Models.OidcProviderJsonContext.Default,
        Honua.Server.Features.Admin.Models.UserManagementJsonContext.Default,
        Honua.Server.Features.Admin.Models.RoleJsonContext.Default,
        Honua.Server.Features.Admin.Models.RateLimitJsonContext.Default,
        Honua.Server.Features.Admin.Models.TableDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.AdminAuthJsonContext.Default,
        Honua.Server.Features.Admin.Models.ConfigurationJsonContext.Default,
        Honua.Server.Features.Admin.Models.LicenseAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.IdentityAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.CacheAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.GeocodingAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.FeatureOverviewJsonContext.Default,
        Honua.Server.Features.HealthCheck.HealthJsonContext.Default,
        Honua.Server.Features.Infrastructure.Models.ProblemJsonContext.Default,
        Honua.Server.Features.Infrastructure.Middleware.LimitsEnforcementJsonContext.Default,
        Honua.Server.Features.Infrastructure.Security.CspViolationJsonContext.Default,
        Honua.Server.Features.GeometryService.Models.GeometryServiceJsonContext.Default,
        Honua.Server.Features.Export.ExportJsonContext.Default);
});

// Add comprehensive IOptions configuration validation
builder.Services.AddConfigurationOptionsValidation();

var app = builder.Build();

var adminDotnetJsAlias = serveAdminUi
    ? ResolveAdminDotnetJsAlias(adminStaticAssetsManifestPath)
    : null;

var activeDbConnectionTracker = app.Services.GetService<IActiveDbConnectionTracker>();
if (activeDbConnectionTracker != null)
{
    EnhancedTelemetry.ConfigureActiveDbConnectionsProvider(activeDbConnectionTracker.GetActiveCount);
}

if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

app.UseHostValidation();

// Add HTTPS redirection middleware to enforce HTTPS for all requests
// This ensures API keys and sensitive data are never transmitted over HTTP
if (!app.Environment.IsDevelopment())
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
    app.Environment.IsDevelopment() ||
    app.Environment.IsEnvironment("Test"));

if (configurationErrors.Count > 0)
{
    var errorDetails = string.Join(Environment.NewLine, configurationErrors);
    ConfigurationLog.ConfigurationValidationFailed(app.Logger, configurationErrors.Count, errorDetails);
    throw new InvalidOperationException("Configuration validation failed. See logs for details.");
}

ConfigurationLog.ConfigurationValidationSucceeded(app.Logger);

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
if (!app.Environment.IsEnvironment("Test"))
{
    app.UseSecurityHeaders();
}

// Add response compression middleware (early in pipeline)
app.UseResponseCompression();

if (serveAdminUi)
{
    // F-01: Block admin UI in non-Development environments when OIDC is not configured.
    // Without OIDC the Blazor WASM client uses AnonymousAuthenticationStateProvider,
    // which renders the full admin dashboard to any visitor.
    var oidcEnabled = app.Configuration.GetValue<bool>($"{OidcAuthenticationOptions.SectionName}:Enabled");
    var blockAdminUi = !oidcEnabled &&
                       !app.Environment.IsDevelopment() &&
                       !app.Environment.IsEnvironment("Test");

    app.Map("/admin", adminApp =>
    {
        if (blockAdminUi)
        {
            adminApp.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json; charset=utf-8";
                await context.Response.WriteAsync(
                    """{"title":"Unauthorized","status":401,"detail":"Admin UI is disabled because OIDC authentication is not configured. Configure Oidc:Enabled in appsettings or environment variables before accessing the admin UI in production."}""")
                    .ConfigureAwait(false);
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(adminDotnetJsAlias))
        {
            adminApp.Use(async (context, next) =>
            {
                if (context.Request.Path.Equals("/_framework/dotnet.js", StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.Path = new PathString($"/_framework/{adminDotnetJsAlias}");
                }

                await next().ConfigureAwait(false);
            });
        }

        adminApp.UseBlazorFrameworkFiles();
        adminApp.UseStaticFiles();
        adminApp.UseRouting();
        adminApp.UseEndpoints(endpoints =>
        {
            if (File.Exists(adminStaticAssetsManifestPath))
            {
                endpoints.MapStaticAssets(adminStaticAssetsManifestPath);
            }
            else
            {
                endpoints.MapStaticAssets();
            }
            endpoints.MapFallbackToFile("index.html");
        });
    });
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
            .AddDocument("tiles", "OGC API Tiles", "/ogc/tiles/openapi.json")
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

// Add global exception handling middleware (after correlation ID for exception logging)
app.UseGlobalExceptionHandling();

// Enable gRPC-Web for all gRPC services (before CORS and endpoint mapping)
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

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

// Log application startup
var appVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
Honua.Server.Features.Infrastructure.Logging.Log.ApplicationStarting(app.Logger,
    appVersion,
    app.Environment.EnvironmentName);

// Run database migrations on startup
await RunDatabaseMigrationsAsync();

// Configure health endpoints
app.MapHealthEndpoints();
app.MapPrometheusEndpoint();

// Configure admin auth bootstrap endpoint (anonymous - must precede admin group)
app.MapAdminAuthEndpoints();

// Configure admin endpoints
app.MapAdminEndpoints();
app.MapAdminObservabilityEndpoints();

// Configure layer publishing endpoints
app.MapLayerPublishingEndpoints();

// Configure service settings endpoints (protocol toggles + MapServer config)
app.MapServiceSettingsEndpoints();

// Configure admin metadata version/manifest endpoints
app.MapAdminMetadataEndpoints();
app.MapAdminManifestApprovalEndpoints();
app.MapAdminManifestDriftEndpoints();
app.MapDeployControlEndpoints();

// Configure admin layer style endpoints
app.MapAdminLayerStyleEndpoints();

// Configure admin alerting zone/rule endpoints
app.MapAlertAdminEndpoints();

// Configure platform admin endpoints (license, identity, cache, geocoding, features)
app.MapLicenseAdminEndpoints();
app.MapIdentityAdminEndpoints();
app.MapCacheAdminEndpoints();
app.MapGeocodingAdminEndpoints();
app.MapFeatureOverviewEndpoints();

// Configure secure connection management endpoints
app.MapSecureConnectionEndpoints();

// Configure control plane IAM endpoints (#511)
app.MapLicenseEndpoints();
app.MapOidcProviderEndpoints();
app.MapUserManagementEndpoints();
app.MapRoleEndpoints();
app.MapRateLimitEndpoints();

// Configure metadata resource endpoints (ADR-0023)
app.MapMetadataResourceEndpoints();

// Configure security endpoints (CSP violation reporting)
app.MapCspViolationReportEndpoint();

// Configure FeatureServer, OGC, and OData endpoints
app.MapServerFeatureEndpoints();

// Configure gRPC feature service endpoint (gRPC-Web enabled via middleware)
app.MapGrpcService<Honua.Server.Features.Grpc.HonuaFeatureService>();
app.MapGrpcHealthChecksService();

// Enable gRPC reflection for dev tooling (grpcurl, grpcui, Postman)
if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

// Configure file import endpoints
app.MapImportEndpoints();

// Configure Geoservices service import endpoints
app.MapGeoservicesImportEndpoints();

// Configure GeoServer import endpoints
app.MapGeoServerImportEndpoints();

// Configure temporary file serving endpoints
app.MapTemporaryFileEndpoints();

// Configure data export endpoints
app.MapExportEndpoints();

// Configure unified operations progress endpoints
app.MapOperationsProgressEndpoints();
app.MapFeatureChangeEventsEndpoints();
app.MapTileOperationsEndpoints();

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

    // Wrap ILayerStyleCatalog with caching decorator
    var innerStyleCatalogDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ILayerStyleCatalog));
    if (innerStyleCatalogDescriptor != null)
    {
        services.Remove(innerStyleCatalogDescriptor);

        services.AddScoped<ILayerStyleCatalog>(sp =>
        {
            ILayerStyleCatalog innerStyleCatalog;
            if (innerStyleCatalogDescriptor.ImplementationFactory != null)
            {
                innerStyleCatalog = (ILayerStyleCatalog)innerStyleCatalogDescriptor.ImplementationFactory(sp);
            }
            else if (innerStyleCatalogDescriptor.ImplementationType != null)
            {
                innerStyleCatalog = (ILayerStyleCatalog)ActivatorUtilities.CreateInstance(sp, innerStyleCatalogDescriptor.ImplementationType);
            }
            else
            {
                throw new InvalidOperationException("Unable to resolve inner ILayerStyleCatalog implementation");
            }

            var cacheOptions = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            if (!cacheOptions.Enabled)
            {
                return innerStyleCatalog;
            }

            var cacheService = sp.GetRequiredService<ICacheService>();
            var options = sp.GetRequiredService<IOptions<CacheOptions>>();
            return new CachingLayerStyleCatalog(innerStyleCatalog, cacheService, options);
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
    var migrationState = app.Services.GetRequiredService<Honua.Server.Features.Infrastructure.Monitoring.MigrationState>();

    if (builder.Configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS"))
    {
        Honua.Server.Features.Infrastructure.Logging.Log.DatabaseMigrationsSkipped(app.Logger);
        migrationState.MarkSkipped("Migrations skipped by configuration.");
        return;
    }

    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
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
            migrationState.MarkFailed(errorMessage);

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
        migrationState.MarkFailed(ex.Message);

        // In non-Development environments, re-throw so the app fails to start
        // (gives a clear CrashLoopBackOff signal in Kubernetes).
        if (!app.Environment.IsDevelopment())
        {
            throw;
        }
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
        var innerCache = new CacheServiceResponseCache(
            sp.GetRequiredService<ICacheService>());
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

static string? ResolveAdminDotnetJsAlias(string endpointsManifestPath)
{
    if (!File.Exists(endpointsManifestPath))
    {
        return null;
    }

    using var stream = File.OpenRead(endpointsManifestPath);
    using var document = JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty("Endpoints", out var endpoints))
    {
        return null;
    }

    foreach (var endpoint in endpoints.EnumerateArray())
    {
        if (endpoint.TryGetProperty("Route", out var route) &&
            string.Equals(route.GetString(), "_framework/dotnet.js", StringComparison.OrdinalIgnoreCase) &&
            endpoint.TryGetProperty("AssetFile", out var assetFile))
        {
            var assetFilePath = assetFile.GetString();
            return string.IsNullOrWhiteSpace(assetFilePath) ? null : Path.GetFileName(assetFilePath);
        }
    }

    return null;
}
