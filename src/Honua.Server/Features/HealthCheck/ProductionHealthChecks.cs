// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Npgsql;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Comprehensive health checks for production dependencies.
/// </summary>
internal static class ProductionHealthChecks
{
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

        // Database health check with connection pool monitoring
        healthChecksBuilder.AddCheck<DatabaseHealthCheck>(
            "database",
            HealthStatus.Degraded,
            new[] { "db", "sql", "postgres" });

        // Redis cache health check (if Redis is configured)
        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            healthChecksBuilder.AddCheck<RedisHealthCheck>(
                "redis",
                HealthStatus.Degraded,
                new[] { "cache", "redis" });
        }

        // File upload service health check
        healthChecksBuilder.AddCheck<FileUploadHealthCheck>(
            "file-upload",
            HealthStatus.Degraded,
            new[] { "upload", "queue" });

        // External service connectivity checks
        healthChecksBuilder.AddCheck<ExternalServiceHealthCheck>(
            "external-services",
            HealthStatus.Degraded,
            new[] { "external", "http" });

        // Production metrics health check
        healthChecksBuilder.AddCheck<ProductionMetricsHealthCheck>(
            "production-metrics",
            HealthStatus.Degraded,
            new[] { "metrics", "monitoring" });

        return services;
    }
}

/// <summary>
/// Health check for database connectivity and connection pool status.
/// </summary>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;
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
        NpgsqlDataSource dataSource,
        ConnectionPoolMetrics connectionPoolMetrics,
        IActiveDbConnectionTracker connectionTracker,
        ILogger<DatabaseHealthCheck> logger)
    {
        _dataSource = dataSource;
        _connectionPoolMetrics = connectionPoolMetrics;
        _connectionTracker = connectionTracker;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var startTime = DateTimeOffset.UtcNow;
            var poolUtilization = _connectionPoolMetrics.GetPoolUtilization();
            var activeConnections = _connectionTracker.GetActiveCount();
            var totalFailures = _connectionPoolMetrics.GetTotalFailures();

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
                ["poolUtilizationPercentage"] = $"{poolUtilization:P2}",
                ["activeConnections"] = activeConnections,
                ["connectionFailures"] = totalFailures,
                ["connectionTimeouts"] = _connectionPoolMetrics.GetTotalTimeouts()
            };

            // Determine health status based on metrics
            if (poolUtilization > 0.9 || totalFailures > 0 || connectionLatency.TotalMilliseconds > 5000)
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
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy(
                "Database connectivity failed",
                ex,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
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
                ["configuration"] = _redis.Configuration,
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
            _logger.LogError(ex, "Redis health check failed");
            return HealthCheckResult.Unhealthy(
                "Redis connectivity failed",
                ex,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
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
            var queueUtilization = (double)queueSnapshot.QueueDepth / queueSnapshot.MaxQueueDepth;

            var data = new Dictionary<string, object>
            {
                ["queueDepth"] = queueSnapshot.QueueDepth,
                ["maxQueueDepth"] = queueSnapshot.MaxQueueDepth,
                ["activeUploads"] = queueSnapshot.ActiveUploads,
                ["maxConcurrentUploads"] = queueSnapshot.MaxConcurrentUploads,
                ["queueUtilization"] = queueUtilization,
                ["queueUtilizationPercentage"] = $"{queueUtilization:P2}"
            };

            // Determine health status based on queue metrics
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
            _logger.LogError(ex, "File upload health check failed");
            return HealthCheckResult.Unhealthy(
                "File upload service check failed",
                ex,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                });
        }
    }
}

/// <summary>
/// Health check for external service connectivity.
/// </summary>
internal sealed class ExternalServiceHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExternalServiceHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalServiceHealthCheck"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger instance.</param>
    public ExternalServiceHealthCheck(
        IHttpClientFactory httpClientFactory,
        ILogger<ExternalServiceHealthCheck> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = new Dictionary<string, object>();
            var overallHealthy = true;

            // Test connectivity to configured external services
            var httpClient = _httpClientFactory.CreateClient("IdentityProviderTest");

            // This is a basic connectivity test - in production you might want to test actual endpoints
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.google.com");
                request.Headers.Add("User-Agent", "Honua-HealthCheck/1.0");

                var response = await httpClient.SendAsync(request, cancellationToken);
                results["externalConnectivity"] = response.IsSuccessStatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    overallHealthy = false;
                }
            }
            catch (Exception ex)
            {
                results["externalConnectivity"] = false;
                results["connectivityError"] = ex.Message;
                overallHealthy = false;
            }

            if (!overallHealthy)
            {
                return HealthCheckResult.Degraded(
                    "Some external services are not reachable",
                    data: results);
            }

            return HealthCheckResult.Healthy("External services are reachable", results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External service health check failed");
            return HealthCheckResult.Degraded(
                "External service connectivity check failed",
                ex,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                });
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
            _logger.LogError(ex, "Production metrics health check failed");
            return HealthCheckResult.Degraded(
                "Production metrics collection failed",
                ex,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                });
        }
    }
}
