// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Startup;

/// <summary>
/// Composition root for swapping Core abstractions over to their Infrastructure
/// implementations (provider routing + decorator wiring for the layer catalogs). Also hosts
/// the shared cache, tile, and limits-options binders that the rest of the registration
/// pipeline depends on.
/// </summary>
internal static class InfrastructureCompositionRoot
{
    /// <summary>
    /// Registers the configured database provider's services, the SQL Server read-only
    /// feature provider, the feature-provider routing surface, and the caching decorators
    /// for <see cref="ILayerCatalog"/> / <see cref="ILayerStyleCatalog"/>.
    /// </summary>
    public static void RegisterInfrastructureServices(IServiceCollection services, IConfiguration configuration)
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

        // Register the CachingLayerCatalog - it will be wired via decorator pattern in RegisterInfrastructureServices
    }
}
