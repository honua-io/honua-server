// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using Honua.Core.Features.Infrastructure.Redis;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Example configuration for standardized Redis infrastructure.
/// This demonstrates how to integrate the new Redis fallback patterns into Program.cs.
/// </summary>
public static class RedisConfigurationExample
{
    /// <summary>
    /// Configures standardized Redis infrastructure in Program.cs
    /// </summary>
    public static IServiceCollection ConfigureStandardizedRedis(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        // 1. Add core Redis infrastructure (health monitoring, etc.)
        services.AddStandardizedRedisInfrastructure();

        // 2. Configure Redis services based on environment and requirements
        ConfigureRedisServices(services, hostEnvironment);

        // 3. Add Redis health checks
        services.AddRedisHealthCheck("redis-infrastructure", "infrastructure", "redis");

        // 4. Replace existing Redis-dependent services with standardized versions
        ReplaceExistingRedisServices(services);

        return services;
    }

    private static void ConfigureRedisServices(IServiceCollection services, IHostEnvironment hostEnvironment)
    {
        // Leader election for background services coordination
        services.AddRedisLeaderElection(
            leadershipKey: "background-worker-leader",
            leaseDuration: TimeSpan.FromMinutes(1));

        // Job queues with different fallback strategies based on criticality
        services.AddRedisJobQueues(new Dictionary<string, RedisFallbackMode>
        {
            // Critical operations must use distributed coordination
            ["geoservices-import"] = RedisFallbackMode.AllowLocalInDev,
            ["geoserver-import"] = RedisFallbackMode.AllowLocalInDev,
            ["workflow-operations"] = RedisFallbackMode.AllowLocalInDev,

            // Background tasks can fall back to in-memory processing
            ["cache-refresh"] = RedisFallbackMode.InMemoryFallback,
            ["tile-operations"] = RedisFallbackMode.InMemoryFallback,
            ["export-jobs"] = RedisFallbackMode.InMemoryFallback,

            // Critical coordination that must fail if Redis is unavailable
            ["distributed-locks"] = RedisFallbackMode.FailFast,
            ["cluster-coordination"] = RedisFallbackMode.FailFast
        });
    }

    private static void ReplaceExistingRedisServices(IServiceCollection services)
    {
        // Replace existing DistributedReplicaStore with standardized version
        // Remove the old registration first
        var oldReplicaStore = services.FirstOrDefault(d =>
            d.ServiceType == typeof(Features.Protocols.GeoServices.FeatureServer.DistributedReplicaStore));
        if (oldReplicaStore != null)
        {
            services.Remove(oldReplicaStore);
        }

        // Add standardized version
        services.AddSingleton<Features.Protocols.GeoServices.FeatureServer.StandardizedDistributedReplicaStore>(sp =>
            new Features.Protocols.GeoServices.FeatureServer.StandardizedDistributedReplicaStore(
                sp.GetService<IDistributedCache>(),
                sp.GetService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IRedisHealthMonitor>(),
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetRequiredService<ILogger<Features.Protocols.GeoServices.FeatureServer.StandardizedDistributedReplicaStore>>()));

        // Replace existing RedisImportJobManager with standardized job queue
        services.AddSingleton<IRedisJobQueue>(sp =>
            sp.GetRequiredService<IReadOnlyDictionary<string, IRedisJobQueue>>()["geoservices-import"]);
    }
}

/// <summary>
/// Extension methods for migrating existing services to use standardized Redis infrastructure.
/// </summary>
public static class RedisServiceMigrationExtensions
{
    /// <summary>
    /// Updates the FeatureChangeWebhookDispatcher to use standardized leader election.
    /// </summary>
    public static IServiceCollection MigrateFeatureChangeWebhookDispatcher(this IServiceCollection services)
    {
        // The live dispatcher registration in Program.cs still owns its lease coordinator wiring.
        // This example remains a no-op until that dispatcher is migrated to IRedisLeaderElection.
        return services;
    }

    /// <summary>
    /// Updates import services to use standardized job queues.
    /// </summary>
    public static IServiceCollection MigrateImportServices(this IServiceCollection services)
    {
        // The existing RedisImportJobManager should be replaced with the standardized job queue
        // and the import background services updated to use IRedisJobQueue

        // Remove existing import job manager registration
        var existingImportManager = services.FirstOrDefault(d =>
            d.ServiceType.Name.Contains("RedisImportJobManager"));
        if (existingImportManager != null)
        {
            services.Remove(existingImportManager);
        }

        // Import services would be updated to use the standardized job queue directly
        // This is a significant refactoring that would need to be done carefully

        return services;
    }
}

/// <summary>
/// Configuration validation for Redis services.
/// </summary>
public static class RedisConfigurationValidator
{
    /// <summary>
    /// Validates Redis configuration and fallback strategies.
    /// </summary>
    public static void ValidateRedisConfiguration(IServiceProvider services, IHostEnvironment hostEnvironment)
    {
        var healthMonitor = services.GetRequiredService<IRedisHealthMonitor>();

        if (hostEnvironment.IsProduction())
        {
            // In production, Redis should be configured
            if (!healthMonitor.WasRedisEverAvailable)
            {
                throw new InvalidOperationException(
                    "Redis is not configured but is required for production deployment. " +
                    "Configure Redis connection string or update fallback strategies.");
            }

            // Validate that critical services use appropriate fallback strategies
            var jobQueues = services.GetService<IReadOnlyDictionary<string, IRedisJobQueue>>();
            if (jobQueues != null)
            {
                var criticalQueues = jobQueues.Where(kvp =>
                    kvp.Key.Contains("import") || kvp.Key.Contains("workflow"));

                foreach (var (key, queue) in criticalQueues)
                {
                    if (queue is RedisServiceBase serviceBase &&
                        serviceBase.FallbackMode == RedisFallbackMode.InMemoryFallback)
                    {
                        throw new InvalidOperationException(
                            $"Job queue '{key}' uses InMemoryFallback in production, " +
                            "which may cause data loss or inconsistency. " +
                            "Use AllowLocalInDev or FailFast for critical operations.");
                    }
                }
            }
        }
    }
}
