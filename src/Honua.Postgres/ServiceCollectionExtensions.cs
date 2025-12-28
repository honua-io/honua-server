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
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.Admin;
using Honua.Postgres.Features.Attachments;
using Honua.Postgres.Features.Catalog;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.HealthCheck;
using Honua.Postgres.Features.Import;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Queries.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            dataSourceBuilder.ConnectionStringBuilder.NoResetOnClose = true;
            dataSourceBuilder.ConnectionStringBuilder.Multiplexing = true;

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
        services.AddScoped<IFeatureStore, PostgresFeatureStore>();

        // Register attachment store implementation (metadata tables live in the honua schema)
        services.AddScoped<IAttachmentStore>(serviceProvider =>
            new PostgresAttachmentStore(
                serviceProvider.GetRequiredService<IDatabaseConnectionProvider>(),
                schemaName: "honua"));

        // Register layer catalog implementation
        services.AddScoped<ILayerCatalog, PostgresLayerCatalog>();

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

        // Register database connection provider with resilience policies
        services.AddScoped<IDatabaseConnectionProvider, PostgresDatabaseConnectionProvider>();

        // Register CRS detection service
        services.AddScoped<ICrsDetectionService>(serviceProvider =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("DefaultConnection connection string is required for CRS detection services");
            }

            return new CrsDetectionService(connectionString);
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
                    BatchSize = section.GetValue("BatchSize", limits.BatchSize),
                    MaxMemoryBytes = section.GetValue("MaxMemoryBytes", limits.MaxMemoryBytes),
                    BackgroundJobThresholdBytes = section.GetValue("BackgroundJobThresholdBytes", limits.BackgroundJobThresholdBytes),
                    MaxPreviewSizeBytes = section.GetValue("MaxPreviewSizeBytes", limits.MaxPreviewSizeBytes),
                    MaxPreviewFeatures = section.GetValue("MaxPreviewFeatures", limits.MaxPreviewFeatures),
                    StreamBufferSize = section.GetValue("StreamBufferSize", limits.StreamBufferSize),
                    UseTransactions = section.GetValue("UseTransactions", limits.UseTransactions),
                    ContinueOnError = section.GetValue("ContinueOnError", limits.ContinueOnError),
                    MaxFeaturesPerFile = section.GetValue("MaxFeaturesPerFile", limits.MaxFeaturesPerFile)
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

            var crsDetectionService = serviceProvider.GetRequiredService<ICrsDetectionService>();
            var limits = serviceProvider.GetRequiredService<Core.Features.Import.Domain.ImportLimits>();
            return new StreamingFileImportService(connectionString, crsDetectionService, limits);
        });

        // Register background import job service
        // Note: This uses a scoped service provider to access the import service
        services.AddScoped<IImportJobService>(serviceProvider =>
        {
            var importService = serviceProvider.GetRequiredService<IFileImportService>();
            return new InMemoryImportJobService(importService);
        });

        return services;
    }
}
