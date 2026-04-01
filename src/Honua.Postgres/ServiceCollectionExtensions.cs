// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.AutoDocs;
using Honua.Core.Features.Import;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Abstractions;
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
using Honua.Postgres.Features.Infrastructure.Styling;
using Honua.Postgres.Features.Styling;
using Honua.Postgres.Features.Metadata;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Postgres.Features.Raster;
using Honua.Postgres.Features.Security;
using Honua.Postgres.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

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

        // Register NpgsqlDataSource as specified in Issue #3
        services.TryAddSingleton<NpgsqlDataSource>(serviceProvider =>
        {
            var connectionString = ResolveConnectionString(serviceProvider, configuration);
            return PostgresDataSourceFactory.Create(connectionString, schemaHeadersEnabled, connectionLimits);
        });

        // Register refactored feature store implementation
        services.AddRefactoredFeatureStore(configuration["Database:Schema"]);

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
        services.AddScoped<IServiceMetadataUpdater, PostgresServiceMetadataUpdater>();
        services.AddScoped<ILayerMetadataUpdater, PostgresLayerMetadataUpdater>();

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
        services.AddHostedService<PostgresCrsWarmupService>();

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
                performanceMonitor,
                logger,
                limits,
                cloudStorage);
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

        // Register ArcGIS REST client for Geoservices service imports
        services.AddHttpClient<ArcGisRestClient>()
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(static () => ArcGisRestClient.CreatePinnedDnsHttpMessageHandler());

        // Register Geoservices import service
        services.AddScoped<IGeoservicesImportService, GeoservicesImportService>();

        // Register Core-level services via their own extensions
        services.AddImportSuggestionsCore();
        services.AddAutoDocsCore();

        // Register GeoServer REST client for GeoServer migration imports
        services.AddHttpClient<GeoServerRestClient>()
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(5);
            });

        // Register GeoServer import service
        services.AddScoped<IGeoServerImportService, GeoServerImportService>();
        services.AddScoped<IGeoServerMigrationManifestService, GeoServerMigrationManifestService>();

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
            var canResolve = resolver.CanResolveSecretAsync(connectionString, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!canResolve)
            {
                return connectionString;
            }

            return resolver.ResolveConnectionStringAsync(connectionString, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
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
