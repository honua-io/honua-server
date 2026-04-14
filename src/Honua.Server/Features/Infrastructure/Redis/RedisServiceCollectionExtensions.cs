// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Service collection extensions for standardized Redis infrastructure.
/// </summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Adds standardized Redis infrastructure services to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
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

    /// <summary>
    /// Adds a Redis-based leader election service to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="leadershipKey">The Redis key to use for leadership coordination</param>
    /// <param name="leaseDuration">Optional lease duration (defaults to 1 minute)</param>
    /// <param name="nodeId">Optional node identifier (defaults to machine name + process ID)</param>
    /// <returns>The service collection for chaining</returns>
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

    /// <summary>
    /// Adds a Redis-based job queue service to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="queueKey">The Redis key to use for the job queue</param>
    /// <param name="fallbackStrategy">The fallback strategy to use when Redis is unavailable</param>
    /// <returns>The service collection for chaining</returns>
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

    /// <summary>
    /// Adds multiple Redis job queues with different keys and strategies.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="queues">Dictionary of queue configurations (key -> fallback strategy)</param>
    /// <returns>The service collection for chaining</returns>
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

    /// <summary>
    /// Adds Redis health check to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="name">Health check name</param>
    /// <param name="tags">Optional tags for the health check</param>
    /// <returns>The service collection for chaining</returns>
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
