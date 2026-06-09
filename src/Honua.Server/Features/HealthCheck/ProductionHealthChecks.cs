// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Npgsql;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Abstractions;
using Honua.Infrastructure.Monitoring;
using Honua.Plugins;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Comprehensive health checks for production dependencies.
/// </summary>
internal static class ProductionHealthChecks
{
    private static readonly string[] DatabaseTags = ["db", "sql", "postgres"];
    private static readonly string[] RedisTags = ["cache", "redis"];
    private static readonly string[] UploadTags = ["upload", "queue"];
    private static readonly string[] ExternalServiceTags = ["external", "http"];
    private static readonly string[] MetricsTags = ["metrics", "monitoring"];
    private static readonly string[] OutboxTags = ["outbox", "events"];
    private static readonly string[] PluginTags = ["plugins", "extensibility"];

    /// <summary>
    /// Adds production health checks to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration for health check settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddProductionHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var healthChecksBuilder = services.AddHealthChecks();

        if (UsesPostgresProvider(configuration))
        {
            // Database health check with connection pool monitoring
            healthChecksBuilder.AddCheck<DatabaseHealthCheck>(
                "database",
                HealthStatus.Degraded,
                DatabaseTags);
        }

        // Redis cache health check (if Redis is configured)
        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            healthChecksBuilder.AddCheck<RedisHealthCheck>(
                "redis",
                HealthStatus.Degraded,
                RedisTags);
        }

        // File upload service health check
        healthChecksBuilder.AddCheck<FileUploadHealthCheck>(
            "file-upload",
            HealthStatus.Degraded,
            UploadTags);

        // External service connectivity checks
        healthChecksBuilder.AddCheck<ExternalServiceHealthCheck>(
            "external-services",
            HealthStatus.Degraded,
            ExternalServiceTags);

        // Production metrics health check
        healthChecksBuilder.AddCheck<ProductionMetricsHealthCheck>(
            "production-metrics",
            HealthStatus.Degraded,
            MetricsTags);

        // Feature-change transactional outbox dispatcher (#692). Surfaces dispatch backlog
        // and dead-letter accumulation on the readiness endpoint so deployments observe
        // CDC durability regressions before they cause silent event loss.
        healthChecksBuilder.AddCheck<Honua.Infrastructure.Events.Outbox.OutboxHealthCheck>(
            "feature-change-outbox",
            HealthStatus.Degraded,
            OutboxTags);

        // Plugin/extension SDK (#347): reports loaded plugins and whether the Enterprise
        // entitlement is active. Always healthy — plugins are optional.
        healthChecksBuilder.AddCheck<PluginHealthCheck>(
            "plugins",
            HealthStatus.Healthy,
            PluginTags);

        return services;
    }

    private static bool UsesPostgresProvider(IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("DataSource:Provider");

        return string.IsNullOrWhiteSpace(provider)
            || provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgis", StringComparison.OrdinalIgnoreCase);
    }

    internal static string FormatPercentage(double value)
        => value.ToString("0.00%", CultureInfo.InvariantCulture);
}

/// <summary>
/// Health check for database connectivity and connection pool status.
/// </summary>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ConnectionPoolMetrics _connectionPoolMetrics;
    private readonly IActiveDbConnectionTracker _connectionTracker;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseHealthCheck"/> class.
    /// </summary>
    /// <param name="dataSource">Npgsql data source.</param>
    /// <param name="connectionPoolMetrics">Connection pool metrics.</param>
    /// <param name="connectionTracker">Connection tracker.</param>
    /// <param name="logger">Logger instance.</param>
    public DatabaseHealthCheck(
        ConnectionPoolMetrics connectionPoolMetrics,
        IActiveDbConnectionTracker connectionTracker,
        ILogger<DatabaseHealthCheck> logger,
        NpgsqlDataSource? dataSource = null)
    {
        _connectionPoolMetrics = connectionPoolMetrics;
        _connectionTracker = connectionTracker;
        _logger = logger;
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var poolUtilization = _connectionPoolMetrics.GetPoolUtilization();
            var activeConnections = _connectionTracker.GetActiveCount();

            if (_dataSource is null)
            {
                return HealthCheckResult.Degraded(
                    "Database telemetry is unavailable because no PostgreSQL data source is registered",
                    data: new Dictionary<string, object>
                    {
                        ["poolUtilization"] = poolUtilization,
                        ["poolUtilizationPercentage"] = ProductionHealthChecks.FormatPercentage(poolUtilization),
                        ["activeConnections"] = activeConnections,
                        ["connectionFailures"] = _connectionPoolMetrics.GetTotalFailures(),
                        ["connectionTimeouts"] = _connectionPoolMetrics.GetTotalTimeouts(),
                        ["dataSourceRegistered"] = false
                    });
            }

            var startTime = DateTimeOffset.UtcNow;

            // Test database connectivity with a simple query
            await using var connection = _dataSource.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);

            var connectionLatency = DateTimeOffset.UtcNow - startTime;

            var data = new Dictionary<string, object>
            {
                ["connectionLatencyMs"] = connectionLatency.TotalMilliseconds,
                ["poolUtilization"] = poolUtilization,
                ["poolUtilizationPercentage"] = ProductionHealthChecks.FormatPercentage(poolUtilization),
                ["activeConnections"] = activeConnections,
                ["connectionFailures"] = _connectionPoolMetrics.GetTotalFailures(),
                ["connectionTimeouts"] = _connectionPoolMetrics.GetTotalTimeouts()
            };

            // Health is based on current connectivity and latency; cumulative counters are telemetry only.
            if (poolUtilization > 0.9 || connectionLatency.TotalMilliseconds > 5000)
            {
                return HealthCheckResult.Degraded(
                    "Database is experiencing high utilization or latency",
                    data: data);
            }

            if (poolUtilization > 0.8 || connectionLatency.TotalMilliseconds > 2000)
            {
                return HealthCheckResult.Degraded(
                    "Database performance is degraded",
                    data: data);
            }

            return HealthCheckResult.Healthy("Database is healthy", data);
        }
        catch (Exception ex)
        {
            ProductionHealthChecksLog.DatabaseHealthCheckFailed(_logger, ex);
            return HealthCheckResult.Unhealthy(
                "Database connectivity failed",
                ex,
                new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name
                });
        }
    }
}

/// <summary>
/// Health check for Redis cache connectivity and performance.
/// </summary>
internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly IDistributedCache _distributedCache;
    private readonly ILogger<RedisHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisHealthCheck"/> class.
    /// </summary>
    /// <param name="redis">Redis connection multiplexer.</param>
    /// <param name="distributedCache">Distributed cache.</param>
    /// <param name="logger">Logger instance.</param>
    public RedisHealthCheck(
        IConnectionMultiplexer? redis,
        IDistributedCache distributedCache,
        ILogger<RedisHealthCheck> logger)
    {
        _redis = redis;
        _distributedCache = distributedCache;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_redis == null)
            {
                return HealthCheckResult.Healthy(
                    "Redis not configured, using in-memory cache",
                    new Dictionary<string, object>
                    {
                        ["cacheType"] = "in-memory"
                    });
            }

            var startTime = DateTimeOffset.UtcNow;
            var database = _redis.GetDatabase();

            // Test Redis connectivity with ping
            var pingLatency = await database.PingAsync();

            // Test cache set/get operations
            var testKey = $"health-check:{Guid.NewGuid()}";
            var testValue = "test-value";

            await _distributedCache.SetStringAsync(testKey, testValue, cancellationToken);
            var retrievedValue = await _distributedCache.GetStringAsync(testKey, cancellationToken);
            await _distributedCache.RemoveAsync(testKey, cancellationToken);

            var totalLatency = DateTimeOffset.UtcNow - startTime;

            var data = new Dictionary<string, object>
            {
                ["pingLatencyMs"] = pingLatency.TotalMilliseconds,
                ["totalLatencyMs"] = totalLatency.TotalMilliseconds,
                ["isConnected"] = _redis.IsConnected,
                ["cacheOperationSuccess"] = retrievedValue == testValue
            };

            // Determine health status
            if (pingLatency.TotalMilliseconds > 1000 || !_redis.IsConnected)
            {
                return HealthCheckResult.Degraded(
                    "Redis is experiencing high latency or connection issues",
                    data: data);
            }

            if (retrievedValue != testValue)
            {
                return HealthCheckResult.Degraded(
                    "Redis cache operations are failing",
                    data: data);
            }

            return HealthCheckResult.Healthy("Redis is healthy", data);
        }
        catch (Exception ex)
        {
            ProductionHealthChecksLog.RedisHealthCheckFailed(_logger, ex);
            return HealthCheckResult.Unhealthy(
                "Redis connectivity failed",
                ex,
                new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name
                });
        }
    }
}

/// <summary>
/// Health check for file upload service and queue status.
/// </summary>
internal sealed class FileUploadHealthCheck : IHealthCheck
{
    private readonly IUploadQueueMetricsProvider _uploadQueueMetricsProvider;
    private readonly ILogger<FileUploadHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileUploadHealthCheck"/> class.
    /// </summary>
    /// <param name="uploadQueueMetricsProvider">Upload queue metrics provider.</param>
    /// <param name="logger">Logger instance.</param>
    public FileUploadHealthCheck(
        IUploadQueueMetricsProvider uploadQueueMetricsProvider,
        ILogger<FileUploadHealthCheck> logger)
    {
        _uploadQueueMetricsProvider = uploadQueueMetricsProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queueSnapshot = _uploadQueueMetricsProvider.GetQueueSnapshot();
            var queueUtilization = GetQueueUtilization(queueSnapshot.QueueDepth, queueSnapshot.MaxQueueDepth);

            var data = new Dictionary<string, object>
            {
                ["queueDepth"] = queueSnapshot.QueueDepth,
                ["maxQueueDepth"] = queueSnapshot.MaxQueueDepth,
                ["activeUploads"] = queueSnapshot.ActiveUploads,
                ["maxConcurrentUploads"] = queueSnapshot.MaxConcurrentUploads,
                ["queueUtilization"] = queueUtilization,
                ["queueUtilizationPercentage"] = ProductionHealthChecks.FormatPercentage(queueUtilization)
            };

            // Determine health status based on queue metrics
            if (queueSnapshot.MaxQueueDepth <= 0)
            {
                return HealthCheckResult.Unhealthy(
                    "Upload queue capacity is misconfigured",
                    data: data);
            }

            if (queueUtilization >= 1.0)
            {
                return HealthCheckResult.Unhealthy(
                    "Upload queue is full",
                    data: data);
            }

            if (queueUtilization > 0.8)
            {
                return HealthCheckResult.Degraded(
                    "Upload queue is at high utilization",
                    data: data);
            }

            return HealthCheckResult.Healthy("File upload service is healthy", data);
        }
        catch (Exception ex)
        {
            ProductionHealthChecksLog.FileUploadHealthCheckFailed(_logger, ex);
            return HealthCheckResult.Unhealthy(
                "File upload service check failed",
                ex,
                new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name
                });
        }
    }

    private static double GetQueueUtilization(int queueDepth, int maxQueueDepth)
        => maxQueueDepth > 0 ? (double)queueDepth / maxQueueDepth : 0.0;
}

/// <summary>
/// Health check for external service connectivity.
/// </summary>
internal sealed class ExternalServiceHealthCheck : IHealthCheck
{
    private readonly ILogger<ExternalServiceHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalServiceHealthCheck"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public ExternalServiceHealthCheck(
        ILogger<ExternalServiceHealthCheck> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "No external service probes are configured",
                new Dictionary<string, object>
                {
                    ["configuredProbes"] = 0
                }));
        }
        catch (Exception ex)
        {
            ProductionHealthChecksLog.ExternalServiceHealthCheckFailed(_logger, ex);
            return Task.FromResult(HealthCheckResult.Degraded(
                "External service connectivity check failed",
                ex,
                new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name
                }));
        }
    }
}

/// <summary>
/// Health check for production metrics collection.
/// </summary>
internal sealed class ProductionMetricsHealthCheck : IHealthCheck
{
    private readonly ProductionMetricsCollector _metricsCollector;
    private readonly ILogger<ProductionMetricsHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProductionMetricsHealthCheck"/> class.
    /// </summary>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <param name="logger">Logger instance.</param>
    public ProductionMetricsHealthCheck(
        ProductionMetricsCollector metricsCollector,
        ILogger<ProductionMetricsHealthCheck> logger)
    {
        _metricsCollector = metricsCollector;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var healthMetrics = _metricsCollector.GetHealthMetrics();
            var alertConditions = healthMetrics.GetAlertConditions();

            var data = new Dictionary<string, object>
            {
                ["memoryUsageMB"] = healthMetrics.MemoryUsageBytes / (1024 * 1024),
                ["memoryPressureLevel"] = healthMetrics.MemoryPressureLevel,
                ["cacheHitRatio"] = healthMetrics.CacheHitRatio,
                ["errorRate"] = healthMetrics.ErrorRate,
                ["dbPoolUtilization"] = healthMetrics.DatabaseConnectionPoolUtilization,
                ["activeAlerts"] = alertConditions.Count,
                ["isHealthy"] = healthMetrics.IsHealthy()
            };

            if (!healthMetrics.IsHealthy())
            {
                return HealthCheckResult.Degraded(
                    $"Production metrics indicate degraded performance: {string.Join(", ", alertConditions)}",
                    data: data);
            }

            return HealthCheckResult.Healthy("Production metrics are healthy", data);
        }
        catch (Exception ex)
        {
            ProductionHealthChecksLog.ProductionMetricsHealthCheckFailed(_logger, ex);
            return HealthCheckResult.Degraded(
                "Production metrics collection failed",
                ex,
                new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name
                });
        }
    }
}

/// <summary>
/// Health check for the plugin/extension SDK (#347). Reports the registered plugins and whether
/// the Enterprise <c>plugin.sdk</c> entitlement is active. Always healthy: plugins are optional.
/// </summary>
internal sealed class PluginHealthCheck : IHealthCheck
{
    private readonly PluginCatalog _catalog;
    private readonly ILicenseEntitlementService _licensing;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginHealthCheck"/> class.
    /// </summary>
    /// <param name="catalog">The registered plugin manifest catalog.</param>
    /// <param name="licensing">The runtime license entitlement service.</param>
    public PluginHealthCheck(PluginCatalog catalog, ILicenseEntitlementService licensing)
    {
        _catalog = catalog;
        _licensing = licensing;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var entitled = _licensing.CheckEntitlement(FeatureCatalog.PluginSdkKey).IsActive;
        var plugins = _catalog.Plugins;

        var data = new Dictionary<string, object>
        {
            ["pluginCount"] = plugins.Count,
            ["entitled"] = entitled,
            ["plugins"] = plugins.Select(p => $"{p.Id}@{p.Version}").ToArray()
        };

        var description = plugins.Count == 0
            ? "No plugins registered"
            : entitled
                ? $"{plugins.Count} plugin(s) active"
                : $"{plugins.Count} plugin(s) registered but inactive (Enterprise plugin.sdk entitlement required)";

        return Task.FromResult(HealthCheckResult.Healthy(description, data));
    }
}
