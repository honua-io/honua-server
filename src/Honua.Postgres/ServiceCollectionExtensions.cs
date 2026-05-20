// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.AutoDocs;
using Honua.Core.Features.Import;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.Admin;
using Honua.Postgres.Features.Alerts;
using Honua.Postgres.Features.Attachments;
using Honua.Postgres.Features.Catalog;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.Geometry;
using Honua.Postgres.Features.GeometryService;
using Honua.Postgres.Features.HealthCheck;
using Honua.Postgres.Features.Import;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Infrastructure.Caching;
using Honua.Postgres.Features.Infrastructure.Crs;
using Honua.Postgres.Features.Infrastructure.Migrations;
using Honua.Postgres.Features.Infrastructure.Transforms;
using Honua.Postgres.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Styling;
using Honua.Postgres.Features.Metadata;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Postgres.Features.Mobile.FieldCollection;
using Honua.Postgres.Features.Raster;
using Honua.Postgres.Features.Security;
using Honua.Postgres.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace Honua.Postgres;

/// <summary>
/// Dependency injection extensions for PostgreSQL services
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add PostgreSQL services including feature store and health checking
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration to get connection string from</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddPostgreSqlServices(this IServiceCollection services, IConfiguration configuration)
    {
        var schemaHeadersEnabled = configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");
        var connectionLimits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);

        var defaultSchema = configuration["Database:Schema"];
        var schemaConfiguration = PostgresSchemaConfiguration.FromConfiguration(configuration);
        services.TryAddSingleton(schemaConfiguration);

        // Register concurrency gate as singleton — shared across all scoped providers.
        // Factory form so the DI container tracks the IDisposable for shutdown disposal.
        services.TryAddSingleton(_ => new QueryConcurrencyGate(connectionLimits));

        // Register NpgsqlDataSource as specified in Issue #3
        services.TryAddSingleton<NpgsqlDataSource>(serviceProvider =>
        {
            var connectionString = ResolveConnectionString(serviceProvider, configuration);
            return PostgresDataSourceFactory.Create(connectionString, schemaHeadersEnabled, connectionLimits, defaultSchema);
        });

        // Register refactored feature store implementation
        services.AddRefactoredFeatureStore(configuration["Database:Schema"]);
        services.TryAddScoped<IFeatureDataProviderRegistry>(serviceProvider =>
            new FeatureDataProviderRegistry(serviceProvider.GetServices<IFeatureDataProvider>()));
        services.TryAddScoped(serviceProvider =>
            new FeatureProviderBindingResolver(
                serviceProvider.GetRequiredService<ISecureConnectionRegistry>(),
                serviceProvider.GetRequiredService<IFeatureDataProviderRegistry>(),
                DataProviderNames.Postgis));
        services.TryAddScoped<FeatureProviderQueryRouter>();

        // Register raster store implementation
        services.AddPostgresRasterStore(configuration["Database:Schema"]);

        // Register alert persistence services
        services.AddScoped<IAlertChangeReader, PostgresAlertChangeReader>();
        services.AddScoped<IAlertRuleRepository, PostgresAlertRuleRepository>();
        services.AddScoped<IAlertStateStore, PostgresAlertStateStore>();
        services.AddScoped<IAlertEventStore, PostgresAlertEventStore>();
        services.AddScoped<IAlertDispatchStore, PostgresAlertDispatchStore>();
        services.AddScoped<IAlertCheckpointStore, PostgresAlertCheckpointStore>();
        services.AddScoped<IAlertAdminStore, PostgresAlertAdminStore>();

        // Register database performance metrics provider
        services.AddScoped<IDatabasePerformanceMetricsProvider, PostgresDatabasePerformanceMetricsProvider>();

        // Register H3 capability checker (scoped to access scoped IDatabaseConnectionProvider;
        // the result is cached statically after the first successful check, assuming single-database deployment)
        services.AddScoped<IH3CapabilityChecker, PostgresH3CapabilityChecker>();

        // Register attachment store implementation (metadata tables live in the honua schema)
        services.AddScoped<IAttachmentStore>(serviceProvider =>
            new PostgresAttachmentStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                serviceProvider.GetRequiredService<ICloudFileStorage>(),
                serviceProvider.GetRequiredService<ILogger<PostgresAttachmentStore>>(),
                schemaName: string.IsNullOrWhiteSpace(configuration["Attachments:Schema"])
                    ? "honua"
                    : configuration["Attachments:Schema"]));

        // Register layer catalog implementation
        services.AddScoped<ILayerCatalog, PostgresLayerCatalog>();
        services.AddScoped<ILayerFieldConfigurationStore, PostgresLayerFieldConfigurationStore>();
        services.AddScoped<IServiceMetadataUpdater, PostgresServiceMetadataUpdater>();
        services.AddScoped<ILayerMetadataUpdater, PostgresLayerMetadataUpdater>();

        // Register FieldCollection mobile sync store (#894)
        services.AddScoped<IFieldCollectionSyncStore, PostgresFieldCollectionSyncStore>();

        // Register metadata resource store (ADR-0023)
        services.AddScoped<IMetadataResourceStore>(serviceProvider =>
            new PostgresMetadataResourceStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                serviceProvider.GetService<Honua.Core.Features.Caching.Abstractions.ICacheService>(),
                configuration["Database:Schema"]));

        // Register manifest version store for GitOps drift detection (#515)
        services.AddScoped<IManifestVersionStore>(serviceProvider =>
            new PostgresManifestVersionStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));

        // Register manifest pending change store for approval workflows
        services.AddScoped<IManifestPendingChangeStore>(serviceProvider =>
            new PostgresManifestPendingChangeStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));

        // Register GitOps watch store for git repository watching (#518)
        services.AddScoped<IGitOpsWatchStore>(serviceProvider =>
            new PostgresGitOpsWatchStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));

        // Register layer style catalog for MapLibre/GeoServices styling
        services.AddScoped<ILayerStyleCatalog>(serviceProvider =>
            new PostgresLayerStyleCatalog(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));

        // Register field profiling service for style suggestions (#400)
        services.AddScoped<IFieldProfilingService>(serviceProvider =>
            new PostgresFieldProfilingService(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                serviceProvider.GetRequiredService<ILogger<PostgresFieldProfilingService>>(),
                configuration["Database:Schema"]));

        // Register table discovery implementation
        services.AddScoped<ITableDiscoveryService, PostgreSqlTableDiscoveryService>();

        // Register layer publishing implementation
        services.AddScoped<ILayerPublishingService, PostgreSqlLayerPublishingService>();

        // Register health checker
        services.AddScoped<IDatabaseHealthChecker, PostgresDatabaseHealthChecker>();

        // Register migration runner for schema upgrades
        services.AddSingleton<IDatabaseMigrationRunner, PostgresDatabaseMigrationRunner>();

        // Register database compatibility checker for PostGIS preflight validation
        services.AddSingleton<IDatabaseCompatibilityChecker, PostgresDatabaseCompatibilityChecker>();

        // Register topology validator for geometry operations
        services.AddScoped<IGeometryTopologyValidator, PostgresGeometryTopologyValidator>();

        // Register anomaly detection
        services.AddScoped<Core.Features.AnomalyDetection.Abstractions.IAnomalyAnalyzer,
            Honua.Postgres.Features.AnomalyDetection.PostgresAnomalyAnalyzer>();

        // Register geometry operation service for buffer/simplify/project
        services.AddScoped<IGeometryOperationService, PostgresGeometryOperationService>();

        // Register SQL filter translator
        services.AddScoped<ISqlFilterTranslator>(_ => new PostgresSqlFilterTranslator(
            useJsonAttributes: true,
            attributesColumn: "attributes",
            geometryColumn: "geometry",
            primaryKeyColumn: "objectid"));

        // PERFORMANCE OPTIMIZATION: Register query cache configuration
        services.Configure<QueryCacheOptions>(configuration.GetSection("Database:QueryCache"));
        if (schemaHeadersEnabled)
        {
            services.Configure<QueryCacheOptions>(options => options.EnableAutomaticCaching = false);
        }

        // PERFORMANCE OPTIMIZATION: Register prepared statement cache as singleton
        // Singleton ensures cache persistence across requests for optimal performance
        services.AddSingleton<PreparedStatementCache>();
        services.AddSingleton<IPreparedStatementCacheStatisticsProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<PreparedStatementCache>());

        // PERFORMANCE OPTIMIZATION: Register high-frequency query preparation service
        // Pre-prepares known frequently-used queries for optimal initial performance
        services.AddHostedService<HighFrequencyQueryPreparationService>();

        // Register enhanced database connection provider with prepared statement caching
        services.AddScoped<IDatabaseConnectionProvider, CachingDatabaseConnectionProvider>();

        // Register CRS detection service
        services.AddScoped<ICrsDetectionService>(serviceProvider =>
            new CrsDetectionService(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<CrsDetectionService>()));
        services.AddScoped<ICrsRegistry, PostgresCrsRegistry>();
        services.AddScoped<ICoordinateTransformService, PostGisCoordinateTransformService>();

        // Register CRS warmup service with leader election for distributed deployments
        services.AddSingleton<IDistributedLeaderElection>(serviceProvider =>
        {
            // Use the no-op implementation here; distributed leader election is only enabled
            // when the Server composition root supplies a real coordinator.
            return new Honua.Postgres.Features.Infrastructure.Coordination.NoOpDistributedLeaderElection(
                "honua:leader:crs-warmup");
        });

        services.AddSingleton<PostgresCrsWarmupService>(serviceProvider =>
            new PostgresCrsWarmupService(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<ILogger<PostgresCrsWarmupService>>(),
                serviceProvider.GetRequiredService<IDistributedLeaderElection>()));

        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresCrsWarmupService>());

        // Register import limits configuration
        services.AddSingleton(serviceProvider =>
        {
            var section = configuration.GetSection("Import:Limits");
            var limits = new Core.Features.Import.Domain.ImportLimits();

            if (section.Exists())
            {
                limits = new Core.Features.Import.Domain.ImportLimits
                {
                    BatchSize = ParsePositiveIntOrDefault(section["BatchSize"], limits.BatchSize),
                    MaxMemoryBytes = ParsePositiveLongOrDefault(section["MaxMemoryBytes"], limits.MaxMemoryBytes),
                    BackgroundJobThresholdBytes = ParsePositiveLongOrDefault(
                        section["BackgroundJobThresholdBytes"],
                        limits.BackgroundJobThresholdBytes),
                    MaxPreviewSizeBytes = ParsePositiveLongOrDefault(section["MaxPreviewSizeBytes"], limits.MaxPreviewSizeBytes),
                    MaxPreviewFeatures = ParsePositiveIntOrDefault(section["MaxPreviewFeatures"], limits.MaxPreviewFeatures),
                    MaxPreviewCountScan = ParsePositiveIntOrDefault(section["MaxPreviewCountScan"], limits.MaxPreviewCountScan),
                    StreamBufferSize = ParsePositiveIntOrDefault(section["StreamBufferSize"], limits.StreamBufferSize),
                    UseTransactions = bool.TryParse(section["UseTransactions"], out var useTransactions) ? useTransactions : limits.UseTransactions,
                    ContinueOnError = bool.TryParse(section["ContinueOnError"], out var continueOnError) ? continueOnError : limits.ContinueOnError,
                    MaxFeaturesPerFile = ParseNonNegativeIntOrDefault(section["MaxFeaturesPerFile"], limits.MaxFeaturesPerFile),
                    MaxArchiveEntryBytes = ParsePositiveLongOrDefault(section["MaxArchiveEntryBytes"], limits.MaxArchiveEntryBytes),
                    MaxArchiveExtractedBytes = ParsePositiveLongOrDefault(
                        section["MaxArchiveExtractedBytes"],
                        limits.MaxArchiveExtractedBytes),
                    MaxArchiveCompressionRatio = ParsePositiveDoubleOrDefault(
                        section["MaxArchiveCompressionRatio"],
                        limits.MaxArchiveCompressionRatio)
                };
            }

            return limits;
        });

        // Register streaming file import service with memory-efficient batch processing
        services.AddScoped<IFileImportService>(serviceProvider =>
        {
            var limits = serviceProvider.GetRequiredService<Core.Features.Import.Domain.ImportLimits>();
            var performanceMonitor = serviceProvider.GetRequiredService<IPerformanceMonitor>();
            var logger = serviceProvider.GetRequiredService<ILogger<StreamingFileImportService>>();
            var cloudStorage = serviceProvider.GetService<Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage>();

            return new StreamingFileImportService(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                serviceProvider.GetRequiredService<ICrsDetectionService>(),
                serviceProvider.GetRequiredService<IFileFormatDetectionService>(),
                performanceMonitor,
                logger,
                limits,
                cloudStorage,
                serviceProvider.GetRequiredService<PostgresSchemaConfiguration>());
        });

        // Register universal import job service using unified progress store
        // This replaces the in-memory job service with one that uses centralized progress tracking
        services.AddSingleton<IImportJobService>(serviceProvider =>
        {
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var progressStore = serviceProvider.GetRequiredService<IUniversalProgressStore>();
            var performanceMonitor = serviceProvider.GetRequiredService<IPerformanceMonitor>();
            var logger = serviceProvider.GetRequiredService<ILogger<UniversalImportJobService>>();
            return new UniversalImportJobService(scopeFactory, progressStore, performanceMonitor, logger);
        });

        // Register ArcGIS REST client for Geoservices service imports with resilience
        services.AddResilientHttpClient<ArcGisRestClient>(
            "arcgis-rest",
            HttpResiliencePolicies.SlowServiceDefaults,
            configureClient: client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(5);
            },
            configureHandler: static () => ArcGisRestClient.CreatePinnedDnsHttpMessageHandler())
            .AddHttpMessageHandler<MigrationRequestCountingHandler>();

        // Register Geoservices import service
        services.AddScoped<IGeoservicesImportService, GeoservicesImportService>();

        // Register Core-level services via their own extensions
        services.AddImportSuggestionsCore();
        services.AddAutoDocsCore();

        // Register GeoServer REST client for GeoServer migration imports with resilience
        services.TryAddTransient<MigrationRequestCountingHandler>();
        services.AddResilientHttpClient<GeoServerRestClient>(
            "geoserver-rest",
            HttpResiliencePolicies.SlowServiceDefaults,
            configureClient: client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(5);
            },
            configureHandler: static () => GeoServerRestClient.CreatePinnedDnsHttpMessageHandler())
            .AddHttpMessageHandler<MigrationRequestCountingHandler>();

        // Register migration catalog writer used by apply-mode imports.
        services.AddScoped<IMigrationCatalogWriter, PostgresMigrationCatalogWriter>();

        // Register ArcGIS migration evidence store (#1025 slice 6). Replaces the Core
        // in-memory default so admin endpoints can serve persisted manifest + parity
        // artifacts across server restarts and across instances.
        services.RemoveAll<IArcGisMigrationEvidenceStore>();
        services.AddScoped<IArcGisMigrationEvidenceStore>(serviceProvider =>
            new PostgresArcGisMigrationEvidenceStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                configuration["Database:Schema"]));

        // Register migration run catalog (issue #1015 slice 5). Resolved as a
        // scoped service so each request opens its own short-lived connection;
        // the catalog is owned by the admin orchestration endpoints and the
        // apply path.
        services.AddScoped<IMigrationRunCatalog>(serviceProvider =>
        {
            var connectionString = ResolveConnectionString(serviceProvider, configuration);
            var logger = serviceProvider.GetRequiredService<ILogger<PostgresMigrationRunCatalog>>();
            return new PostgresMigrationRunCatalog(connectionString, logger);
        });

        // Register migration performance evidence store (#1033 slice 5). Resolved alongside
        // the JsonTypeInfo<MigrationPerformanceEvidenceArtifact> the Server registers from
        // its source-generated context so persistence stays AOT-safe. TryAdd so a host that
        // bypasses Postgres registration retains the in-memory fallback default.
        services.TryAddScoped<IMigrationPerformanceEvidenceStore, PostgresMigrationPerformanceEvidenceStore>();

        // Register GeoServer import service
        services.AddScoped<IGeoServerImportService, GeoServerImportService>();

        // Register OGC API Features collection import (#1029 slice 2) with its Postgres sink.
        services.AddScoped<IOgcApiFeaturesCollectionSink>(serviceProvider =>
            new PostgresOgcApiFeaturesCollectionSink(
                serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                serviceProvider.GetRequiredService<ILogger<PostgresOgcApiFeaturesCollectionSink>>()));
        services.AddResilientHttpClient<OgcApiFeaturesImportService>(
            "ogc-api-features-import",
            HttpResiliencePolicies.SlowServiceDefaults,
            configureClient: client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(10);
            },
            configureHandler: static () => OgcApiFeaturesImportService.CreatePinnedDnsHttpMessageHandler());
        services.AddScoped<IOgcApiFeaturesImportService>(serviceProvider =>
            serviceProvider.GetRequiredService<OgcApiFeaturesImportService>());

        // Register OGC service migration scanner for WFS inventory and WMS/WMTS manual-review planning.
        services.AddResilientHttpClient<OgcServiceMigrationScanner>(
            "ogc-service-migration",
            HttpResiliencePolicies.SlowServiceDefaults,
            configureClient: client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(2);
            },
            configureHandler: static () => OgcServiceMigrationScanner.CreatePinnedDnsHttpMessageHandler())
            .AddHttpMessageHandler<MigrationRequestCountingHandler>();
        services.AddScoped<IOgcServiceMigrationScanner>(serviceProvider =>
            serviceProvider.GetRequiredService<OgcServiceMigrationScanner>());

        // Register OGC WFS data import service (#1016 slice 2). Reuses the OGC service migration
        // scanner above for inventory + manifest planning; here we add a named HttpClient used
        // for paged GetFeature requests and the import service itself.
        services.AddResilientHttpClient(
            OgcWfsImportService.HttpClientName,
            "ogc-wfs-import",
            HttpResiliencePolicies.SlowServiceDefaults,
            configureClient: client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(5);
            },
            configureHandler: static () => OgcServiceMigrationScanner.CreatePinnedDnsHttpMessageHandler());
        services.AddScoped<IOgcWfsImportService>(serviceProvider =>
        {
            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            return new OgcWfsImportService(
                serviceProvider.GetRequiredService<IOgcServiceMigrationScanner>(),
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                httpClientFactory.CreateClient(OgcWfsImportService.HttpClientName),
                serviceProvider.GetRequiredService<ILogger<OgcWfsImportService>>(),
                serviceProvider.GetService<PostgresSchemaConfiguration>());
        });

        // Register secure connection management services
        services.AddSecureConnectionServices(configuration);
        services.UseSecureConnectionProvider(configuration);

        return services;
    }

    private static string ResolveConnectionString(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("DefaultConnection connection string is required for PostgreSQL services");
        }

        var resolver = serviceProvider.GetService<IConnectionSecretResolver>();
        if (resolver == null)
        {
            return connectionString;
        }

        try
        {
            var canResolve = resolver.CanResolve(connectionString);
            if (!canResolve)
            {
                return connectionString;
            }

            var resolved = resolver.ResolveSecretAsync(connectionString, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return resolved ?? connectionString;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to resolve DefaultConnection via secret provider.", ex);
        }
    }

    private static int ParsePositiveIntOrDefault(string? value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private static int ParseNonNegativeIntOrDefault(string? value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : defaultValue;
    }

    private static long ParsePositiveLongOrDefault(string? value, long defaultValue)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private static double ParsePositiveDoubleOrDefault(string? value, double defaultValue)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }
}
