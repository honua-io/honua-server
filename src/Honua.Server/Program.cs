// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
// ✅ DEPENDENCY INVERSION: Server uses Core abstractions only
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
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
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Federation;
using Honua.Server.Features.Admin.Jobs;
using Honua.Server.Features.Admin.OperateFixtures;
using Honua.Server.Features.Admin.Share;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.CloudDemo;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Console;
using Honua.Server.Features.Console.Collaboration;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.Io.Export;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.Provisioner;
using Honua.ControlPlane;
using Honua.FileStorage;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Identity;
using Honua.Server.Features.Identity.Saml;
using Honua.Server.Features.Identity.Scim;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.Import.TileCachePackage;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Infrastructure.AuditLog;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Configuration;
using Honua.Infrastructure.Extensions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Hosting;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.RateLimiting;
using Honua.Infrastructure.Redis;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Styling;
using Honua.Infrastructure.Validation;
using Honua.Server.Features.Mobile.Auth;
using Honua.Server.Features.Mobile.Diagnostics;
using Honua.Server.Features.Mobile.FieldCollection;
using Honua.Server.Features.Orchestration;
using Honua.Server.Features.Studio;
using Honua.PackageReview;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Operations.Status;
using Honua.Server.Features.Streaming;
using Honua.Server.Features.WorkflowPackages;
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
// Standalone Test images (e.g. the honua-console live lane's Testcontainers image) expose the
// Operate observability fixture seed endpoint and must register the real data providers the
// seeder resolves. They run their own migrations, unlike in-process WebAppFixture hosts which
// set HONUA_SKIP_MIGRATIONS and wire isolated providers themselves (honua-server#2350).
var operateObservabilityFixtureEnabled =
    Honua.Server.Features.Admin.OperateFixtures.OperateObservabilityFixtureOptions.ResolveEnabled(
        builder.Configuration,
        builder.Configuration.GetValue<bool>(
            $"{Honua.Server.Features.Admin.OperateFixtures.OperateObservabilityFixtureOptions.SectionName}:Enabled"));
var hostManagesOwnMigrations = !builder.Configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS");

// A standalone fixture host (Development/Test only) needs the connection-encryption master key
// the Postgres secure-connection provider requires; the honua-console Testcontainers harness does
// not supply it. Fill deterministic dev defaults for any keys not already configured so the seed
// endpoint — and the audit-log/connection-provider path every request hits — works out of the box
// (honua-server#2350). Skipped in Production because the fixture validator forbids it there.
if (operateObservabilityFixtureEnabled &&
    (builder.Environment.IsDevelopment() || isTestEnvironment))
{
    var operateFixtureHostDefaults =
        Honua.Server.Features.Admin.OperateFixtures.OperateObservabilityFixtureHostDefaults
            .CreateMissingDefaults(builder.Configuration);
    if (operateFixtureHostDefaults.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(operateFixtureHostDefaults);
    }
}
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
// ASP.NET's static-web-assets loader eagerly opens every content root listed in the runtime
// manifest — including obj/<Config>/<tfm>/compressed/ (the hosted Blazor demo's precompressed
// assets). In the sharded Release *test* build that directory is not reliably materialized, so
// the loader throws DirectoryNotFoundException at host construction and fails every test in the
// shard before any test logic runs (honua-server#2904). API integration tests do not need the
// hosted Blazor assets, so the Test host skips the loader by default; the few tests that assert
// the hosted STAC ops demo shell opt back in with HONUA_TEST_HOSTED_BLAZOR_ASSETS=true (loaded
// through the resilient path below, which pre-creates any missing content roots).
var loadHostedBlazorStaticWebAssets = serveStacOpsDemo && !builder.Environment.IsDevelopment();
if (isTestEnvironment)
{
    loadHostedBlazorStaticWebAssets =
        builder.Configuration.GetValue("HONUA_TEST_HOSTED_BLAZOR_ASSETS", false);
}
if (loadHostedBlazorStaticWebAssets)
{
    StartupConfigurationHelpers.LoadHostedBlazorStaticWebAssets(builder);
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
var redisCacheEntitled = await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(
    builder.Configuration,
    builder.Environment);
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
            .MinimumLevel.Override("Honua.Infrastructure.Middleware.SecurityHeadersMiddleware", Serilog.Events.LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Infrastructure.Authentication.ApiKeyAuthenticationHandler", Serilog.Events.LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Protocols.Ogc.Api.Features.OgcFeaturesQueryHandler", Serilog.Events.LogEventLevel.Warning);
    }

    if (isDevelopment)
    {
        // Development: Human-readable console output
        config.WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
            formatProvider: System.Globalization.CultureInfo.InvariantCulture);
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
// Standalone Python/JS harnesses can opt back in with HONUA_REGISTER_TEST_INFRASTRUCTURE=true;
// a self-migrating standalone Test image that enables the Operate observability fixture also
// opts in automatically so the seed endpoint's providers resolve (honua-server#2350).
if (Honua.Server.Startup.TestInfrastructureRegistrationPolicy.ShouldRegisterInfrastructure(
        isTestEnvironment,
        registerInfrastructureInTestEnvironment,
        operateObservabilityFixtureEnabled,
        hostManagesOwnMigrations))
{
    InfrastructureCompositionRoot.RegisterInfrastructureServices(builder.Services, builder.Configuration);
}

// Provider-aware admin connection testing: register every engine's connection driver + the registry so the
// connection create/test endpoints build and probe MySQL / SQL Server / Oracle / PostgreSQL with the correct
// ADO.NET provider instead of always speaking Npgsql. Registered unconditionally — independent of the primary
// data-source provider — because any of these engines can be an external secured connection.
Honua.Postgres.Features.Security.PostgresConnectionDriverServiceCollectionExtensions.AddPostgresConnectionDriver(builder.Services);
Honua.MySql.Features.Security.MySqlConnectionDriverServiceCollectionExtensions.AddMySqlConnectionDriver(builder.Services);
Honua.SqlServer.Features.Security.SqlServerConnectionDriverServiceCollectionExtensions.AddSqlServerConnectionDriver(builder.Services);
Honua.Redshift.Features.Security.RedshiftConnectionDriverServiceCollectionExtensions.AddRedshiftConnectionDriver(builder.Services);
#if !HONUA_SKIP_ORACLE
// The Native AOT publish (HonuaSkipOracleForAotVerification) drops the Honua.Oracle
// ProjectReference and defines HONUA_SKIP_ORACLE, so this registration is compiled out
// (Oracle.ManagedDataAccess is not single-file/AOT safe — see Honua.Server.csproj).
Honua.Oracle.Features.Security.OracleConnectionDriverServiceCollectionExtensions.AddOracleConnectionDriver(builder.Services);
#endif
#if !HONUA_SKIP_SNOWFLAKE
// The Native AOT publish (HonuaSkipSnowflakeForAotVerification) drops the Honua.Snowflake
// ProjectReference and defines HONUA_SKIP_SNOWFLAKE, so this registration is compiled out
// (Snowflake.Data is not single-file/AOT safe — see Honua.Server.csproj).
Honua.Snowflake.Features.Security.SnowflakeConnectionDriverServiceCollectionExtensions.AddSnowflakeConnectionDriver(builder.Services);
#endif
builder.Services.AddSingleton<Honua.Core.Features.Security.Abstractions.IConnectionDriverRegistry, Honua.Core.Features.Security.Abstractions.ConnectionDriverRegistry>();

// IGeometryService is a pure NTS-backed compute service (its only dependency is
// IOptions<LimitsOptions>), not a data provider the WebAppFixture substitutes.
// RegisterInfrastructureServices — skipped in the Test environment so the
// fixture can swap the data providers — is its only registration site, which
// left every geometry-touching endpoint unable to resolve it under test. Pin it
// unconditionally with TryAdd so production keeps the single registration above
// and the test host gets it too.
builder.Services.TryAddSingleton<Honua.Core.Features.Geometry.Abstractions.IGeometryService,
    Honua.Infrastructure.Services.GeometryService>();

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
builder.Services.Configure<Honua.Infrastructure.Caching.QueryResultCacheOptions>(options =>
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

builder.Services.AddSingleton<Honua.Infrastructure.Caching.IQueryResultCacheManager,
    Honua.Infrastructure.Caching.QueryResultCacheManager>();

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
builder.Services.Configure<MetadataReleaseOperationOptions>(
    builder.Configuration.GetSection(MetadataReleaseOperationOptions.SectionName));
builder.Services.AddResilientHttpClient(
    "import-source",
    "import-source",
    HttpResiliencePolicies.SlowServiceDefaults,
    configureHandler: static () => Honua.Import.FileImport.ImportHttpClientHelper.CreatePinnedDnsHttpMessageHandler());
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
#if !HONUA_EXCLUDE_AZURE
builder.Services.AddResilientHttpClient(
    AzureBatchDataPlaneClient.HttpClientName,
    "control-plane-azure-batch",
    HttpResiliencePolicies.FastApiDefaults);
#endif
// ---- Extracted: control-plane deploy + batch-compute backends (Startup/BatchAndDeployBackendsRegistration.cs)
builder.Services.AddHonuaBatchAndDeployBackends();
// Substrate-neutral single-host rolling-replace proxy seams (ADR-0060). The embedded reverse proxy is
// only wired into the request pipeline when ControlPlane:SelfHosted:Enabled is true, so default
// deployments are untouched.
builder.Services.AddHonuaSelfHostedRollingProxy(builder.Configuration);
// ---- End extracted block

if (connectedRedis != null)
{
    // Control-plane reconcile graph (stores, four typed reconcilers, dispatcher seam, event handler,
    // trigger options). Shared with the cloud event entrypoint (Honua.ControlPlane.Lambda) via
    // ControlPlaneReconcileRegistration so both wire the exact same graph from one source of truth.
    // Phase 0: one dispatcher routes a reconcile-once request to the typed reconciler that owns the
    // operation kind. Both the poll loops and the Phase 1 event handler call this single method.
    // Phase 1: the event handler is the cloud (EventBridge -> Lambda) entrypoint, and the backstop
    // sweep self-heals dropped events; the TriggerMode flag chooses poll (on-prem) vs event (cloud).
    builder.Services.AddHonuaControlPlaneReconcilers(builder.Configuration);

    // Phase 3: the PERIODIC (bucket-b) scheduled-tick dispatcher routes a tick kind to the handler
    // that owns the matching background service's idempotent tick body. Handlers are contributed by
    // the owning assemblies (registered alongside each service above). Under TriggerMode=Poll the
    // in-process timers drive the ticks; under Event the timers are not hosted and EventBridge
    // Scheduler -> the scheduled-tick endpoint drives them through this dispatcher.
    builder.Services.AddSingleton<
        Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickDispatcher,
        Honua.ControlPlane.ScheduledTickDispatcher>();

    var controlPlaneTriggerMode = builder.Configuration
        .GetSection(Honua.ControlPlane.ControlPlaneTriggerOptions.SectionName)
        .GetValue<Honua.ControlPlane.ControlPlaneTriggerMode>("TriggerMode", Honua.ControlPlane.ControlPlaneTriggerMode.Poll);

    if (!isTestEnvironment)
    {
        // All reconcile families are mode-selected (Phase 1: execution jobs; Phase 2: deploy +
        // staged metadata/coordinated releases). Poll (default, on-prem/portable): the 5s loops run
        // exactly as before. Event (cloud): every 5s loop is disabled; the event handler (deploy
        // provider events + staged self-continue signals) plus the two backstop sweeps drive
        // reconciliation instead.
        if (controlPlaneTriggerMode == Honua.ControlPlane.ControlPlaneTriggerMode.Poll)
        {
            builder.Services.AddHostedService<DeployWorkflowReconcilerBackgroundService>();
            builder.Services.AddHostedService<ExecutionJobReconcilerBackgroundService>();
            builder.Services.AddHostedService<Honua.ControlPlane.MetadataReleaseReconcilerBackgroundService>();
            builder.Services.AddHostedService<Honua.ControlPlane.CoordinatedReleaseReconcilerBackgroundService>();
        }

        // The backstop sweeps ship in BOTH modes so a dropped/missed event (or, under poll, a wedged
        // loop) self-heals. They are low-frequency and are a no-op when the active operations are
        // fresh. The execution-job backstop covers Batch jobs; the workflow backstop covers the
        // deploy/metadata/coordinated operations and walks any wedged staged release forward.
        builder.Services.AddHostedService<Honua.ControlPlane.ExecutionJobBackstopSweepService>();
        builder.Services.AddHostedService<Honua.ControlPlane.WorkflowOperationBackstopSweepService>();

        // GP-plane observability spine (#2463): sample the execution-job store on a low-frequency
        // loop and publish per-(status, backend) queue depth for the honua.execution.queue.depth
        // gauge. Runs in both trigger modes since it only reads state to emit telemetry.
        builder.Services.AddHostedService<Honua.ControlPlane.ExecutionQueueDepthCollectorBackgroundService>();

        // Graduated ops-findings autonomy (#2557): a leased singleton evaluator that can
        // route auto-safe remediation findings back through the operation gateway. It is
        // only hosted when Redis/job-store coordination is active; no-Redis hosts stay inert.
        builder.Services.AddHostedService<Honua.Infrastructure.Monitoring.OpsFindingsAutonomyEvaluationService>();
    }

    // Agent-operation approval surface (#1692/#1693): durable proposal store +
    // shared operation gateway choke point + per-class executors.
    builder.Services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore,
        Honua.ControlPlane.RedisOperationProposalStore>();
    builder.Services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationGateway,
        Honua.ControlPlane.OperationGateway>();
    builder.Services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor,
        Honua.ControlPlane.Executors.DeployOperationExecutor>();
    builder.Services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor,
        Honua.ControlPlane.Executors.AdminConfigOperationExecutor>();
    // MetadataRelease executor is create-only BY DESIGN (#2563): rollback and the coordinated-release
    // approval-gate model stay endpoint/cockpit-driven — see MetadataReleaseOperationExecutor remarks.
    // Seed has NO executor here — it is enum-only on trunk with no runner to adapt (#2563); the gateway
    // continues to return NotSupported for it, and IOperationExecutorCatalog reports that truthfully.
    builder.Services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor,
        Honua.ControlPlane.Executors.MetadataReleaseOperationExecutor>();
    // Geoprocess executor (#2814): resumes an approved destructive/sink GP plan.
    // A gated GP submission is persisted as an AwaitingApproval proposal carrying the
    // serialized plan; approving it routes here to re-submit through the GP job
    // pipeline with the approval gate already satisfied (ADR-0064). Resolves the GP
    // job service lazily to avoid the gateway <-> job-service construction cycle.
    builder.Services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor,
        Honua.Geoprocessing.GeoprocessOperationExecutor>();
    // Executor-discovery capability surface (#2563): reflects the exact executors registered above so
    // discovery consumers (honua_supported_operation_kinds and the proposal compatibility field)
    // can never drift from routing reality.
    builder.Services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutorCatalog,
        Honua.ControlPlane.OperationExecutorCatalog>();
}

// Pending-approval notification channel (#1695): emitted from the gateway/store
// boundary, not per-endpoint. Safe to register regardless of Redis availability.
builder.Services.TryAddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IProposalNotifier,
    Honua.Server.Features.Admin.ProposalNotifier>();
// Real admin-config applier: the payload-discriminated ops-action registry (#2561).
// Executes self-healing actuators (alert redrive/pause/resume, bounded-admission tune),
// failing closed on unknown/malformed payloads. Replaces the previous logging stub.
builder.Services.TryAddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IAdminConfigChangeApplier,
    Honua.ControlPlane.Executors.OpsActionAdminConfigChangeApplier>();
// Per-action guardrail tiers for the ops-action discriminator (unknown actions Blocked).
builder.Services.TryAddSingleton<Honua.Core.Features.Guardrails.Abstractions.IOpsActionGuardrailCatalog,
    Honua.ControlPlane.Executors.OpsActionGuardrailCatalog>();
// Per-action auto-safe metadata for graduated autonomy policy checks.
builder.Services.TryAddSingleton<Honua.Core.Features.Guardrails.Abstractions.IOpsActionSafetyCatalog,
    Honua.ControlPlane.Executors.OpsActionSafetyCatalog>();
// Autonomous success is evidence-gated: the only auto-safe action must re-observe the live
// dispatch state across an observation window before the gateway records Succeeded (#2568).
builder.Services.AddScoped<Honua.ControlPlane.IAutonomousOperationConvergence,
    Honua.ControlPlane.Executors.AlertDispatchAutonomousOperationConvergence>();

// Configure tile options
InfrastructureCompositionRoot.ConfigureTileOptions(builder.Services, builder.Configuration);

// Configure caching options and register cache services
InfrastructureCompositionRoot.ConfigureCaching(builder.Services, builder.Configuration, redisCacheEntitled);

// Configure cloud file storage for imports and attachments
builder.Services.AddCloudFileStorage(builder.Configuration);

// Configure file upload security limits
builder.Services.Configure<FileUploadSecurityOptions>(
    builder.Configuration.GetSection(FileUploadSecurityOptions.SectionName));

// Federated-query planning and source configuration (#341).
// Experimental gate (PA-001): federated query is off by default. Set
// Experimental__Features__FederatedQuery=true to opt in.
if (builder.Configuration.GetValue<bool>("Experimental:Features:FederatedQuery", false))
{
    builder.Services.AddFederationServices(builder.Configuration);
}

// Register configuration validators to ensure application fails fast on invalid configuration
StartupConfigurationHelpers.RegisterConfigurationValidators(builder.Services);

// Register health check services
builder.Services.AddOptions<MigrationSafetyOptions>()
    .Bind(builder.Configuration.GetSection(MigrationSafetyOptions.SectionName));
builder.Services.AddSingleton<Honua.Infrastructure.Monitoring.MigrationState>();
builder.Services.AddSingleton<Honua.Infrastructure.Monitoring.MigrationBackupHookState>();
builder.Services.TryAddSingleton<IDatabaseMigrationBackupHookRecorder,
    Honua.Infrastructure.Monitoring.AuditingMigrationBackupHookRecorder>();
builder.Services.AddSingleton<Honua.Infrastructure.Monitoring.DatabaseCompatibilityState>();

// Degraded-start resilience (#1632): when enabled, transient DB-unavailability at startup does
// not crash the process; the host serves non-DB routes and recovers connectivity in the background.
var startupResilienceOptions = builder.Configuration
    .GetSection(Honua.Core.Configuration.StartupResilienceOptions.SectionName)
    .Get<Honua.Core.Configuration.StartupResilienceOptions>()
    ?? new Honua.Core.Configuration.StartupResilienceOptions();
// Env-friendly override (HONUA_DB_DEGRADED_START=true) for serverless presets.
if (builder.Configuration.GetValue<bool?>("HONUA_DB_DEGRADED_START") is { } envDegradedStart)
{
    startupResilienceOptions = startupResilienceOptions with { DegradedStartEnabled = envDegradedStart };
}

builder.Services.AddSingleton(startupResilienceOptions);
builder.Services.AddSingleton<Honua.Infrastructure.Monitoring.DegradedStartupContext>();
builder.Services.AddHostedService<Honua.Infrastructure.Monitoring.DatabaseRecoveryBackgroundService>();
builder.Services.AddScoped<Honua.Infrastructure.Monitoring.IDeployPreflightProbe,
    Honua.Infrastructure.Monitoring.DeployPreflightProbe>();
builder.Services.AddScoped<Honua.Server.Features.HealthCheck.IReadinessCheckService,
    Honua.Server.Features.HealthCheck.ReadinessCheckService>();
builder.Services.AddProductionHealthChecks(builder.Configuration);

// ---- Extracted: licensing + identity-provider HTTP clients (Startup/LicensingRegistration.cs)
builder.Services.AddHonuaLicensing(builder.Configuration, builder.Environment);
// ---- End extracted block

// Edition guardrail ladder (#1691): resolves DirectExecute/RequiresApproval/Blocked
// per (operation class x edition). Fails closed for unknown classes outside dev.
builder.Services.Configure<Honua.Core.Features.Guardrails.GuardrailLadderOptions>(
    builder.Configuration.GetSection(Honua.Core.Features.Guardrails.GuardrailLadderOptions.SectionName));
if (!builder.Environment.IsDevelopment())
{
    builder.Services.PostConfigure<Honua.Core.Features.Guardrails.GuardrailLadderOptions>(
        options => options.FailClosed = true);
}

builder.Services.AddSingleton<Honua.Core.Features.Guardrails.Abstractions.IGuardrailLadder,
    Honua.Core.Features.Guardrails.DefaultGuardrailLadder>();

// Register configuration documentation service for self-documenting admin endpoint
builder.Services.AddScoped<Honua.Server.Features.Admin.Services.ConfigurationDocumentationService>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.TryAddScoped<IConsoleJobService, ConsoleJobService>();
builder.Services.TryAddSingleton<IShareExportDestinationResolver, UnsupportedShareExportDestinationResolver>();
builder.Services.TryAddSingleton<IShareExportStore, InMemoryShareExportStore>();
builder.Services.TryAddSingleton<IShareTrafficStore, InMemoryShareTrafficStore>();

// Register control plane IAM in-memory defaults. Uses TryAdd so a durable provider
// implementation registered earlier (e.g. PostgresRoleStore from AddPostgreSqlServices)
// wins — otherwise the later default would shadow it and grants would not persist (#1575).
builder.Services.AddInMemoryControlPlaneIamDefaults();
// Canonical per-operation permission resolver (#1375): the shared authorization
// seam over EffectivePermissions. Scoped so it can consume the (scoped) Postgres
// role store when durable RBAC is active.
builder.Services.AddScoped<Honua.Core.Features.Authorization.Abstractions.IPermissionResolver,
    Honua.Core.Features.Authorization.PermissionResolver>();
// Row-level security (#502, epic #1275). In-memory policy store as the TryAdd default
// (PostgresRlsPolicyStore wins when durable storage is active), plus the request-scoped
// filter source that translates the caller's roles/claims + per-layer policies into the
// parameterized predicate AND-ed into queries across every protocol.
builder.Services.TryAddScoped<Honua.Core.Features.Authorization.Abstractions.IRlsPolicyStore,
    Honua.Server.Features.Admin.Services.InMemoryRlsPolicyStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Honua.Core.Features.Authorization.Abstractions.IRowLevelSecurityFilterSource,
    Honua.Infrastructure.Authentication.RowLevelSecurityFilterSource>();
// Field-level security (column masking) (#1940), the field-level companion to RLS.
// In-memory policy store as the TryAdd default (PostgresFieldMaskPolicyStore wins when
// durable storage is active), plus the request-scoped source that resolves the caller's
// roles + per-layer policies into the set of attributes masked from query output across
// every protocol and output format.
builder.Services.TryAddScoped<Honua.Core.Features.Authorization.Abstractions.IFieldMaskPolicyStore,
    Honua.Server.Features.Admin.Services.InMemoryFieldMaskPolicyStore>();
builder.Services.AddScoped<Honua.Core.Features.Authorization.Abstractions.IFieldMaskSource,
    Honua.Infrastructure.Authentication.FieldMaskSource>();
builder.Services.AddSingleton<Honua.Infrastructure.Authentication.IAdminApiKeyStore>(sp =>
    new Honua.Infrastructure.Authentication.InMemoryAdminApiKeyStore(sp.GetService<TimeProvider>()));
// Embed governance (#1191): authoritative embed key issuance/scoping, policy
// evaluation, rate accounting, and redacted analytics ingestion. In-memory
// defaults; durable providers can replace these via TryAdd later.
builder.Services.AddSingleton<Honua.Core.Features.EmbedGovernance.Abstractions.IEmbedKeyStore>(sp =>
    new Honua.Core.Features.EmbedGovernance.InMemoryEmbedKeyStore(sp.GetService<TimeProvider>()));
builder.Services.AddSingleton<Honua.Core.Features.EmbedGovernance.Abstractions.IEmbedAnalyticsStore>(
    new Honua.Core.Features.EmbedGovernance.InMemoryEmbedAnalyticsStore());
// v1 metadata-resource / manifest-approval / gitops-watch admin surface removed in #1035 cutover.
// V2 admin UX (epic #1046) edits the canonical MetadataV2Graph document directly via IMetadataV2GraphStore.

// Console metadata v2 content + RBAC baseline (#1162). Persistent store lands in #1163.
builder.Services.AddSingleton<Honua.Core.Features.Console.Abstractions.IConsoleContentStore>(sp =>
    new Honua.Server.Features.Console.Services.InMemoryConsoleContentStore(
        sp.GetService<TimeProvider>() ?? TimeProvider.System));
builder.Services.AddScoped<Honua.Core.Features.Console.Abstractions.IConsoleActionEvaluator,
    Honua.Server.Features.Console.Services.ConsoleActionEvaluator>();
// Console Share access: public-link + embed state (#1215). In-memory store
// shares the persistent-store follow-on (#1163); validator depends on the
// content + share stores to walk the provenance closure.
builder.Services.AddSingleton<Honua.Core.Features.Console.Abstractions.IConsoleShareStore>(sp =>
    new Honua.Server.Features.Console.Services.InMemoryConsoleShareStore(
        sp.GetService<TimeProvider>() ?? TimeProvider.System));
builder.Services.AddScoped<Honua.Core.Features.Console.Abstractions.IConsoleDependencyClosureValidator,
    Honua.Server.Features.Console.Services.ConsoleDependencyClosureValidator>();
// Console open-data DCAT + STAC publication state (#1214). In-memory store shares
// the persistent-store follow-on (#1163) with the content/share stores.
builder.Services.AddSingleton<Honua.Core.Features.Console.Abstractions.IConsoleOpenDataStore>(sp =>
    new Honua.Server.Features.Console.Services.InMemoryConsoleOpenDataStore(
        sp.GetService<TimeProvider>() ?? TimeProvider.System));
// Console catalog discovery-endpoints registry read model (#1279). The discovery
// dialects a server publishes are a server-wide config/metadata concern; this
// config-backed read model materialises them into the Console projection. A
// durable/metadata-v2-backed source can replace this registration later.
builder.Services.AddSingleton<Honua.Server.Features.Console.Services.ICatalogDiscoveryRegistryStore>(
    _ => new Honua.Server.Features.Console.Services.ConfigCatalogDiscoveryRegistryStore());

// Content publication registry for Studio-generated maps/dashboards/reports/apps (#1183).
// In-memory store is the default; Postgres registration (AddPostgreSqlServices) overrides
// it with durable storage when Postgres is active.
Honua.Core.Features.Publishing.Content.ContentPublishingServiceCollectionExtensions.AddContentPublishingServices(builder.Services);

// Register shared Infrastructure services
builder.Services.AddScoped<Honua.Infrastructure.Services.IGeometryConverter,
    Honua.Infrastructure.Services.GeometryConverter>();
builder.Services.AddScoped<ILayerStyleService, LayerStyleService>();
builder.Services.AddScoped<Honua.Core.Features.Styling.Abstractions.IOgcStyleProjection,
    Honua.Server.Features.Styling.OgcStyleProjection>();
builder.Services.AddSingleton<Honua.Core.Features.Styling.Abstractions.IGeoServicesStyleConverter,
    Honua.Server.Features.Styling.GeoServicesStyleConverter>();
builder.Services.AddSingleton<Honua.Core.Features.Styling.Abstractions.ISldStyleConverter,
    Honua.Server.Features.Styling.Sld.SldStyleConverter>();
builder.Services.AddStyleSuggestionCore();

// Configure temporary file service for image exports
builder.Services.Configure<Honua.Infrastructure.Services.TemporaryFileOptions>(
    builder.Configuration.GetSection(Honua.Infrastructure.Services.TemporaryFileOptions.SectionName));
builder.Services.AddSingleton<Honua.Infrastructure.Services.FileSystemTemporaryFileService>();
builder.Services.AddSingleton<Honua.Infrastructure.Services.ITemporaryFileService,
    Honua.Infrastructure.Services.CloudBackedTemporaryFileService>();

// Temporary-file cleanup is a PERIODIC tick (bucket-b). Its cleanup deletes expired temp files via a
// fresh scope and is idempotent, so the scheduled-tick handler is registered in BOTH trigger modes;
// the in-process 30-minute timer is hosted only under TriggerMode=Poll (default, on-prem), keeping
// that path byte-for-byte unchanged.
builder.Services.TryAddSingleton<Honua.Infrastructure.Services.TemporaryFileCleanupService>();
builder.Services.AddSingleton<
    Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler,
    Honua.Infrastructure.Services.TemporaryFileCleanupScheduledTickHandler>();
if (Honua.Core.Features.ControlPlane.Abstractions.ControlPlaneTriggerModeResolver
    .ShouldHostInProcessTimers(builder.Configuration))
{
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<Honua.Infrastructure.Services.TemporaryFileCleanupService>());
}

// Register shared validation services
builder.Services.AddValidationServices();

// Register feature services (FeatureServer, OGC, OData, Observability)
builder.Services.AddServerFeatures(builder.Configuration);
builder.Services.AddOperateObservabilityFixtures(builder.Configuration, builder.Environment);
builder.Services.AddWorkflowPackages();
builder.Services.AddOperationsToolset(builder.Configuration);
// #2483 (ADR-0056 Increment 4): publish validated operations-toolset descriptors as
// first-class MCP tools. Off unless Mcp:PublishOperations:Enabled=true; wired after the
// operations toolset so the tool source can resolve the canonical IOperationCatalog.
Honua.Ai.Protocols.Mcp.McpServiceCollectionExtensions.AddMcpPublishedOperationTools(
    builder.Services, builder.Configuration);
builder.Services.AddAdminRealtime(builder.Configuration);
if (!isTestEnvironment)
{
    builder.Services.AddOrchestrationBackgroundServices(builder.Configuration);
}

builder.Services.AddSingleton<Honua.Protocols.GeoServices.FeatureServer.DistributedReplicaStore>(sp =>
    new Honua.Protocols.GeoServices.FeatureServer.DistributedReplicaStore(
        sp.GetService<IDistributedCache>(),
        sp.GetRequiredService<ILogger<Honua.Protocols.GeoServices.FeatureServer.DistributedReplicaStore>>()));
// Replica/change-tracking services are provider-specific: Postgres registers concrete
// implementations; DuckDB and MySQL (both read-only) register no-op stubs via their own
// AddXxxServices extensions. Skip the Postgres registration for those providers so the
// stubs are not overwritten with an implementation that would issue Postgres SQL against
// a non-Postgres connection.
var replicaProvider = DataProviderNames.Normalize(
    builder.Configuration.GetValue<string>("DataSource:Provider"));
if (replicaProvider != DataProviderNames.DuckDb &&
    replicaProvider != DataProviderNames.MySql)
{
    builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IReplicaRepository>(sp =>
        new Honua.Postgres.Features.FeatureStore.Services.PostgresReplicaRepository(
            sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider>()));
    builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IReplicaConflictRepository>(sp =>
        new Honua.Postgres.Features.FeatureStore.Services.PostgresReplicaConflictRepository(
            sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider>()));
    builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IChangeTracker>(sp =>
        new Honua.Postgres.Features.FeatureStore.Services.PostgresChangeTracker(
            sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider>()));
    // Temporal history store (#1166 slices 2-5): reads the uncollapsed change log with attribution.
    // Overrides the Core no-op fallback registered by AddTemporalHistory. Read-only/non-Postgres
    // providers keep the no-op store (history unsupported), matching the no-op change tracker.
    builder.Services.AddScoped<Honua.Core.Features.Temporal.Abstractions.ITemporalHistoryStore>(sp =>
        new Honua.Postgres.Features.FeatureStore.Services.PostgresTemporalHistoryStore(
            sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider>()));
    // Branch-versioning manager (#1272 Track B, ADR-0051) — Postgres-only; read-only/non-Postgres
    // providers register the NoOp stub (SupportsVersioning=false) in their ServiceCollectionExtensions.
    builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IVersionManager>(sp =>
        new Honua.Postgres.Features.FeatureStore.Services.PostgresVersionManager(
            sp.GetRequiredService<Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider>(),
            schemaName: null,
            versionLock: sp.GetRequiredService<Honua.Core.Features.FeatureStore.Abstractions.IVersionLock>()));
}

// Branch-versioning durable lock + async job runtime (#1553). The Redis-backed version lock serializes
// reconcile/post/resolve per (service, version) across replicas; the Redis-backed job store makes the
// async reconcile/post job pollable and restart-durable. Both degrade to single-node in-process/in-memory
// fallbacks when Redis is not configured. The job runner wraps the synchronous reconcile/post engine in a
// durable, pollable job and is provider-agnostic (it adapts to the registered IVersionManager). These are
// registered for every provider so the version-management endpoints can resolve them uniformly; for
// non-Postgres providers the NoOp version manager rejects reconcile/post, so the lock/store/runner stay
// inert.
builder.Services.AddSingleton<Honua.Core.Features.FeatureStore.Abstractions.IVersionLock>(sp =>
    new Honua.Infrastructure.Coordination.RedisVersionLock(
        sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
        sp.GetRequiredService<ILogger<Honua.Infrastructure.Coordination.RedisVersionLock>>()));
builder.Services.AddSingleton<Honua.Core.Features.FeatureStore.Abstractions.IVersionJobStore>(sp =>
    new Honua.Infrastructure.Coordination.RedisVersionJobStore(
        sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
        sp.GetRequiredService<ILogger<Honua.Infrastructure.Coordination.RedisVersionJobStore>>()));
builder.Services.AddSingleton<Honua.Core.Features.FeatureStore.Abstractions.IVersionJobRunner,
    Honua.Core.Features.FeatureStore.Services.VersionJobRunner>();
builder.Services.AddScoped<Honua.Protocols.GeoServices.FeatureServer.IReplicaStore>(sp =>
    new Honua.Protocols.GeoServices.FeatureServer.Services.CachingReplicaStore(
        sp.GetRequiredService<Honua.Protocols.GeoServices.FeatureServer.DistributedReplicaStore>(),
        sp.GetRequiredService<Honua.Core.Features.FeatureStore.Abstractions.IReplicaRepository>()));
// Canonical replica-upload synchronization pipeline (#1272). Conflict detection runs against the
// provider's change log; conflict-record writes use the durable conflict store when supported and
// otherwise fall back to last-write-wins. Available for all providers since IChangeTracker and
// IReplicaConflictRepository are always registered (read-only providers register no-op stubs).
builder.Services.AddScoped<Honua.Core.Features.FeatureStore.Abstractions.IReplicaSyncService,
    Honua.Core.Features.FeatureStore.Services.ReplicaSyncService>();

// ---- Extracted: import/export job managers, migration evidence, tile operations
//      (Startup/ImportExportTileOperationsRegistration.cs)
builder.Services.AddHonuaImportExportAndTileOperations(builder.Configuration);
// ---- End extracted block

// ---- Per-area geocoder/router build jobs (provisioner GP-on-Batch jobs)
//      (Features/Provisioner/ProvisionerServiceCollectionExtensions.cs)
builder.Services.AddHonuaProvisionerBuildJobs(builder.Configuration);
// ---- End block


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

// Enterprise identity: SCIM 2.0 provisioning (#510) and SAML 2.0 SP-initiated SSO (#508).
// Both adapt into the existing identity/role model: SCIM provisions users + maps groups to
// roles; SAML consumes a signed assertion and establishes a Honua session.
builder.Services.AddEnterpriseIdentity(builder.Configuration);
// Configure security headers
builder.Services.AddSecurityHeaders(builder.Configuration);
// Configure security audit log sink (#1144)
builder.Services.AddHonuaAuditLog();
// SIEM export connectors (Splunk HEC / Microsoft Sentinel / S3 / syslog) + audit retention and
// data-residency controls (issue #2157). Off by default (AuditLog:Export:Enabled=false); registers
// the dispatcher (retry/backoff + dead-letter), residency guard, retention pruner, and any enabled
// push sinks. Pull-based export + tamper-evidence (#2112) remain the baseline.
Honua.Server.Features.Infrastructure.AuditLog.Export.AuditExportServiceCollectionExtensions
    .AddHonuaAuditExport(builder.Services, builder.Configuration);
// Scheduled audit hash-chain verifier (#2810): periodically replays the tamper-evident audit chain
// and publishes the result as a signal so a broken link raises a paged health fault / ops finding
// instead of being caught only on a manual /verify. On by default (AuditLog:ChainVerification:Enabled).
Honua.Server.Features.Infrastructure.AuditLog.AuditChainVerificationServiceCollectionExtensions
    .AddAuditChainVerification(builder.Services, builder.Configuration);
// Configure tenant context resolution rail (#1144). Defaults are bound from
// the MultiTenancy configuration section; the inline callback is the wiring
// point for environment-specific overrides.
builder.Services.AddHonuaTenantContext(builder.Configuration, _ => { });
// Configure schema-per-tenant routing + usage metering rail (#346). Disabled by
// default (MultiTenancy:SchemaRouting:Enabled=false) so single-tenant deployments
// retain byte-identical behavior; registration only adds the resolver/meter seam.
builder.Services.AddHonuaTenantSchemaRouting(builder.Configuration);
// Tenant lifecycle/provisioning + billing wiring (issue #2156). Provisioning is opt-in: the
// catalog starts empty so single-tenant deployments are unchanged until tenants are created
// through the admin surface. Registers the catalog, lifecycle service, billing sink, and exporter.
builder.Services.AddHonuaTenantLifecycle();
// Configure CORS policies
builder.Services.AddCorsPolicies(builder.Configuration, builder.Environment);
builder.Services.AddInputValidation(builder.Configuration);
// App-level rate limiting services (issue #355). Off by default — operators opt in via
// RateLimiting:Enabled. Registers options validation, the policy store backing the admin
// surface, and the partitioned middleware. Edge enforcement (ADR-0004) remains the
// baseline; this is the opt-in identity-aware (tenant/user/API-key) enterprise slice.
builder.Services.AddRateLimiting(builder.Configuration);

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
builder.Services.AddHonuaJsonContexts();

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
            await Honua.Infrastructure.Models.ProblemDetailsHelpers.CreateAdminProblem(
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
        .GetRequiredService<IOptions<Honua.Infrastructure.Authentication.ApiKeyAuthenticationOptions>>()
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
        Honua.Infrastructure.Authentication.AuthenticationLog
            .DevelopmentBypassActiveAtStartup(app.Logger, environmentName);
    }
    else if (devAuthRequested)
    {
        Honua.Infrastructure.Authentication.AuthenticationLog
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

// The admin web UI lives in the separate `honua-console` repo and is deployed
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
    // Protocol-overview document so the explorer represents Honua's FULL protocol
    // surface. Scalar can only list protocols that publish an OpenAPI description
    // (the OGC API family + STAC + Admin); the standards that self-describe through
    // their own discovery documents (Esri GeoServices, OData, WFS/WMS/WMTS) would
    // otherwise be invisible here, making the switcher read as the complete set —
    // which it is not. This default landing lists every supported protocol and
    // links the self-describing ones to their native discovery endpoints.
    var protocolsOverviewDescription = """
        # Honua speaks many protocols

        Honua serves **one dataset through many open geospatial protocols** — use the one your client already speaks. This reference documents the protocols that publish an **OpenAPI** description; the standards below describe themselves through their **own** discovery documents (by design — they are spec-compliant), so each links out to its native endpoint.

        ## Explore in this reference (OpenAPI)
        Use the document switcher (top-left) to open any of these:
        - **OGC API — Features**, **Tiles**, **Maps**, **Coverages**, **Styles**, **Processes**
        - **STAC API**
        - **Admin API**

        ## Also supported — standards-native discovery
        Fully supported; each describes itself through its own standard endpoint:
        - **Esri GeoServices REST** — FeatureServer · MapServer · ImageServer · GeocodeServer · GPServer → [`/rest/services`](/rest/services?f=json)
        - **OData v4** → [`/odata/$metadata`](/odata/$metadata)
        - **OGC WFS 2.0** → [`GetCapabilities`](/wfs?service=WFS&request=GetCapabilities)
        - **OGC WMS** → [`GetCapabilities`](/wms?service=WMS&request=GetCapabilities)
        - **OGC WMTS** → [`GetCapabilities`](/wmts?service=WMTS&request=GetCapabilities)

        Plus **MapLibre-native** vector/raster/GeoJSON sources composed client-side by the Honua SDK.

        > This is the full protocol surface. The document switcher only lists the OpenAPI-described protocols; the standards above are reached through their own discovery documents.
        """;
    var protocolsOverviewDoc = new
    {
        openapi = "3.0.3",
        info = new
        {
            title = "Honua — supported protocols",
            version = "1.0.0",
            description = protocolsOverviewDescription,
        },
        paths = new Dictionary<string, object>(),
    };
    app.MapGet("/docs/protocols.openapi.json", () => Results.Json(protocolsOverviewDoc));

    app.MapScalarApiReference("/docs", options =>
    {
        options
            .WithTitle("Honua API Explorer")
            .WithTheme(ScalarTheme.BluePlanet)
            .AddDocument("overview", "All protocols", "/docs/protocols.openapi.json", isDefault: true)
            .AddDocument("features", "OGC API Features", "/openapi.json")
            .AddDocument("coverages", "OGC API Coverages", "/ogc/coverages/openapi.json")
            .AddDocument("tiles", "OGC API Tiles", "/ogc/tiles/openapi.json")
            .AddDocument("maps", "OGC API Maps", "/ogc/maps/openapi.json")
            .AddDocument("styles", "OGC API Styles", "/ogc/styles/openapi.json")
            .AddDocument("processes", "OGC API Processes", "/ogc/processes/openapi.json")
            .AddDocument("stac", "STAC API", "/stac/openapi.json")
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
    // PA-059: The default Serilog request-logging message template includes
    // {RequestPath} (path only, no query string), which means sensitive query
    // parameters such as "password" and "token" on /sharing/rest/generateToken
    // are never written to the application log. Set this explicitly so that
    // any future template change goes through a deliberate code review rather
    // than silently inheriting a new format that might expose credentials.
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        // PA-059: Query-string parameters are intentionally excluded from every
        // diagnostic property below. For the generateToken endpoint in particular,
        // "password" and "token" values in the URL must never reach log sinks.
        // The RequestPath property above carries only the path component.
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

// Add CORS middleware before the exception handler so error responses (4xx/5xx) carry
// Access-Control-Allow-Origin headers; otherwise browsers report every server error as
// a CORS failure, masking the real status/body from web clients (#1627). It also stays
// after UseGrpcWeb (preserving the gRPC-Web preflight ordering) and before auth so it can
// answer preflight requests.
app.UseHonuaCors(app.Environment);

// Add global exception handling middleware after request logging.
app.UseGlobalExceptionHandling();

// Emit the Esri/GeoServices error envelope for routing-level 404/405 (and
// 406/415/501) terminations under /rest that would otherwise return a bodyless
// status. Scoped to GeoServices paths so OGC/STAC/OData/admin contracts are
// untouched. Runs after global exception handling so thrown exceptions keep their
// existing protocol shaping and only status-only responses are re-shaped here.
app.UseRestErrorEnvelope();

// A configured deployment profile is a fail-closed HTTP surface allowlist backed by the
// drift-gated feature catalog. With no profile configured this middleware is inert.
Honua.Server.Features.Capabilities.DeploymentCapabilityProfileApplicationBuilderExtensions
    .UseDeploymentCapabilityProfile(app);

// Validate query, form, and selected header inputs before authentication and endpoint execution.
app.UseInputValidation();

// Validate optional/required client certificates before the regular auth stack so
// required mTLS surfaces can return machine-readable errors instead of TLS handshakes.
app.UseHonuaClientCertificateAuthentication();

// Add authentication and authorization middleware early to short-circuit unauthorized requests
app.UseApiKeyAuthentication();

// Bridge ArcGIS-style portal tokens (?token=, X-Esri-Authorization, Authorization: Bearer)
// for requests that the default scheme did not authenticate. Must run after
// UseAuthentication (inside UseApiKeyAuthentication) and before tenant resolution
// so the tenant middleware sees the hydrated principal claims (#1241).
app.UsePortalTokenAuthentication();

// Resolve tenant context immediately after authentication so claims (and the
// X-Honua-Tenant override header) are evaluated against the resolved principal
// before any downstream feature handler reads ITenantContext (#1144).
app.UseHonuaTenantContext();

// Route the resolved tenant to its PostgreSQL schema and record a usage signal (#346).
// No-op unless MultiTenancy:SchemaRouting:Enabled=true. Must run after tenant context
// resolution and before any feature handler that reads the database.
app.UseHonuaTenantSchemaRouting();

// Block requests for a suspended/deleted tenant (issue #2156). Runs after tenant context
// resolution. A no-op for tenants not present in the catalog, so the default pipeline is
// unchanged until tenants are provisioned through the admin surface.
app.UseHonuaTenantStatusEnforcement();

// Audit-log middleware records security-relevant request outcomes. It runs after
// auth so the audit actor is the authenticated principal, and before endpoint
// execution so 401/403/5xx responses are still observed (#1144).
app.UseHonuaAuditLog();

// App-level rate limiting (issue #355). Runs after authentication and tenant resolution
// so buckets partition by tenant + authenticated user/API-key identity (falling back to
// source IP for anonymous traffic). No-ops unless RateLimiting:Enabled is set; the MVP
// posture is still edge enforcement (ADR-0004).
app.UseRateLimiting();

// Add limits enforcement middleware (after auth, before request logging)
app.UseLimitsEnforcement();

// Map public demo service/layer contract IDs to internal seeded layer IDs and guard demo writes.
app.UseCloudDemoServiceLayerAliases();
app.UseCloudDemoWritableFeatureGuard();

// Enable output caching middleware
app.UseOutputCache();

// Log application startup
var appVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
Honua.Infrastructure.Logging.Log.ApplicationStarting(app.Logger,
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
app.MapShareAdminEndpoints();
app.MapAdminRealtimeHub();

// Configure layer publishing endpoints
app.MapLayerPublishingEndpoints();

// Configure service settings endpoints (protocol toggles + MapServer config)
app.MapServiceSettingsEndpoints();

// Configure admin metadata version/manifest endpoints
// v1 admin endpoint mappings removed in #1035 cutover; V2 admin UX (#1046) lives elsewhere.
app.MapMetadataReleaseEndpoints();
app.MapMetadataReleaseOperationEndpoints();
app.MapMetadataReleaseControlEndpoints();
app.MapCoordinatedReleaseControlEndpoints();

// Phase 3: internal, token-guarded scheduled-tick endpoint for EventBridge Scheduler. Mapped only
// when ControlPlane:TriggerMode=Event; a no-op (route absent) under Poll (default, on-prem).
app.MapScheduledTickEndpoints(builder.Configuration);
app.MapMetadataPrevalidationEndpoints();
app.MapDeployControlEndpoints();

// Cloud event-driven control-plane surface (TriggerMode=Event only): the EventBridge-invoked
// reconcile Lambda + EventBridge Scheduler backstop post here. No-op under poll (on-prem).
app.MapControlPlaneEventEndpoints();

// Console approval surface for agent-proposed operations (#1694).
app.MapProposalEndpoints();

// Consolidated ops-health snapshot + deterministic ops-findings engine (ADR-0060 WS4 / #2457).
app.MapOpsObservabilityEndpoints();

// Server-authoritative aggregated operational status + read-only ops scope (A12).
app.MapOperateStatusEndpoints();

// Configure admin layer style endpoints
app.MapAdminLayerStyleEndpoints();
app.MapAdminLayerFieldConfigurationEndpoints();
app.MapAdminLayerAuthoringEndpoints();
app.MapAdminLayerFilterConfigurationEndpoints();
app.MapReplicaManagementEndpoints();
app.MapAdminLayerValidationEndpoints();
app.MapAdminStyleSuggestionEndpoints();
app.MapAdminSldStyleEndpoints();

// Configure admin alerting zone/rule endpoints
app.MapAlertAdminEndpoints();

// Configure alert dispatch self-healing ops endpoints (dead-letter redrive, channel pause/resume) (#2561)
app.MapAlertOpsAdminEndpoints();

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
app.MapGeoprocessingUsageEndpoints();
app.MapFeatureOverviewEndpoints();

// Rate limit policy administration (issue #355): CRUD + status for tenant/user/API-key
// quota policies. Admin-authorized; the policies are consumed by the rate limiting
// middleware registered above.
app.MapRateLimitEndpoints();

// Tenant lifecycle/provisioning administration (issue #2156): create/suspend/resume/delete plus
// the per-tenant billing usage export. Admin-authorized.
app.MapTenantAdminEndpoints();

// Configure compliance admin endpoints (SOC 2 / FedRAMP readiness, key rotation, report export) (#352)
app.MapComplianceAdminEndpoints();

// Configure secure connection management endpoints
app.MapSecureConnectionEndpoints();
// Experimental gate (PA-001): only map federation endpoints when the feature is enabled.
if (app.Configuration.GetValue<bool>("Experimental:Features:FederatedQuery", false))
{
    app.MapFederationEndpoints();
}
app.MapClientCertificateAdminEndpoints();

// Configure control plane IAM endpoints (#511)
app.MapLicenseEndpoints();
app.MapOidcProviderEndpoints();
app.MapUserManagementEndpoints();
app.MapRoleEndpoints();
app.MapRlsPolicyEndpoints();
app.MapFieldMaskPolicyEndpoints();

// Enterprise identity provisioning + SSO (#510 SCIM 2.0, #508 SAML 2.0)
app.MapScimEndpoints();
app.MapSamlEndpoints();

// Configure Console metadata v2 content + RBAC endpoints (#1162)
app.MapConsoleSessionEndpoints();
app.MapConsoleContentEndpoints();
app.MapConsoleActionEndpoints();
// Console Share access public-link + embed API (#1215)
app.MapConsoleShareEndpoints();
app.MapCatalogDiscoveryEndpoints();
app.MapConsoleSharePublicEndpoints();
// Console open-data DCAT + STAC publication API (#1214)
app.MapConsoleOpenDataEndpoints();
app.MapConsoleOpenDataPublicEndpoints();
app.MapStudioPackageEndpoints();
app.MapStudioMapCollaborationEndpoints();
app.MapWorkflowPackageEndpoints();
app.MapOperationsEndpoints();
Honua.Server.Features.Console.Publications.ContentPublicationEndpoints.MapContentPublicationEndpoints(app);
Honua.Server.Features.Console.Publications.PublishedRouteEndpoints.MapPublishedRouteEndpoints(app);
app.MapAdminApiKeyEndpoints();
Honua.Server.Features.Admin.EmbedGovernance.EmbedGovernanceEndpoints.MapEmbedGovernanceEndpoints(app);
Honua.Server.Features.Admin.EmbedGovernance.EmbedPolicyEndpoints.MapEmbedPolicyEndpoints(app);
app.MapOAuthClientEndpoints();
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
app.MapGrpcService<Honua.Geoprocessing.HonuaProcessService>();
app.MapGrpcService<Honua.Server.Features.Spec.HonuaSpecService>();
app.MapGrpcService<Honua.Scene.Grpc.HonuaSceneGrpcService>();
app.MapGrpcService<Honua.Scene.Grpc.HonuaTileGrpcService>();
app.MapGrpcService<Honua.Scene.Grpc.HonuaElevationGrpcService>();
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

// Configure footprint-driven batch import orchestration endpoints (issue #1253)
app.MapMigrationBatchEndpoints();

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

// Configure Esri tile/vector-tile cache package import + serving endpoints (#1269)
app.MapTileCachePackageEndpoints();

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

// Substrate-neutral single-host rolling-replace front proxy (ADR-0060). Mapped only when the
// self-hosted backend is enabled; the catch-all proxy route only handles paths not claimed by an
// explicit endpoint, so the control-plane API and health endpoints keep their normal precedence.
if (Honua.ControlPlane.SelfHostedRollingProxyRegistration.IsSelfHostedProxyEnabled(builder.Configuration))
{
    app.MapReverseProxy();
}

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
    var migrationState = app.Services.GetRequiredService<Honua.Infrastructure.Monitoring.MigrationState>();

    if (builder.Configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS"))
    {
        Honua.Infrastructure.Logging.Log.DatabaseMigrationsSkipped(app.Logger);
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
        Honua.Infrastructure.Logging.Log.DatabaseConnectionStringNotConfigured(app.Logger);

        if (app.Environment.IsProduction())
        {
            Honua.Infrastructure.Logging.Log.DatabaseConnectionStringMissingInProduction(app.Logger);
        }

        migrationState.MarkSkipped("No database connection string configured.");
        return;
    }

    // The migration runner is provider-specific: only the active durable provider
    // registers one (Postgres today via AddPostgreSqlServices). It is absent when the
    // infrastructure composition root is skipped — e.g. the Test environment, where an
    // external harness/fixture swaps the data providers — and for embedded/read-through
    // providers that own no migratable schema. Resolve it optionally and skip gracefully,
    // exactly as RunPostGisPreflightCheckAsync does for IDatabaseCompatibilityChecker,
    // instead of crashing startup on an unresolved GetRequiredService.
    var migrationRunner = app.Services.GetService<IDatabaseMigrationRunner>();
    if (migrationRunner is null)
    {
        Honua.Infrastructure.Logging.Log.DatabaseMigrationsSkipped(app.Logger);
        migrationState.MarkSkipped("No database migration runner registered for the active data provider.");
        return;
    }

    Honua.Infrastructure.Logging.Log.DatabaseMigrationsStarting(app.Logger);
    migrationState.MarkRunning("Applying database migrations.");

    try
    {
        var result = await migrationRunner.RunMigrationsAsync(
            connectionString,
            Assembly.GetExecutingAssembly(),
            app.Lifetime.ApplicationStopping);

        if (!result.Successful)
        {
            var errorMessage = result.ErrorMessage ?? "Database migration failed.";
            var error = result.Error ?? new InvalidOperationException(errorMessage);
            Honua.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, errorMessage, error);
            migrationState.MarkFailed("Database migrations failed.");

            // In non-Development environments, re-throw so the app fails to start
            // (gives a clear CrashLoopBackOff signal in Kubernetes) — unless degraded
            // start is enabled and the failure is transient connectivity (#1632).
            if (!app.Environment.IsDevelopment()
                && !TryEnterDegradedStart("migrations", error, migrationsPending: true))
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                return; // unreachable; satisfies the compiler
            }

            return;
        }

        var scriptCount = result.AppliedScripts.Count;
        if (scriptCount > 0)
        {
            Honua.Infrastructure.Logging.Log.DatabaseMigrationsCompleted(app.Logger, scriptCount);
            // Log individual script names for debugging
            foreach (var script in result.AppliedScripts)
            {
                Honua.Infrastructure.Logging.Log.MigrationScriptApplied(app.Logger, script);
            }

            migrationState.MarkSucceeded($"Applied {scriptCount} migration script(s).");
        }
        else
        {
            Honua.Infrastructure.Logging.Log.NoDatabaseMigrationsToApply(app.Logger);
            migrationState.MarkSucceeded("No pending migration scripts.");
        }
    }
    catch (Exception ex)
    {
        Honua.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, ex.Message, ex);
        migrationState.MarkFailed("Database migrations failed.");

        // In non-Development environments, re-throw so the app fails to start
        // (gives a clear CrashLoopBackOff signal in Kubernetes) — unless degraded
        // start is enabled and the failure is transient connectivity (#1632).
        if (!app.Environment.IsDevelopment()
            && !TryEnterDegradedStart("migrations", ex, migrationsPending: true))
        {
            throw;
        }
    }
}

// Degraded-start gate (#1632): returns true when the host should keep running despite a
// startup database failure, rather than crashing the process (which on AWS Lambda becomes a
// Runtime.ExitError crash loop that 500s every route, including non-database routes). Only
// transient connectivity failures are suppressed, and only when degraded start is enabled;
// genuine misconfiguration still fails loudly. When suppressed, the degraded context is armed
// so the background recovery loop retries connectivity and flips readiness once recovered.
bool TryEnterDegradedStart(string phase, Exception error, bool migrationsPending)
{
    var resilienceOptions = app.Services.GetRequiredService<Honua.Core.Configuration.StartupResilienceOptions>();
    if (!resilienceOptions.DegradedStartEnabled)
    {
        return false;
    }

    if (!Honua.Core.Features.Infrastructure.Resilience.StartupDatabaseResilience.IsTransientConnectivityError(error))
    {
        // Real misconfiguration (bad migration SQL, incompatible PostGIS): never mask it.
        return false;
    }

    var warning = Honua.Core.Features.Infrastructure.Resilience.StartupDatabaseResilience
        .BuildDegradedStartWarning(phase, error.Message);
    Honua.Infrastructure.Logging.Log.DatabaseStartupDegraded(app.Logger, warning);

    var degradedContext = app.Services.GetRequiredService<Honua.Infrastructure.Monitoring.DegradedStartupContext>();
    degradedContext.MarkDegraded(migrationsPending, Assembly.GetExecutingAssembly());
    return true;
}

// PostGIS preflight compatibility check
async Task RunPostGisPreflightCheckAsync()
{
    var compatibilityState = app.Services.GetRequiredService<Honua.Infrastructure.Monitoring.DatabaseCompatibilityState>();

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
        Honua.Infrastructure.Logging.Log.PostGisPreflightCheckSkipped(app.Logger);
        return;
    }

    Honua.Infrastructure.Logging.Log.PostGisPreflightCheckStarting(app.Logger);

    Honua.Core.Features.Infrastructure.Domain.DatabaseCompatibilityResult result;
    try
    {
        result = await checker.CheckCompatibilityAsync(connectionString, app.Lifetime.ApplicationStopping);
    }
    catch (Exception ex) when (!app.Environment.IsDevelopment())
    {
        // The preflight could not reach the database to check compatibility. If degraded start is
        // enabled and the failure is transient connectivity, arm degraded mode (migrations are run
        // after this method, so they are still pending) rather than crashing the process (#1632).
        if (TryEnterDegradedStart("PostGIS preflight", ex, migrationsPending: true))
        {
            return;
        }

        throw;
    }

    compatibilityState.SetResult(result);

    if (result.IsCompatible)
    {
        Honua.Infrastructure.Logging.Log.PostGisPreflightCheckPassed(
            app.Logger, result.EngineVersion, result.PostGisVersion ?? "unknown");
        return;
    }

    var errorMessage = result.ErrorMessage ?? "Database compatibility check failed.";

    if (!app.Environment.IsDevelopment())
    {
        Honua.Infrastructure.Logging.Log.PostGisPreflightCheckFailedCritical(app.Logger, errorMessage);
        throw new InvalidOperationException($"PostGIS preflight check failed: {errorMessage}");
    }

    Honua.Infrastructure.Logging.Log.PostGisPreflightCheckFailedDevelopment(app.Logger, errorMessage);
}
