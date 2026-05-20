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
using Honua.Core.Features.Metadata.Schema;
using Honua.Core.Features.Security;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.CloudDemo;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.Server.Features.Export;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.FileStorage;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Features.Infrastructure.Extensions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.RateLimiting;
using Honua.Server.Features.Infrastructure.Redis;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Mobile.Auth;
using Honua.Server.Features.Mobile.Diagnostics;
using Honua.Server.Features.Mobile.FieldCollection;
using Honua.Server.Features.Orchestration;
using Honua.Server.Features.Streaming;
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
AddSecurityConfiguration(builder.Configuration, builder.Environment);
builder.Services.AddDataProtection();

// Enable Aspire integrations only when Aspire configuration is present.
var useAspire = builder.Configuration.GetSection("Aspire").Exists();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? builder.Configuration["Aspire:StackExchange:Redis:ConnectionString"];
var redisCacheEntitled = await IsRedisCacheEntitledAsync(builder.Configuration);
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
    RegisterInfrastructureServices(builder.Services, builder.Configuration);
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
ConfigureLimits(builder.Services, builder.Configuration);

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
builder.Services.AddSingleton<IAwsLambdaAliasClient, AwsSdkLambdaAliasClient>();
builder.Services.AddSingleton<IAwsAlbClient, AwsSdkAlbClient>();
builder.Services.AddSingleton<IAwsEcsClient, AwsSdkEcsClient>();
builder.Services.AddSingleton<IAzureFunctionsSlotClient, AzureManagementFunctionsSlotClient>();
builder.Services.AddSingleton<IAzureContainerAppsRevisionClient, AzureManagementContainerAppsRevisionClient>();
builder.Services.AddSingleton<IAzureBatchClient, AzureBatchDataPlaneClient>();
builder.Services.AddSingleton<AzureBatchComputeBackend>();
builder.Services.AddSingleton<IBatchComputeBackend>(sp => sp.GetRequiredService<AzureBatchComputeBackend>());
builder.Services.AddSingleton<IDeployTargetRegistry, ConfigurationDeployTargetRegistry>();
builder.Services.AddSingleton<IExecutionJobDefinitionRegistry, ConfigurationExecutionJobDefinitionRegistry>();
builder.Services.AddSingleton<DeployWorkflowService>();
builder.Services.AddSingleton<IDeployTelemetrySignalEvaluator, PrometheusDeployTelemetrySignalEvaluator>();
builder.Services.AddSingleton<KubernetesGitOpsDeployBackend>();
builder.Services.AddSingleton<AwsEcsGitOpsDeployBackend>();
builder.Services.AddSingleton<AwsEcsAlbDeployBackend>();
builder.Services.AddSingleton<AzureContainerAppsGitOpsDeployBackend>();
builder.Services.AddSingleton<AzureContainerAppsRevisionDeployBackend>();
builder.Services.AddSingleton<AwsLambdaGitOpsDeployBackend>();
builder.Services.AddSingleton<AzureFunctionsGitOpsDeployBackend>();
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<KubernetesGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsEcsGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsEcsAlbDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsRevisionDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsLambdaGitOpsDeployBackend>());
builder.Services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureFunctionsGitOpsDeployBackend>());
builder.Services.AddSingleton<LocalBatchComputeBackend>();
builder.Services.AddSingleton<IBatchComputeBackend>(sp =>
    sp.GetRequiredService<LocalBatchComputeBackend>());

// AWS Batch backend follows the unconditional registration pattern used by sibling AWS deploy
// backends. Per-workload AWS Batch settings (job definition ARN, queue ARN, region, resource
// overrides) are carried on each ExecutionJobSpec.Parameters entry via ControlPlane:ExecutionWorkloads,
// so the adapter has no global options section it depends on. Registering unconditionally keeps
// the backend visible to the reconciler whenever an operator targets Backend=honua-aws-batch.
builder.Services.AddSingleton<IAwsBatchJobClient, AwsSdkBatchJobClient>();
builder.Services.AddSingleton<AwsBatchComputeBackend>();
builder.Services.AddSingleton<IBatchComputeBackend>(sp => sp.GetRequiredService<AwsBatchComputeBackend>());

builder.Services.AddSingleton<IKubernetesJobClient, KubernetesJobClient>();
builder.Services.AddSingleton<KubernetesJobBatchComputeBackend>();
builder.Services.AddSingleton<IBatchComputeBackend>(sp =>
    sp.GetRequiredService<KubernetesJobBatchComputeBackend>());
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
ConfigureTileOptions(builder.Services, builder.Configuration);

// Configure caching options and register cache services
ConfigureCaching(builder.Services, builder.Configuration, redisCacheEntitled);

// Configure cloud file storage for imports and attachments
builder.Services.AddCloudFileStorage(builder.Configuration);

// Configure file upload security limits
builder.Services.Configure<FileUploadSecurityOptions>(
    builder.Configuration.GetSection(FileUploadSecurityOptions.SectionName));

// Register configuration validators to ensure application fails fast on invalid configuration
RegisterConfigurationValidators(builder.Services);

// Register health check services
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Monitoring.MigrationState>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Monitoring.DatabaseCompatibilityState>();
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Monitoring.IDeployPreflightProbe,
    Honua.Server.Features.Infrastructure.Monitoring.DeployPreflightProbe>();
builder.Services.AddScoped<Honua.Server.Features.HealthCheck.IReadinessCheckService,
    Honua.Server.Features.HealthCheck.ReadinessCheckService>();
builder.Services.AddProductionHealthChecks(builder.Configuration);

builder.Services.Configure<Honua.Server.Features.Infrastructure.Licensing.LicenseOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Licensing.LicenseOptions.SectionName));
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Licensing.IEd25519Verifier,
    Honua.Server.Features.Infrastructure.Licensing.BouncyCastleEd25519Verifier>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Licensing.FileBackedLicenseService>();
builder.Services.AddSingleton<Honua.Core.Features.Licensing.Abstractions.ILicenseEntitlementService>(sp =>
    sp.GetRequiredService<Honua.Server.Features.Infrastructure.Licensing.FileBackedLicenseService>());
builder.Services.AddSingleton<Honua.Core.Features.Licensing.Abstractions.ILicenseStatusProvider>(sp =>
    sp.GetRequiredService<Honua.Server.Features.Infrastructure.Licensing.FileBackedLicenseService>());
builder.Services.AddSingleton<Honua.Core.Features.Licensing.Abstractions.ILicenseManager>(sp =>
    sp.GetRequiredService<Honua.Server.Features.Infrastructure.Licensing.FileBackedLicenseService>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Honua.Server.Features.Infrastructure.Licensing.FileBackedLicenseService>());

// Register named HTTP client for identity provider connectivity tests with resilience
builder.Services.AddResilientHttpClient(
    "IdentityProviderTest",
    "identity-provider-test",
    HttpResiliencePolicies.FastApiDefaults);
builder.Services.AddResilientHttpClient(
    "AdminAuthOidc",
    "admin-auth-oidc",
    HttpResiliencePolicies.FastApiDefaults,
    configureHandler: () => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });

// Register configuration documentation service for self-documenting admin endpoint
builder.Services.AddScoped<Honua.Server.Features.Admin.Services.ConfigurationDocumentationService>();

// Register control plane IAM services (in-memory implementations until #496, #498, #355 land)
builder.Services.AddSingleton<Honua.Core.Features.Identity.Abstractions.IOidcProviderStore,
    Honua.Server.Features.Admin.Services.InMemoryOidcProviderStore>();
builder.Services.AddSingleton<Honua.Core.Features.Identity.Abstractions.IUserStore,
    Honua.Server.Features.Admin.Services.InMemoryUserStore>();
builder.Services.AddSingleton<Honua.Core.Features.Authorization.Abstractions.IRoleStore,
    Honua.Server.Features.Admin.Services.InMemoryRoleStore>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Authentication.IAdminApiKeyStore>(sp =>
    new Honua.Server.Features.Infrastructure.Authentication.InMemoryAdminApiKeyStore(sp.GetService<TimeProvider>()));
builder.Services.AddSingleton<IMetadataSchemaRegistry, MetadataSchemaRegistry>();
builder.Services.AddSingleton<IMetadataCompiler, DefaultMetadataCompiler>();

// Register manifest approval workflow services
builder.Services.Configure<Honua.Server.Features.Admin.Models.ManifestApprovalOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Admin.Models.ManifestApprovalOptions.SectionName));
builder.Services.Configure<Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptions>,
    Honua.Server.Features.Admin.Models.ManifestApprovalWebhookOptionsValidator>();
builder.Services.AddResilientHttpClient(
    "manifest-approval-webhook",
    "manifest-approval-webhook",
    HttpResiliencePolicies.FastApiDefaults,
    configureHandler: static () => Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
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

// Register GitOps watch services (#518)
builder.Services.Configure<Honua.Server.Features.Admin.Models.GitOpsWatchOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Admin.Models.GitOpsWatchOptions.SectionName));
builder.Services.AddHostedService(sp =>
    new Honua.Server.Features.Admin.GitOpsWatchService(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<IOptions<Honua.Server.Features.Admin.Models.GitOpsWatchOptions>>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Admin.GitOpsWatchService>>()));

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

// Register Geoservices import job manager and background service
builder.Services.AddSingleton<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(sp =>
    new Honua.Server.Features.Import.UniversalProgressStore(
        sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Import.UniversalProgressStore>>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.Configure<Honua.Server.Features.Import.FileUploadOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Import.FileUploadOptions.SectionName));
builder.Services.AddSingleton<Honua.Server.Features.Import.StreamingFileUploadService>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Abstractions.IUploadQueueMetricsProvider>(sp =>
    sp.GetRequiredService<Honua.Server.Features.Import.StreamingFileUploadService>());
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

// Register export background service with durable request persistence and a bounded in-process scheduler.
builder.Services.AddSingleton(System.Threading.Channels.Channel.CreateBounded<string>(
    new System.Threading.Channels.BoundedChannelOptions(4)
    {
        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
    }));
builder.Services.AddSingleton<Honua.Server.Features.Export.IExportJobService>(sp =>
    new Honua.Server.Features.Export.ExportJobService(
        sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IUniversalProgressStore>(),
        sp.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        sp.GetRequiredService<System.Threading.Channels.Channel<string>>(),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Export.ExportJobService>>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddHostedService<Honua.Server.Features.Export.ExportBackgroundService>();


builder.Services.Configure<Honua.Server.Features.Infrastructure.Events.FeatureChangeEventOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Events.FeatureChangeEventOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions>, Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptionsValidator>();
builder.Services.AddOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions>()
    .Bind(builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>(sp =>
{
    return new Honua.Server.Features.Infrastructure.Events.InMemoryFeatureChangeEventStore(
        sp.GetRequiredService<IOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeEventOptions>>(),
        sp.GetService<IConnectionMultiplexer>(),
        allowInMemoryFallback: !requiresDurableDistributedEvents);
});
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStoreHealth>(sp =>
    (Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStoreHealth)sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>());
// Register feature-stream session manager and streaming publisher (#501)
builder.Services.AddOptions<Honua.Server.Features.Streaming.FeatureStreamOptions>()
    .Bind(builder.Configuration.GetSection(Honua.Server.Features.Streaming.FeatureStreamOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<Honua.Server.Features.Streaming.FeatureStreamOptions>,
    Honua.Server.Features.Streaming.FeatureStreamOptionsValidator>();
builder.Services.AddSingleton<Honua.Server.Features.Streaming.FeatureStreamSessionManager>(sp =>
    new Honua.Server.Features.Streaming.FeatureStreamSessionManager(
        sp.GetRequiredService<IOptions<Honua.Server.Features.Streaming.FeatureStreamOptions>>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Streaming.FeatureStreamSessionManager>>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddSingleton(System.Threading.Channels.Channel.CreateUnbounded<Honua.Server.Features.Infrastructure.Events.PendingFeatureChangeSignal>());
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.IFeatureChangeRetryQueue>(sp =>
    new Honua.Server.Features.Infrastructure.Events.FeatureChangeRetryQueue(
        sp.GetService<IDistributedCache>(),
        sp.GetRequiredService<System.Threading.Channels.Channel<Honua.Server.Features.Infrastructure.Events.PendingFeatureChangeSignal>>(),
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>(),
        sp.GetRequiredService<Honua.Server.Features.Streaming.FeatureStreamSessionManager>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Infrastructure.Events.FeatureChangeRetryQueue>>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventPublisher>(sp =>
    new Honua.Server.Features.Streaming.FeatureStreamPublisher(
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>(),
        sp.GetRequiredService<Honua.Server.Features.Streaming.FeatureStreamSessionManager>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Streaming.FeatureStreamPublisher>>(),
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.IFeatureChangeRetryQueue>()));
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.FeatureMutationEventService>();

// Feature-change transactional outbox (#692). Capability provider, dispatcher, health,
// and metrics. The outbox repository itself is registered by the active provider's
// service-collection extension (Postgres registers a working repository; SQL Server and
// DuckDB register only the capability provider since they do not support feature writes).
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<Honua.Server.Features.Infrastructure.Events.Outbox.OutboxDispatcherOptions>,
    Honua.Server.Features.Infrastructure.Events.Outbox.OutboxDispatcherOptionsValidator>();
builder.Services.AddOptions<Honua.Server.Features.Infrastructure.Events.Outbox.OutboxDispatcherOptions>()
    .Bind(builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Events.Outbox.OutboxDispatcherOptions.SectionName))
    .ValidateOnStart();
// Default capability provider for hosts that bypass RegisterInfrastructureServices (test
// factories). The active provider's extension uses AddSingleton, so when it runs first
// it wins over this TryAdd; when infrastructure registration is skipped, the dispatcher
// still constructs and immediately exits because SupportsTransactionalOutbox is false.
builder.Services.TryAddSingleton<
    Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxCapabilityProvider,
    Honua.Server.Features.Infrastructure.Events.Outbox.NoOpOutboxCapabilityProvider>();
builder.Services.AddSingleton<Honua.Server.Features.Infrastructure.Events.Outbox.OutboxDispatcherBackgroundService>();
builder.Services.AddSingleton<Honua.Core.Features.Infrastructure.Events.Outbox.IOutboxHealth>(sp =>
    sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.Outbox.OutboxDispatcherBackgroundService>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.Outbox.OutboxDispatcherBackgroundService>());
builder.Services.AddHostedService<Honua.Server.Features.Infrastructure.Events.FeatureChangeRetryBackgroundService>();
builder.Services.AddScoped<Honua.Server.Features.Streaming.FeatureStreamDependencies>();
builder.Services.AddHostedService<Honua.Server.Features.Streaming.FeatureStreamHeartbeatService>();
builder.Services.AddResilientHttpClient(
    "feature-change-webhook",
    "feature-change-webhook",
    HttpResiliencePolicies.FastApiDefaults,
    configureHandler: static () => Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
builder.Services.AddHostedService(sp =>
    new Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookDispatcher(
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Events.IFeatureChangeEventStore>(),
        sp.GetService<IDistributedCache>(),
        sp.GetService<IConnectionMultiplexer>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IOptions<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptions>>(),
        sp.GetRequiredService<ILogger<Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookDispatcher>>()));
builder.Services.AddCollaborationSessionTransport();

// Register manifest drift webhook dispatcher (#515)
builder.Services.AddSingleton<IValidateOptions<Honua.Server.Features.Admin.ManifestDriftWebhookOptions>, Honua.Server.Features.Admin.ManifestDriftWebhookOptionsValidator>();
builder.Services.AddOptions<Honua.Server.Features.Admin.ManifestDriftWebhookOptions>()
    .Bind(builder.Configuration.GetSection(Honua.Server.Features.Admin.ManifestDriftWebhookOptions.SectionName));
builder.Services.AddResilientHttpClient(
    "manifest-drift-webhook",
    "manifest-drift-webhook",
    HttpResiliencePolicies.FastApiDefaults,
    configureHandler: static () => Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
builder.Services.AddHostedService(sp =>
    new Honua.Server.Features.Admin.ManifestDriftWebhookDispatcher(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetService<IDistributedCache>(),
        sp.GetService<IConnectionMultiplexer>(),
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
        sp.GetRequiredService<ILogger<Honua.Server.Features.Admin.TileOperations.TileOperationJobService>>(),
        sp.GetService<IConnectionMultiplexer>()));
builder.Services.Configure<Honua.Server.Features.Admin.TileOperations.TileCacheWarmingOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Admin.TileOperations.TileCacheWarmingOptions.SectionName));
builder.Services.AddHostedService<Honua.Server.Features.Admin.TileOperations.TileCacheWarmingHostedService>();
builder.Services.AddHostedService<Honua.Server.Features.Admin.TileOperations.TileOperationBackgroundService>();

// Register OData services and handlers
// Configure authentication options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.ApiKeyAuthenticationOptions>(options =>
{
    options.IsDevelopmentMode = builder.Environment.IsDevelopment();
    options.IsTestMode = builder.Environment.IsEnvironment("Test");
    var adminPassword = builder.Configuration["HONUA_ADMIN_PASSWORD"];

    // SECURITY: Validate admin password complexity in production
    if (builder.Environment.IsProduction() && !string.IsNullOrEmpty(adminPassword))
    {
        if (adminPassword.Length < 16)
        {
            throw new InvalidOperationException("Admin password must be at least 16 characters in production environment");
        }

        bool hasUpper = adminPassword.Any(char.IsUpper);
        bool hasLower = adminPassword.Any(char.IsLower);
        bool hasDigit = adminPassword.Any(char.IsDigit);
        bool hasSpecial = adminPassword.Any(c => !char.IsLetterOrDigit(c));

        if (!(hasUpper && hasLower && hasDigit && hasSpecial))
        {
            throw new InvalidOperationException("Admin password must contain uppercase, lowercase, digit, and special characters in production environment");
        }
    }

    options.AdminPassword = adminPassword;
    options.DevAuthBypass = builder.Configuration["HONUA_DEV_AUTH"];
    options.EnableBasicAuthCompatibility =
        builder.Configuration.GetValue("Authentication:BasicCompatibility:Enabled",
            builder.Configuration.GetValue("HONUA_ENABLE_BASIC_AUTH_COMPAT", false));

    // SECURITY: Always enforce HTTPS for basic auth in production - cannot be overridden
    if (builder.Environment.IsProduction())
    {
        options.RequireHttpsForBasicAuth = true;
    }
    else
    {
        // In development/test environments, allow configuration override
        var requireHttpsForBasicAuth = builder.Configuration.GetValue("Authentication:BasicCompatibility:RequireHttps",
            builder.Configuration.GetValue("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", true));
        options.RequireHttpsForBasicAuth = requireHttpsForBasicAuth;
    }
});

// Configure OIDC authentication options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.OidcAuthenticationOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Authentication.OidcAuthenticationOptions.SectionName));

// Configure mobile runtime auth refresh options
builder.Services.Configure<Honua.Server.Features.Mobile.Auth.MobileAuthOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Mobile.Auth.MobileAuthOptions.SectionName));

// Configure RBAC options
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.RbacOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Authentication.RbacOptions.SectionName));

// Configure operator authorization and approval
builder.Services.AddSingleton<Honua.Core.Features.Authorization.Abstractions.IOperatorAuthorizationEvaluator,
    Honua.Server.Features.Infrastructure.Authentication.OperatorAuthorizationEvaluator>();
builder.Services.AddSingleton<Honua.Core.Features.Authorization.Abstractions.IOperatorApprovalEvaluator,
    Honua.Server.Features.Infrastructure.Authentication.DefaultOperatorApprovalEvaluator>();
builder.Services.Configure<Honua.Server.Features.Infrastructure.Authentication.OperatorApprovalOptions>(
    builder.Configuration.GetSection(Honua.Server.Features.Infrastructure.Authentication.OperatorApprovalOptions.SectionName));
builder.Services.AddScoped<Honua.Server.Features.Infrastructure.Authentication.OperatorApprovalGate>();

// Configure authentication and authorization
builder.Services.AddApiKeyAuthentication();

// Add OIDC authentication if enabled
builder.Services.AddOidcAuthentication(builder.Configuration);
builder.Services.AddOidcAuthorization(builder.Configuration);
builder.Services.AddSingleton<AdminAuthSessionStore>();
// Configure security headers
builder.Services.AddSecurityHeaders(builder.Configuration);
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
        Honua.Server.Features.Admin.Models.DeployControlJsonContext.Default,
        Honua.Server.Features.Infrastructure.Monitoring.MetricsJsonContext.Default,
        Honua.Server.Features.Import.ImportJsonContext.Default,
        Honua.Server.Features.Import.RasterImportJsonContext.Default,
        Honua.Server.Features.Import.GeoservicesImportApiJsonContext.Default,
        Honua.Server.Features.Admin.OperationsProgressJsonContext.Default,
        Honua.Server.Features.Admin.FeatureEventReplayJsonContext.Default,
        Honua.Server.Features.Mobile.Auth.MobileAuthJsonContext.Default,
        Honua.Server.Features.Mobile.Diagnostics.MobileExceptionIngestionJsonContext.Default,
        Honua.Server.Features.Mobile.FieldCollection.FieldCollectionSyncJsonContext.Default,
        Honua.Server.Features.Admin.TileOperations.TileOperationsJsonContext.Default,
        Honua.Server.Features.Admin.Models.MetadataResourceJsonContext.Default,
        Honua.Server.Features.Admin.Models.ManifestApprovalJsonContext.Default,
        Honua.Server.Features.Admin.Models.GitOpsWatchJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerStyleJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerFieldConfigurationJsonContext.Default,
        Honua.Server.Features.Admin.Models.LayerValidationJsonContext.Default,
        Honua.Server.Features.Admin.Models.StyleSuggestionJsonContext.Default,
        Honua.Server.Features.Admin.Models.AlertAdminJsonContext.Default,
        Honua.Server.Features.Admin.Models.LicenseJsonContext.Default,
        Honua.Server.Features.Admin.Models.OidcProviderJsonContext.Default,
        Honua.Server.Features.Admin.Models.UserManagementJsonContext.Default,
        Honua.Server.Features.Admin.Models.RoleJsonContext.Default,
        Honua.Server.Features.Admin.Models.AdminApiKeyJsonContext.Default,
        Honua.Server.Features.Admin.Models.SceneDatasetJsonContext.Default,
        Honua.Server.Features.Admin.Models.SceneGenerationJsonContext.Default,
        Honua.Server.Features.Protocols.Scene.Models.PublicSceneDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.RateLimitJsonContext.Default,
        Honua.Server.Features.Admin.Models.TableDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.ExternalServiceDiscoveryJsonContext.Default,
        Honua.Server.Features.Admin.Models.AdminAuthJsonContext.Default,
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
        Honua.Server.Features.CloudDemo.CloudDemoJsonContext.Default,
        Honua.Server.Features.HealthCheck.HealthJsonContext.Default,
        Honua.Server.Features.Infrastructure.Models.ProblemJsonContext.Default,
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
        Honua.Server.Features.Protocols.Ogc.Api.Processes.OgcProcessesJsonContext.Default);
});

// Add comprehensive IOptions configuration validation
builder.Services.AddConfigurationOptionsValidation();

var app = builder.Build();

FilterHostedBlazorStaticAssetEndpoints(
    app,
    allowStacOpsDemoAssets: serveStacOpsDemo);

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
    ConfigureHostedBlazorAssets(app, stacOpsDemoPathPrefix);
    app.MapGet("/samples/stac-ops", () => Results.Redirect("/samples/stac-ops/index.html"))
        .ExcludeFromDescription();
    MapHostedBlazorFallback(app, stacOpsDemoPathPrefix);
}
else
{
    MapDisabledHostedBlazorPrefix(app, stacOpsDemoPathPrefix);
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

// Enable gRPC-Web for all gRPC services (before CORS and endpoint mapping)
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

// Add CORS middleware before auth to handle preflight requests
app.UseHonuaCors(app.Environment);

// Validate query, form, and selected header inputs before authentication and endpoint execution.
app.UseInputValidation();

// Add authentication and authorization middleware early to short-circuit unauthorized requests
app.UseApiKeyAuthentication();

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
app.MapAdminRealtimeHub();

// Configure layer publishing endpoints
app.MapLayerPublishingEndpoints();

// Configure service settings endpoints (protocol toggles + MapServer config)
app.MapServiceSettingsEndpoints();

// Configure admin metadata version/manifest endpoints
app.MapAdminMetadataEndpoints();
app.MapAdminManifestApprovalEndpoints();
app.MapAdminManifestDriftEndpoints();
app.MapAdminGitOpsWatchEndpoints();
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
app.MapAdminApiKeyEndpoints();

// Configure metadata resource endpoints (ADR-0023)
app.MapMetadataResourceEndpoints();

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
app.MapRasterImportEndpoints();

// Configure Geoservices service import endpoints
app.MapGeoservicesImportEndpoints();

// Configure GeoServer import endpoints
app.MapGeoServerImportEndpoints();

// Configure GeoServer migration run admin orchestration endpoints (issue #1015 slice 5)
app.MapMigrationRunAdminEndpoints();

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
    else if (provider.Equals("duckdb", StringComparison.OrdinalIgnoreCase))
    {
        Honua.DuckDB.ServiceCollectionExtensions.AddDuckDBServices(services, configuration);
    }
    else if (provider.Equals(DataProviderNames.MySql, StringComparison.OrdinalIgnoreCase) ||
             provider.Equals("mariadb", StringComparison.OrdinalIgnoreCase))
    {
        Honua.MySql.ServiceCollectionExtensions.AddMySqlServices(services, configuration);
    }
    else
    {
        throw new InvalidOperationException($"Unsupported data source provider '{provider}'.");
    }

    // Register the SQL Server spatial provider as an additional read-only feature backend (#850).
    // Layers whose connection resolves to provider 'sqlserver'/'mssql' are routed here through the
    // shared FeatureProviderBindingResolver. Disabled when SqlServer:Enabled is explicitly false.
    if (configuration.GetValue("SqlServer:Enabled", true))
    {
        Honua.SqlServer.ServiceCollectionExtensions.AddSqlServerFeatureProvider(services, configuration);
    }

    services.TryAddScoped<IFeatureDataProviderRegistry>(serviceProvider =>
        new FeatureDataProviderRegistry(serviceProvider.GetServices<IFeatureDataProvider>()));
    services.TryAddScoped(serviceProvider =>
        new FeatureProviderBindingResolver(
            serviceProvider.GetRequiredService<Honua.Core.Features.Security.Abstractions.ISecureConnectionRegistry>(),
            serviceProvider.GetRequiredService<IFeatureDataProviderRegistry>(),
            DataProviderNames.Normalize(provider)));
    services.TryAddScoped<FeatureProviderQueryRouter>();

    // Add centralized configuration management and secret services
    services.AddConfigurationManagement(configuration);

    // Wrap ILayerCatalog with caching decorator
    // This uses the decorator pattern to add caching behavior transparently
    var innerCatalogDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ILayerCatalog));
    if (innerCatalogDescriptor != null)
    {
        services.Remove(innerCatalogDescriptor);

        // Shared resolver for the data-source catalog (PostgresLayerCatalog) — avoids
        // duplicating the resolution logic across the main and keyed registrations.
        ILayerCatalog ResolveDataSourceCatalog(IServiceProvider sp)
        {
            if (innerCatalogDescriptor.ImplementationFactory != null)
                return (ILayerCatalog)innerCatalogDescriptor.ImplementationFactory(sp);
            if (innerCatalogDescriptor.ImplementationType != null)
                return (ILayerCatalog)ActivatorUtilities.CreateInstance(sp, innerCatalogDescriptor.ImplementationType);
            throw new InvalidOperationException("Unable to resolve inner ILayerCatalog implementation");
        }

        // Register the data-source catalog as a keyed service so the background refresh
        // decorator can fetch fresh data without going through the caching layer.
        // Wrapped with the monitoring decorator so background refresh reads remain
        // observable in catalog telemetry while still bypassing the cache.
        services.AddKeyedScoped<ILayerCatalog>(
            BackgroundRefreshCacheDecorator.UncachedCatalogServiceKey,
            (sp, _) =>
            {
                var catalog = ResolveDataSourceCatalog(sp);
                var performanceMonitor = sp.GetRequiredService<IPerformanceMonitor>();
                var monitorLogger = sp.GetRequiredService<ILogger<MonitoredLayerCatalogDecorator>>();
                return new MonitoredLayerCatalogDecorator(catalog, performanceMonitor, monitorLogger);
            });

        services.AddScoped<ILayerCatalog>(sp =>
        {
            ILayerCatalog innerCatalog = ResolveDataSourceCatalog(sp);

            // Apply caching decorator if enabled
            var cacheOptions = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            ILayerCatalog catalog = innerCatalog;
            if (cacheOptions.Enabled)
            {
                var cacheService = sp.GetRequiredService<ICacheService>();
                var options = sp.GetRequiredService<IOptions<CacheOptions>>();
                var schemaContext = sp.GetService<ISchemaContext>();
                catalog = new CachingLayerCatalog(catalog, cacheService, options, schemaContext);

                // Wrap with background refresh decorator for stale-while-revalidate
                if (cacheOptions.BackgroundRefreshEnabled)
                {
                    var refreshCoordinator = sp.GetRequiredService<ICacheRefreshCoordinator>();
                    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                    var refreshLogger = sp.GetRequiredService<ILogger<BackgroundRefreshCacheDecorator>>();
                    catalog = new BackgroundRefreshCacheDecorator(catalog, cacheService, refreshCoordinator, scopeFactory, options, refreshLogger, schemaContext);
                }
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
            var schemaContext = sp.GetService<ISchemaContext>();
            return new CachingLayerStyleCatalog(innerStyleCatalog, cacheService, options, schemaContext);
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

static async Task<bool> IsRedisCacheEntitledAsync(IConfiguration configuration)
{
    var redisConnectionString = configuration.GetConnectionString("redis")
        ?? configuration["Aspire:StackExchange:Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(redisConnectionString))
    {
        return false;
    }

    using var loggerFactory = LoggerFactory.Create(static builder => builder.AddConsole());
    var snapshot = await Honua.Server.Features.Infrastructure.Licensing.FileBackedLicenseService
        .LoadBootstrapSnapshotAsync(configuration, loggerFactory)
        .ConfigureAwait(false);
    return snapshot.HasEntitlement("caching.redis");
}

// Configure caching services with Redis and in-memory fallback
static void ConfigureCaching(IServiceCollection services, IConfiguration configuration, bool redisCacheEntitled)
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
        var redis = redisCacheEntitled ? sp.GetService<IConnectionMultiplexer>() : null;

        // StackExchangeRedisCache prepends its InstanceName to all keys internally.
        // Raw multiplexer operations (e.g., TTL lookup) must use the same prefix.
        var redisCacheOpts = sp.GetService<IOptions<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions>>();
        var instanceName = redisCacheOpts?.Value?.InstanceName;

        return new RedisCacheService(distributedCache, options, logger, performanceMonitor, redis, instanceName);
    });

    // Register interfaces pointing to the singleton
    services.AddSingleton<ICacheService>(sp => sp.GetRequiredService<RedisCacheService>());
    services.AddSingleton<ICacheHealthChecker>(sp => sp.GetRequiredService<RedisCacheService>());
    services.AddSingleton<ICacheStorageMetricsProvider>(sp => sp.GetRequiredService<RedisCacheService>());

    services.AddSingleton<IResponseCache>(sp =>
    {
        var innerCache = new CacheServiceResponseCache(
            sp.GetRequiredService<ICacheService>());
        return new MonitoredResponseCacheDecorator(
            innerCache,
            sp.GetRequiredService<IPerformanceMonitor>(),
            sp.GetRequiredService<ILogger<MonitoredResponseCacheDecorator>>());
    });

    // Register distributed cache refresh coordinator
    services.AddSingleton<Honua.Server.Features.Infrastructure.Caching.DistributedCacheRefreshCoordinator>(sp =>
        new Honua.Server.Features.Infrastructure.Caching.DistributedCacheRefreshCoordinator(
            sp.GetRequiredService<IOptions<CacheOptions>>(),
            sp.GetRequiredService<IPerformanceMonitor>(),
            sp.GetRequiredService<ILogger<Honua.Server.Features.Infrastructure.Caching.DistributedCacheRefreshCoordinator>>(),
            redisCacheEntitled ? sp.GetService<IConnectionMultiplexer>() : null));

    services.AddSingleton<ICacheRefreshCoordinator>(sp =>
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Caching.DistributedCacheRefreshCoordinator>());
    services.AddSingleton<Honua.Core.Features.Caching.Abstractions.IDistributedCacheRefreshCoordinator>(sp =>
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Caching.DistributedCacheRefreshCoordinator>());
    services.AddHostedService(sp =>
        sp.GetRequiredService<Honua.Server.Features.Infrastructure.Caching.DistributedCacheRefreshCoordinator>());

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

static void ConfigureHostedBlazorAssets(
    IApplicationBuilder app,
    PathString pathPrefix)
{
    var prefix = pathPrefix.Value?.Trim('/') ??
        throw new InvalidOperationException("Hosted Blazor path prefix is required.");
    var webRootFileProvider = app.ApplicationServices
        .GetRequiredService<IWebHostEnvironment>()
        .WebRootFileProvider;

    // Keep hosted shells isolated to their configured prefix so enabling one
    // does not expose another shell's static web assets.
    app.UseStaticFiles(new StaticFileOptions
    {
        RequestPath = pathPrefix,
        FileProvider = new PathPrefixFileProvider(webRootFileProvider, prefix)
    });
}

static void MapHostedBlazorFallback(
    IEndpointRouteBuilder endpoints,
    PathString pathPrefix)
{
    var prefix = pathPrefix.Value?.TrimEnd('/') ??
        throw new InvalidOperationException("Hosted Blazor path prefix is required.");
    var fallbackRoute = $"{prefix}/{{*path:nonfile}}";
    var fallbackFile = $"{prefix.TrimStart('/')}/index.html";

    endpoints.MapFallbackToFile(fallbackRoute, fallbackFile);
}

static void FilterHostedBlazorStaticAssetEndpoints(
    WebApplication app,
    bool allowStacOpsDemoAssets)
{
    var blockedPrefixes = new List<string>(1);
    // Always block the `admin/` static-asset prefix — the in-tree Blazor admin UI
    // moved to the sibling `honua-server-admin` repo and ships as a separately
    // deployed static site.
    blockedPrefixes.Add("admin/");

    if (!allowStacOpsDemoAssets)
    {
        blockedPrefixes.Add("samples/stac-ops/");
    }

    var routeBuilder = (IEndpointRouteBuilder)app;
    var dataSources = routeBuilder.DataSources.ToArray();

    routeBuilder.DataSources.Clear();
    foreach (var dataSource in dataSources)
    {
        routeBuilder.DataSources.Add(new HostedBlazorStaticAssetFilterDataSource(
            dataSource,
            blockedPrefixes));
    }
}

static void MapDisabledHostedBlazorPrefix(
    IApplicationBuilder app,
    PathString pathPrefix)
{
    app.Map(pathPrefix, disabledApp =>
    {
        disabledApp.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    });
}

sealed class PathPrefixFileProvider(
    IFileProvider innerProvider,
    string pathPrefix) : IFileProvider
{
    private readonly IFileProvider _innerProvider = innerProvider;
    private readonly string _pathPrefix = pathPrefix;

    public IDirectoryContents GetDirectoryContents(string subpath)
        => _innerProvider.GetDirectoryContents(ApplyPrefix(subpath));

    public IFileInfo GetFileInfo(string subpath)
        => _innerProvider.GetFileInfo(ApplyPrefix(subpath));

    public IChangeToken Watch(string filter)
        => _innerProvider.Watch(ApplyPrefix(filter));

    private string ApplyPrefix(string subpath)
    {
        var trimmedSubpath = subpath.TrimStart('/');
        return string.IsNullOrEmpty(trimmedSubpath)
            ? _pathPrefix
            : $"{_pathPrefix}/{trimmedSubpath}";
    }
}

sealed class HostedBlazorStaticAssetFilterDataSource(
    EndpointDataSource innerDataSource,
    IReadOnlyCollection<string> blockedPrefixes) : EndpointDataSource
{
    private readonly EndpointDataSource _innerDataSource = innerDataSource;
    private readonly IReadOnlyCollection<string> _blockedPrefixes = blockedPrefixes;

    public override IReadOnlyList<Endpoint> Endpoints => FilterEndpoints();

    public override IChangeToken GetChangeToken() => _innerDataSource.GetChangeToken();

    private IReadOnlyList<Endpoint> FilterEndpoints()
    {
        if (_blockedPrefixes.Count == 0)
        {
            return _innerDataSource.Endpoints;
        }

        return _innerDataSource.Endpoints
            .Where(ShouldKeepEndpoint)
            .ToArray();
    }

    private bool ShouldKeepEndpoint(Endpoint endpoint)
    {
        if (endpoint is not RouteEndpoint routeEndpoint)
        {
            return true;
        }

        var route = routeEndpoint.RoutePattern.RawText;
        if (string.IsNullOrEmpty(route))
        {
            return true;
        }

        foreach (var blockedPrefix in _blockedPrefixes)
        {
            if (route.StartsWith(blockedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
