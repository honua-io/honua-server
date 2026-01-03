// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.Admin;
using Honua.Postgres.Features.Attachments;
using Honua.Postgres.Features.Catalog;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.HealthCheck;
using Honua.Postgres.Features.Import;
using Honua.Postgres.Features.Infrastructure.Caching;
using Honua.Postgres.Features.Infrastructure.Monitoring;
using Honua.Postgres.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
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

        // Register NpgsqlDataSource as specified in Issue #3
        services.AddSingleton<NpgsqlDataSource>(serviceProvider =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("DefaultConnection connection string is required for PostgreSQL services");
            }

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

            // PERFORMANCE OPTIMIZATION: Configure optimized connection settings
            dataSourceBuilder.ConnectionStringBuilder.Pooling = true;
            dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = 5;
            dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = 50;
            dataSourceBuilder.ConnectionStringBuilder.ConnectionIdleLifetime = 300; // 5 minutes
            dataSourceBuilder.ConnectionStringBuilder.ConnectionPruningInterval = 10;
            dataSourceBuilder.ConnectionStringBuilder.CommandTimeout = 30;
            dataSourceBuilder.ConnectionStringBuilder.WriteBufferSize = 16384; // 16KB
            dataSourceBuilder.ConnectionStringBuilder.ReadBufferSize = 16384; // 16KB
            dataSourceBuilder.ConnectionStringBuilder.NoResetOnClose = !schemaHeadersEnabled;
            dataSourceBuilder.ConnectionStringBuilder.Multiplexing = !schemaHeadersEnabled;

            if (dataSourceBuilder.ConnectionStringBuilder.Multiplexing)
            {
                // Npgsql multiplexing does not support keepalive settings.
                dataSourceBuilder.ConnectionStringBuilder.KeepAlive = 0;
                dataSourceBuilder.ConnectionStringBuilder.TcpKeepAliveTime = 0;
                dataSourceBuilder.ConnectionStringBuilder.TcpKeepAliveInterval = 0;
            }
            else
            {
                dataSourceBuilder.ConnectionStringBuilder.KeepAlive = 30;
                dataSourceBuilder.ConnectionStringBuilder.TcpKeepAliveTime = 30;
                dataSourceBuilder.ConnectionStringBuilder.TcpKeepAliveInterval = 2;
            }

            // Note: Not using EnableDynamicJson() for AOT compatibility
            // Manual JSON serialization is used instead for JSONB parameters

            return dataSourceBuilder.Build();
        });

        // PERFORMANCE OPTIMIZATION: Register StringBuilder object pool
        services.AddSingleton<ObjectPool<StringBuilder>>(serviceProvider =>
        {
            var provider = new DefaultObjectPoolProvider();
            return provider.Create(new PostgresFeatureStore.StringBuilderPooledObjectPolicy());
        });

        // Register feature store implementation
        services.AddScoped<PostgresFeatureStore>();
        services.AddScoped<IFeatureStore>(serviceProvider =>
        {
            var innerStore = serviceProvider.GetRequiredService<PostgresFeatureStore>();
            var performanceMonitor = serviceProvider.GetRequiredService<IPerformanceMonitor>();
            var logger = serviceProvider.GetRequiredService<ILogger<MonitoredFeatureStoreDecorator>>();
            return new MonitoredFeatureStoreDecorator(innerStore, performanceMonitor, logger);
        });

        // Register database performance metrics provider
        services.AddSingleton<IDatabasePerformanceMetricsProvider, PostgresDatabasePerformanceMetricsProvider>();

        // Register attachment store implementation (metadata tables live in the honua schema)
        services.AddScoped<IAttachmentStore>(serviceProvider =>
            new PostgresAttachmentStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                schemaName: string.IsNullOrWhiteSpace(configuration["Attachments:Schema"])
                    ? "honua"
                    : configuration["Attachments:Schema"],
                storageBasePath: configuration["Attachments:StoragePath"]));

        // Register layer catalog implementation
        services.AddScoped<ILayerCatalog, PostgresLayerCatalog>();

        // Register admin catalog for metadata CRUD operations
        services.AddScoped<IAdminCatalog, PostgresAdminCatalog>();

        // Register table discovery implementation
        services.AddScoped<ITableDiscoveryService, PostgreSqlTableDiscoveryService>();

        // Register health checker
        services.AddScoped<IDatabaseHealthChecker, PostgresDatabaseHealthChecker>();

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

        // PERFORMANCE OPTIMIZATION: Register high-frequency query preparation service
        // Pre-prepares known frequently-used queries for optimal initial performance
        services.AddHostedService<HighFrequencyQueryPreparationService>();

        // Register enhanced database connection provider with prepared statement caching
        services.AddScoped<IDatabaseConnectionProvider, CachingDatabaseConnectionProvider>();

        // Register CRS detection service
        services.AddScoped<ICrsDetectionService>(serviceProvider =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("DefaultConnection connection string is required for CRS detection services");
            }

            return new CrsDetectionService(connectionString, serviceProvider.GetService<ISchemaContext>());
        });

        // Register import limits configuration
        services.AddSingleton(serviceProvider =>
        {
            var section = configuration.GetSection("Import:Limits");
            var limits = new Core.Features.Import.Domain.ImportLimits();

            if (section.Exists())
            {
                limits = new Core.Features.Import.Domain.ImportLimits
                {
                    BatchSize = int.TryParse(section["BatchSize"], out var batchSize) ? batchSize : limits.BatchSize,
                    MaxMemoryBytes = long.TryParse(section["MaxMemoryBytes"], out var maxMemory) ? maxMemory : limits.MaxMemoryBytes,
                    BackgroundJobThresholdBytes = long.TryParse(section["BackgroundJobThresholdBytes"], out var bgThreshold) ? bgThreshold : limits.BackgroundJobThresholdBytes,
                    MaxPreviewSizeBytes = long.TryParse(section["MaxPreviewSizeBytes"], out var previewSize) ? previewSize : limits.MaxPreviewSizeBytes,
                    MaxPreviewFeatures = int.TryParse(section["MaxPreviewFeatures"], out var previewFeatures) ? previewFeatures : limits.MaxPreviewFeatures,
                    StreamBufferSize = int.TryParse(section["StreamBufferSize"], out var bufferSize) ? bufferSize : limits.StreamBufferSize,
                    UseTransactions = bool.TryParse(section["UseTransactions"], out var useTransactions) ? useTransactions : limits.UseTransactions,
                    ContinueOnError = bool.TryParse(section["ContinueOnError"], out var continueOnError) ? continueOnError : limits.ContinueOnError,
                    MaxFeaturesPerFile = int.TryParse(section["MaxFeaturesPerFile"], out var maxFeatures) ? maxFeatures : limits.MaxFeaturesPerFile
                };
            }

            return limits;
        });

        // Register streaming file import service with memory-efficient batch processing
        services.AddScoped<IFileImportService>(serviceProvider =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("DefaultConnection connection string is required for file import services");
            }

            var limits = serviceProvider.GetRequiredService<Core.Features.Import.Domain.ImportLimits>();
            var performanceMonitor = serviceProvider.GetRequiredService<IPerformanceMonitor>();
            var logger = serviceProvider.GetRequiredService<ILogger<StreamingFileImportService>>();
            return new StreamingFileImportService(
                connectionString,
                performanceMonitor,
                logger,
                limits,
                serviceProvider.GetService<ISchemaContext>());
        });

        // Register background import job service
        // Note: This uses a scoped service provider to access the import service
        services.AddScoped<IImportJobService>(serviceProvider =>
        {
            var importService = serviceProvider.GetRequiredService<IFileImportService>();
            var performanceMonitor = serviceProvider.GetRequiredService<IPerformanceMonitor>();
            var logger = serviceProvider.GetRequiredService<ILogger<InMemoryImportJobService>>();
            return new InMemoryImportJobService(importService, performanceMonitor, logger);
        });

        // Register ArcGIS REST client for Esri service imports
        services.AddHttpClient<ArcGisRestClient>()
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(5);
            });

        // Register Esri import service
        services.AddScoped<IEsriImportService, EsriImportService>();

        return services;
    }
}
