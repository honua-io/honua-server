// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Infrastructure.Redis;

public static class RedisServiceCollectionExtensions
{
    public static IServiceCollection AddStandardizedRedisInfrastructure(this IServiceCollection services)
    {
        // Register Redis health monitor as singleton
        services.TryAddSingleton<IRedisHealthMonitor>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();
            var logger = sp.GetRequiredService<ILogger<RedisHealthMonitor>>();
            return new RedisHealthMonitor(redis, logger);
        });

        return services;
    }

    public static IServiceCollection AddRedisLeaderElection(
        this IServiceCollection services,
        string leadershipKey,
        TimeSpan? leaseDuration = null,
        string? nodeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leadershipKey);

        services.AddStandardizedRedisInfrastructure();

        services.TryAddSingleton<IRedisLeaderElection>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();
            var healthMonitor = sp.GetRequiredService<IRedisHealthMonitor>();
            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            var logger = sp.GetRequiredService<ILogger<RedisLeaderElection>>();

            return new RedisLeaderElection(
                leadershipKey,
                redis,
                healthMonitor,
                hostEnvironment,
                logger,
                leaseDuration,
                nodeId);
        });

        return services;
    }

    public static IServiceCollection AddRedisJobQueue(
        this IServiceCollection services,
        string queueKey,
        RedisFallbackMode fallbackStrategy = RedisFallbackMode.AllowLocalInDev)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueKey);

        services.AddStandardizedRedisInfrastructure();

        services.TryAddSingleton<IRedisJobQueue>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();
            var healthMonitor = sp.GetRequiredService<IRedisHealthMonitor>();
            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            var logger = sp.GetRequiredService<ILogger<RedisJobQueue>>();

            var strategy = fallbackStrategy switch
            {
                RedisFallbackMode.FailFast => RedisFallbackStrategy.FailFast,
                RedisFallbackMode.InMemoryFallback => RedisFallbackStrategy.InMemoryFallback,
                RedisFallbackMode.AllowLocalInDev => RedisFallbackStrategy.AllowLocalInDev,
                _ => throw new ArgumentException($"Unknown fallback strategy: {fallbackStrategy}")
            };

            return new RedisJobQueue(
                queueKey,
                redis,
                healthMonitor,
                strategy,
                hostEnvironment,
                logger);
        });

        return services;
    }

    public static IServiceCollection AddRedisJobQueues(
        this IServiceCollection services,
        Dictionary<string, RedisFallbackMode> queues)
    {
        ArgumentNullException.ThrowIfNull(queues);

        services.AddStandardizedRedisInfrastructure();

        services.TryAddSingleton<IReadOnlyDictionary<string, IRedisJobQueue>>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();
            var healthMonitor = sp.GetRequiredService<IRedisHealthMonitor>();
            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            var queueInstances = new Dictionary<string, IRedisJobQueue>();

            foreach (var (queueKey, fallbackMode) in queues)
            {
                var logger = loggerFactory.CreateLogger<RedisJobQueue>();
                var strategy = fallbackMode switch
                {
                    RedisFallbackMode.FailFast => RedisFallbackStrategy.FailFast,
                    RedisFallbackMode.InMemoryFallback => RedisFallbackStrategy.InMemoryFallback,
                    RedisFallbackMode.AllowLocalInDev => RedisFallbackStrategy.AllowLocalInDev,
                    _ => throw new ArgumentException($"Unknown fallback strategy: {fallbackMode}")
                };

                queueInstances[queueKey] = new RedisJobQueue(
                    queueKey,
                    redis,
                    healthMonitor,
                    strategy,
                    hostEnvironment,
                    logger);
            }

            return queueInstances;
        });

        return services;
    }

    public static IServiceCollection AddRedisHealthCheck(
        this IServiceCollection services,
        string name = "redis",
        params string[] tags)
    {
        services.AddStandardizedRedisInfrastructure();

        services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>(name, tags: tags);

        return services;
    }
}
