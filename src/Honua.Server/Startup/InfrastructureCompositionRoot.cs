// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Startup;

/// <summary>
/// Composition root for swapping Core abstractions over to their Infrastructure
/// implementations (provider routing plus shared cache, tile, and limits-options binders
/// that the rest of the registration pipeline depends on).
/// </summary>
internal static class InfrastructureCompositionRoot
{
    /// <summary>
    /// Registers the configured database provider's services, the SQL Server read-only
    /// feature provider, the feature-provider routing surface, and the caching decorators
    /// for <see cref="ILayerStyleCatalog"/>.
    /// </summary>
    public static void RegisterInfrastructureServices(IServiceCollection services, IConfiguration configuration)
    {
        var configuredProvider = configuration.GetValue<string>("DataSource:Provider");
        var provider = DataProviderNames.Normalize(configuredProvider);
        switch (provider)
        {
            case DataProviderNames.Postgis:
            case DataProviderNames.PostgreSql:
                Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, configuration);
                break;
            case DataProviderNames.DuckDb:
                Honua.DuckDB.ServiceCollectionExtensions.AddDuckDBServices(services, configuration);
                break;
            case DataProviderNames.MySql:
                Honua.MySql.ServiceCollectionExtensions.AddMySqlServices(services, configuration);
                break;
            default:
                throw new InvalidOperationException($"Unsupported data source provider '{configuredProvider}'.");
        }

        // Register the SQL Server spatial provider as an additional read-only feature backend (#850).
        // Metadata v2 publications whose connection resolves to provider 'sqlserver'/'mssql' are routed here
        // through the shared FeatureProviderQueryRouter. Disabled when SqlServer:Enabled is explicitly false.
        if (configuration.GetValue("SqlServer:Enabled", true))
        {
            Honua.SqlServer.ServiceCollectionExtensions.AddSqlServerFeatureProvider(services, configuration);
        }

        services.TryAddScoped<IFeatureDataProviderRegistry>(serviceProvider =>
            new FeatureDataProviderRegistry(serviceProvider.GetServices<IFeatureDataProvider>()));
        services.TryAddScoped(serviceProvider =>
            new FeatureProviderQueryRouter(
                serviceProvider.GetRequiredService<Honua.Core.Features.Security.Abstractions.ISecureConnectionRegistry>(),
                serviceProvider.GetRequiredService<IFeatureDataProviderRegistry>(),
                provider));

        // Add centralized configuration management and secret services
        services.AddConfigurationManagement(configuration);

        // Audit-A1 / ADR-0044: IGeometryService's concrete NTS-backed
        // implementation lives in Server because Honua.Hosting must not
        // depend on the NetTopologySuite package graph. The hosting-side
        // validation surface (AddValidationServices) consumes the
        // IGeometryService abstraction registered here.
        services.AddSingleton<IGeometryService, GeometryService>();

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

    /// <summary>Bind + validate <see cref="LimitsOptions"/> from configuration.</summary>
    public static void ConfigureLimits(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LimitsOptions>(options =>
        {
            configuration.GetSection(LimitsOptions.SectionName).Bind(options);

            var validator = new LimitsOptionsValidator();
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

    /// <summary>Bind <see cref="Honua.Core.Features.Tiles.TileOptions"/> with default values.</summary>
    public static void ConfigureTileOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Honua.Core.Features.Tiles.TileOptions>(options =>
        {
            configuration.GetSection(Honua.Core.Features.Tiles.TileOptions.SectionName).Bind(options);
        });
    }

    /// <summary>
    /// Wires the unified cache service (Redis + in-memory fallback), the response cache,
    /// and the distributed cache-refresh coordinator hosted service.
    /// </summary>
    public static void ConfigureCaching(IServiceCollection services, IConfiguration configuration, bool redisCacheEntitled)
    {
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

        services.AddSingleton<DistributedCacheRefreshCoordinator>(sp =>
            new DistributedCacheRefreshCoordinator(
                sp.GetRequiredService<IOptions<CacheOptions>>(),
                sp.GetRequiredService<IPerformanceMonitor>(),
                sp.GetRequiredService<ILogger<DistributedCacheRefreshCoordinator>>(),
                redisCacheEntitled ? sp.GetService<IConnectionMultiplexer>() : null));

        services.AddSingleton<ICacheRefreshCoordinator>(sp =>
            sp.GetRequiredService<DistributedCacheRefreshCoordinator>());
        services.AddSingleton<IDistributedCacheRefreshCoordinator>(sp =>
            sp.GetRequiredService<DistributedCacheRefreshCoordinator>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<DistributedCacheRefreshCoordinator>());
    }
}
