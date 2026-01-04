// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Postgres.Features.Infrastructure.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

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
        // Register object pools for performance optimization
        services.AddSingleton<ObjectPool<StringBuilder>>(provider =>
        {
            var poolProvider = provider.GetRequiredService<ObjectPoolProvider>();
            return poolProvider.Create(new Services.StringBuilderPooledObjectPolicy());
        });

        services.AddSingleton<ObjectPool<Dictionary<string, object?>>>(provider =>
        {
            var poolProvider = provider.GetRequiredService<ObjectPoolProvider>();
            return poolProvider.Create(new DictionaryPooledObjectPolicy());
        });

        // Register core feature store services
        services.AddSingleton<IGeometryProcessor>(provider =>
            new GeometryProcessor());

        services.AddSingleton<IFeatureCacheManager>(provider =>
        {
            var connectionProvider = provider.GetRequiredService<Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider>();
            var logger = provider.GetRequiredService<ILogger<FeatureCacheManager>>();
            return new FeatureCacheManager(connectionProvider, logger, schemaName);
        });

        services.AddSingleton<IFeatureQueryBuilder>(provider =>
        {
            var stringBuilderPool = provider.GetRequiredService<ObjectPool<StringBuilder>>();
            var geometryProcessor = provider.GetRequiredService<IGeometryProcessor>();
            return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor, schemaName);
        });

        services.AddSingleton<IFeatureDataAccess>(provider =>
        {
            var connectionProvider = provider.GetRequiredService<Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider>();
            var geometryProcessor = provider.GetRequiredService<IGeometryProcessor>();
            var cacheManager = provider.GetRequiredService<IFeatureCacheManager>();
            var dictionaryPool = provider.GetRequiredService<ObjectPool<Dictionary<string, object?>>>();
            var statementCache = provider.GetService<PreparedStatementCache>();
            var logger = provider.GetRequiredService<ILogger<FeatureDataAccess>>();
            return new FeatureDataAccess(connectionProvider, geometryProcessor, cacheManager, dictionaryPool, statementCache, logger, schemaName);
        });

        // Register the main feature store implementation
        services.AddSingleton<PostgresFeatureStoreRefactored>();

        // Optionally, you can register it as the primary implementation
        services.AddSingleton<IFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddSingleton<IGmlFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());
        services.AddSingleton<IStreamingFeatureStore>(provider => provider.GetRequiredService<PostgresFeatureStoreRefactored>());

        return services;
    }
}
