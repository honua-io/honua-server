// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Honua.Core.Features.Infrastructure.ServiceRegistration.ValidationPatterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace Honua.Postgres;

/// <summary>
/// Consolidated dependency injection extensions for PostgreSQL services.
/// Demonstrates the consolidation framework reducing ~150 lines to ~50 lines.
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add PostgreSQL services using consolidated registration patterns.
    /// </summary>
    public static IServiceCollection AddPostgreSqlServices(this IServiceCollection services, IConfiguration configuration)
    {
        var schemaName = configuration["Database:Schema"];
        var connectionLimits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);
        var schemaHeadersEnabled = configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");

        // Register core infrastructure using consolidated patterns
        RegisterCoreInfrastructure(services, configuration, connectionLimits, schemaHeadersEnabled, schemaName);

        // Register feature stores using consolidated patterns
        RegisterFeatureStores(services, schemaName);

        // Register business services using consolidated patterns
        RegisterBusinessServices(services, configuration, schemaName);

        // Register external integrations using consolidated patterns
        RegisterExternalIntegrations(services);

        return services;
    }

    private static void RegisterCoreInfrastructure(
        IServiceCollection services,
        IConfiguration configuration,
        QueryConnectionLimits connectionLimits,
        bool schemaHeadersEnabled,
        string? schemaName)
    {
        // Register infrastructure singletons using consolidated patterns
        services
            .AddSingletonService(_ => new QueryConcurrencyGate(connectionLimits))
            .AddSingletonService<NpgsqlDataSource>(serviceProvider =>
            {
                var connectionString = ResolveConnectionString(serviceProvider, configuration);
                return PostgresDataSourceFactory.Create(connectionString, schemaHeadersEnabled, connectionLimits, schemaName);
            })
            .AddSingletonService<PreparedStatementCache>()
            .AddSingletonService<IPreparedStatementCacheStatisticsProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<PreparedStatementCache>());

        // Register performance optimizations
        services
            .AddPerformanceOptimizedObjectPools()
            .AddHostedService<HighFrequencyQueryPreparationService>();

        // Register enhanced database connection provider
        services.AddScopedService<IDatabaseConnectionProvider, CachingDatabaseConnectionProvider>();

        // Configure query cache
        services.Configure<QueryCacheOptions>(configuration.GetSection("Database:QueryCache"));
        if (schemaHeadersEnabled)
        {
            services.Configure<QueryCacheOptions>(options => options.EnableAutomaticCaching = false);
        }
    }

    private static void RegisterFeatureStores(IServiceCollection services, string? schemaName)
    {
        // Register feature store services using consolidated patterns
        services.AddRefactoredFeatureStore(schemaName);
        services.AddPostgresRasterStore(schemaName);

        // Register feature store interfaces using segregated interface pattern
        services.AddFeatureStoreServices<PostgresFeatureStoreRefactored>(schemaName,
            typeof(IFeatureReader),
            typeof(IFeatureWriter),
            typeof(ITileProvider),
            typeof(IRelationshipStore),
            typeof(IGeoJsonFeatureStore),
            typeof(IGeobufFeatureStore),
            typeof(IGmlFeatureStore),
            typeof(IKmlFeatureStore),
            typeof(IStreamingFeatureStore));
    }

    private static void RegisterBusinessServices(IServiceCollection services, IConfiguration configuration, string? schemaName)
    {
        // Register alert services using consolidated database-dependent pattern
        services.AddDatabaseDependentServices(
            (typeof(IAlertChangeReader), typeof(PostgresAlertChangeReader), ServiceLifetime.Scoped),
            (typeof(IAlertRuleRepository), typeof(PostgresAlertRuleRepository), ServiceLifetime.Scoped),
            (typeof(IAlertStateStore), typeof(PostgresAlertStateStore), ServiceLifetime.Scoped),
            (typeof(IAlertEventStore), typeof(PostgresAlertEventStore), ServiceLifetime.Scoped),
            (typeof(IAlertDispatchStore), typeof(PostgresAlertDispatchStore), ServiceLifetime.Scoped),
            (typeof(IAlertCheckpointStore), typeof(PostgresAlertCheckpointStore), ServiceLifetime.Scoped),
            (typeof(IAlertAdminStore), typeof(PostgresAlertAdminStore), ServiceLifetime.Scoped));

        // Register catalog services using schema-based pattern
        services.AddPostgresFeatureServices<PostgresLayerCatalog, ILayerCatalog>(schemaName);
        services.AddPostgresFeatureServices<PostgresServiceMetadataUpdater, IServiceMetadataUpdater>(schemaName);
        services.AddPostgresFeatureServices<PostgresLayerMetadataUpdater, ILayerMetadataUpdater>(schemaName);

        // Register metadata and manifest services with schema pattern
        RegisterSchemaBasedServices(services, configuration, schemaName);

        // Register CRS and spatial services
        RegisterSpatialServices(services, configuration);

        // Register import services with configuration parsing
        RegisterImportServices(services, configuration, schemaName);
    }

    private static void RegisterSchemaBasedServices(IServiceCollection services, IConfiguration configuration, string? schemaName)
    {
        // Use schema-based service pattern for metadata services
        services
            .AddSchemaBasedService<IMetadataResourceStore, PostgresMetadataResourceStore>(schemaName)
            .AddSchemaBasedService<IManifestVersionStore, PostgresManifestVersionStore>(schemaName)
            .AddSchemaBasedService<IManifestPendingChangeStore, PostgresManifestPendingChangeStore>(schemaName)
            .AddSchemaBasedService<IGitOpsWatchStore, PostgresGitOpsWatchStore>(schemaName)
            .AddSchemaBasedService<ILayerStyleCatalog, PostgresLayerStyleCatalog>(schemaName)
            .AddSchemaBasedService<IFieldProfilingService, PostgresFieldProfilingService>(schemaName);

        // Register attachment store with configuration-based schema
        services.AddScopedService<IAttachmentStore>(serviceProvider =>
        {
            var attachmentSchema = string.IsNullOrWhiteSpace(configuration["Attachments:Schema"])
                ? "honua"
                : configuration["Attachments:Schema"];

            return new PostgresAttachmentStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                serviceProvider.GetRequiredService<ICloudFileStorage>(),
                serviceProvider.GetRequiredService<ILogger<PostgresAttachmentStore>>(),
                attachmentSchema);
        });
    }

    private static void RegisterSpatialServices(IServiceCollection services, IConfiguration configuration)
    {
        // Register CRS services using consolidated patterns
        services
            .AddScopedService<ICrsDetectionService, CrsDetectionService>()
            .AddScopedService<ICrsRegistry, PostgresCrsRegistry>()
            .AddScopedService<ICoordinateTransformService, PostGisCoordinateTransformService>()
            .AddScopedService<IH3CapabilityChecker, PostgresH3CapabilityChecker>();

        // Register geometry services
        services
            .AddScopedService<IGeometryTopologyValidator, PostgresGeometryTopologyValidator>()
            .AddScopedService<IGeometryOperationService, PostgresGeometryOperationService>()
            .AddScopedService<Core.Features.AnomalyDetection.Abstractions.IAnomalyAnalyzer,
                Honua.Postgres.Features.AnomalyDetection.PostgresAnomalyAnalyzer>();

        // Register distributed leader election and CRS warmup
        RegisterDistributedServices(services);

        // Register SQL filter translator with configuration
        services.AddScopedService<ISqlFilterTranslator>(_ => new PostgresSqlFilterTranslator(
            useJsonAttributes: true,
            attributesColumn: "attributes",
            geometryColumn: "geometry",
            primaryKeyColumn: "objectid"));
    }

    private static void RegisterDistributedServices(IServiceCollection services)
    {
        // Register leader election using consolidated singleton pattern
        services.AddSingletonService<IDistributedLeaderElection>(serviceProvider =>
        {
            var redis = serviceProvider.GetService<IConnectionMultiplexer>();
            var logger = serviceProvider.GetRequiredService<ILogger<RedisDistributedLeaderElection>>();
            return new RedisDistributedLeaderElection("honua:leader:crs-warmup", redis, logger);
        });

        // Register CRS warmup service
        services
            .AddSingletonService<PostgresCrsWarmupService>()
            .AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PostgresCrsWarmupService>());
    }

    private static void RegisterImportServices(IServiceCollection services, IConfiguration configuration, string? schemaName)
    {
        // Parse import limits using consolidated configuration parsing
        var importLimits = ConfigurationParsing.ParseConfigurationSection<Core.Features.Import.Domain.ImportLimits>(
            configuration.GetSection("Import:Limits"),
            new Core.Features.Import.Domain.ImportLimits(),
            ParseImportLimitsCustom);

        services.AddSingleton(importLimits);

        // Register import services with schema pattern
        services
            .AddSchemaBasedService<IFileImportService, StreamingFileImportService>(schemaName)
            .AddSingletonService<IImportJobService, UniversalImportJobService>();

        // Register core import features
        services.AddImportSuggestionsCore();
        services.AddAutoDocsCore();
    }

    private static void RegisterExternalIntegrations(IServiceCollection services)
    {
        // Register HTTP client-based services using consolidated pattern
        services
            .AddResilientHttpClientService<ArcGisRestClient, IGeoservicesImportService>(
                "arcgis-rest",
                client =>
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                    client.Timeout = TimeSpan.FromMinutes(5);
                },
                ArcGisRestClient.CreatePinnedDnsHttpMessageHandler)
            .AddResilientHttpClientService<GeoServerRestClient, IGeoServerImportService>(
                "geoserver-rest",
                client =>
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                    client.Timeout = TimeSpan.FromMinutes(5);
                });

        // Register secure connection services
        services.AddSecureConnectionServices(configuration);
        services.UseSecureConnectionProvider(configuration);
    }

    private static void ParseImportLimitsCustom(Core.Features.Import.Domain.ImportLimits limits, IConfigurationSection section)
    {
        limits.BatchSize = ConfigurationParsing.ParsePositiveIntOrDefault(section["BatchSize"], limits.BatchSize);
        limits.MaxMemoryBytes = ConfigurationParsing.ParsePositiveLongOrDefault(section["MaxMemoryBytes"], limits.MaxMemoryBytes);
        limits.BackgroundJobThresholdBytes = ConfigurationParsing.ParsePositiveLongOrDefault(
            section["BackgroundJobThresholdBytes"], limits.BackgroundJobThresholdBytes);
        limits.MaxPreviewSizeBytes = ConfigurationParsing.ParsePositiveLongOrDefault(
            section["MaxPreviewSizeBytes"], limits.MaxPreviewSizeBytes);
        limits.MaxPreviewFeatures = ConfigurationParsing.ParsePositiveIntOrDefault(
            section["MaxPreviewFeatures"], limits.MaxPreviewFeatures);
        limits.MaxPreviewCountScan = ConfigurationParsing.ParsePositiveIntOrDefault(
            section["MaxPreviewCountScan"], limits.MaxPreviewCountScan);
        limits.StreamBufferSize = ConfigurationParsing.ParsePositiveIntOrDefault(
            section["StreamBufferSize"], limits.StreamBufferSize);
        limits.UseTransactions = ConfigurationParsing.ParseBoolOrDefault(section["UseTransactions"], limits.UseTransactions);
        limits.ContinueOnError = ConfigurationParsing.ParseBoolOrDefault(section["ContinueOnError"], limits.ContinueOnError);
        limits.MaxFeaturesPerFile = ConfigurationParsing.ParseNonNegativeIntOrDefault(
            section["MaxFeaturesPerFile"], limits.MaxFeaturesPerFile);
        limits.MaxArchiveEntryBytes = ConfigurationParsing.ParsePositiveLongOrDefault(
            section["MaxArchiveEntryBytes"], limits.MaxArchiveEntryBytes);
        limits.MaxArchiveExtractedBytes = ConfigurationParsing.ParsePositiveLongOrDefault(
            section["MaxArchiveExtractedBytes"], limits.MaxArchiveExtractedBytes);
        limits.MaxArchiveCompressionRatio = ConfigurationParsing.ParsePositiveDoubleOrDefault(
            section["MaxArchiveCompressionRatio"], limits.MaxArchiveCompressionRatio);
    }

    private static string ResolveConnectionString(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("DefaultConnection connection string is required for PostgreSQL services");
        }

        var resolver = serviceProvider.GetService<IConnectionSecretResolver>();
        if (resolver == null) return connectionString;

        try
        {
            var canResolve = resolver.CanResolveSecretAsync(connectionString, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!canResolve) return connectionString;

            return resolver.ResolveConnectionStringAsync(connectionString, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to resolve DefaultConnection via secret provider.", ex);
        }
    }
}