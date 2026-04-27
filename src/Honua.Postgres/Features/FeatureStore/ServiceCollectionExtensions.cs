// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Postgres.Features.Infrastructure.Caching;
using Honua.Postgres.Features.SpatialAnalytics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.FeatureStore;

/// <summary>
/// Dependency injection extensions for the refactored feature store services
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the refactored feature store services with dependency injection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="schemaName">Optional database schema name</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddRefactoredFeatureStore(this IServiceCollection services, string? schemaName = null)
    {
        var poolProvider = new DefaultObjectPoolProvider();

        // Register object pools for performance optimization
        services.AddSingleton<ObjectPool<StringBuilder>>(_ =>
            poolProvider.Create(new Services.StringBuilderPooledObjectPolicy()));

        services.AddSingleton<ObjectPool<Dictionary<string, object?>>>(_ =>
            poolProvider.Create(new DictionaryPooledObjectPolicy()));

        // Register core feature store services
        services.AddSingleton<IGeometryProcessor>(provider =>
        {
            var limits = provider.GetService<IOptions<LimitsOptions>>()?.Value?.Geometry;
            var geoJsonPrecision = limits?.MaxCoordinatePrecision ?? FeatureQueryEncoding.GeometryTextPrecision;
            return new GeometryProcessor(geoJsonPrecision);
        });

        services.AddScoped<IFeatureCacheManager>(provider =>
        {
            var connectionProvider = provider.GetRequiredService<IDatabaseConnectionProvider>();
            var logger = provider.GetRequiredService<ILogger<FeatureCacheManager>>();
            return new FeatureCacheManager(connectionProvider, logger, schemaName);
        });

        services.AddScoped<IFeatureQueryBuilder>(provider =>
        {
            var stringBuilderPool = provider.GetRequiredService<ObjectPool<StringBuilder>>();
            var geometryProcessor = provider.GetRequiredService<IGeometryProcessor>();
            return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor, schemaName);
        });

        services.AddScoped<IFeatureDataAccess>(provider =>
        {
            var dependencies = new FeatureDataAccessDependencies(
                provider.GetRequiredService<IDatabaseConnectionProvider>(),
                provider.GetRequiredService<IGeometryProcessor>(),
                provider.GetRequiredService<IFeatureCacheManager>(),
                provider.GetRequiredService<ObjectPool<Dictionary<string, object?>>>(),
                provider.GetService<PreparedStatementCache>(),
                provider.GetRequiredService<ILogger<FeatureDataAccess>>(),
                provider.GetService<IOptions<PerformanceMonitoringOptions>>(),
                provider.GetService<IOptions<LimitsOptions>>(),
                provider.GetService<IPerformanceMonitor>(),
                schemaName);

            return new FeatureDataAccess(dependencies);
        });

        // Register the main feature store implementation
        services.AddScoped<PostgresFeatureStoreRefactored>();

        // Register segregated interfaces
        services.AddScoped<IFeatureDataProvider>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IFeatureReader>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IFeatureWriter>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<ITileProvider>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IRelationshipStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IGeoJsonFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IGeobufFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IGmlFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IKmlFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddScoped<IStreamingFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());

        // Spatial analytics reader (clustering, spatial join, buffer aggregate, density).
        // Composes the existing query builder + data access pipeline so all observability
        // (slow-query logging, metrics, telemetry) flows through the same code path as
        // statistics, date bins and H3 aggregation.
        services.AddScoped<ISpatialAnalyticsReader, PostgresSpatialAnalyticsReader>();

        return services;
    }
}
