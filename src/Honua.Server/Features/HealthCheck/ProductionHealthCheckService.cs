// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Caching;
using Honua.Server.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Comprehensive health check service for production monitoring.
/// Aggregates database, cache, memory, and external service health status.
/// </summary>
internal sealed class ProductionHealthCheckService : IHealthCheck
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConnectionPoolMetrics _connectionPoolMetrics;
    private readonly ProductionMetricsCollector _metricsCollector;
    private readonly IConnectionMultiplexer? _redis;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<ProductionHealthCheckService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProductionHealthCheckService"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency resolution.</param>
    /// <param name="connectionPoolMetrics">Database connection pool metrics.</param>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <param name="redis">Redis connection multiplexer.</param>
    /// <param name="cacheOptions">Cache configuration options.</param>
    /// <param name="logger">Logger instance.</param>
    public ProductionHealthCheckService(
        IServiceProvider serviceProvider,
        ConnectionPoolMetrics connectionPoolMetrics,
        ProductionMetricsCollector metricsCollector,
        IConnectionMultiplexer? redis,
        IOptions<CacheOptions> cacheOptions,
        ILogger<ProductionHealthCheckService> logger)
    {
        _serviceProvider = serviceProvider;
        _connectionPoolMetrics = connectionPoolMetrics;
        _metricsCollector = metricsCollector;
        _redis = redis;
        _cacheOptions = cacheOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Performs comprehensive health check across all critical systems.
    /// </summary>
    /// <param name="context">Health check context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health check result.</returns>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var healthData = new Dictionary<string, object>();
        var isHealthy = true;
        var issues = new List<string>();

        try
        {
            // Check database health
            var dbHealth = await CheckDatabaseHealthAsync(cancellationToken);
            healthData["database"] = dbHealth;
            if (!dbHealth.IsHealthy)
            {
                isHealthy = false;
                issues.AddRange(dbHealth.Issues);
            }

            // Check cache health
            var cacheHealth = await CheckCacheHealthAsync(cancellationToken);
            healthData["cache"] = cacheHealth;
            if (!cacheHealth.IsHealthy)
            {
                isHealthy = false;
                issues.AddRange(cacheHealth.Issues);
            }

            // Check memory health
            var memoryHealth = CheckMemoryHealth();
            healthData["memory"] = memoryHealth;
            if (!memoryHealth.IsHealthy)
            {
                isHealthy = false;
                issues.AddRange(memoryHealth.Issues);
            }

            // Check connection pool health
            var poolHealth = CheckConnectionPoolHealth();
            healthData["connectionPool"] = poolHealth;
            if (!poolHealth.IsHealthy)
            {
                isHealthy = false;
                issues.AddRange(poolHealth.Issues);
            }

            // Get overall production metrics
            var productionMetrics = _metricsCollector.GetHealthMetrics();
            healthData["productionMetrics"] = new
            {
                errorRate = productionMetrics.ErrorRate,
                cacheHitRatio = productionMetrics.CacheHitRatio,
                memoryPressure = productionMetrics.MemoryPressureLevel,
                timestamp = productionMetrics.Timestamp
            };

            // Aggregate health status
            var status = isHealthy ? HealthStatus.Healthy : HealthStatus.Degraded;
            var description = isHealthy
                ? "All systems are healthy"
                : $"Found {issues.Count} issue(s): {string.Join("; ", issues)}";

            return HealthCheckResult.Healthy(description, healthData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform production health check");
            return HealthCheckResult.Unhealthy("Health check failed", ex, healthData);
        }
    }

    /// <summary>
    /// Checks database health including connection pool status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Database health status.</returns>
    private async Task<ComponentHealthStatus> CheckDatabaseHealthAsync(CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var isHealthy = true;

        try
        {
            // Test basic database connectivity
            using var scope = _serviceProvider.CreateScope();
            var connectionProvider = scope.ServiceProvider
                .GetService<Honua.Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider>();

            if (connectionProvider != null)
            {
                using var connection = connectionProvider.GetConnection();
                await connection.OpenAsync(cancellationToken);

                // Test simple query
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                var result = await command.ExecuteScalarAsync(cancellationToken);

                if (result == null || !result.Equals(1))
                {
                    isHealthy = false;
                    issues.Add("Database query test failed");
                }
            }
            else
            {
                isHealthy = false;
                issues.Add("Database connection provider not available");
            }

            // Check connection pool metrics
            var poolUtilization = _connectionPoolMetrics.GetPoolUtilization();
            if (poolUtilization > 0.9)
            {
                isHealthy = false;
                issues.Add($"High connection pool utilization: {poolUtilization:P2}");
            }

            var failures = _connectionPoolMetrics.GetTotalFailures();
            if (failures > 0)
            {
                isHealthy = false;
                issues.Add($"Connection acquisition failures: {failures}");
            }
        }
        catch (Exception ex)
        {
            isHealthy = false;
            issues.Add($"Database connectivity failed: {ex.Message}");
            _logger.LogError(ex, "Database health check failed");
        }

        return new ComponentHealthStatus
        {
            IsHealthy = isHealthy,
            Issues = issues,
            Metrics = new Dictionary<string, object>
            {
                ["poolUtilization"] = _connectionPoolMetrics.GetPoolUtilization(),
                ["totalFailures"] = _connectionPoolMetrics.GetTotalFailures(),
                ["totalTimeouts"] = _connectionPoolMetrics.GetTotalTimeouts()
            }
        };
    }

    /// <summary>
    /// Checks cache health including Redis connectivity and performance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cache health status.</returns>
    private async Task<ComponentHealthStatus> CheckCacheHealthAsync(CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var isHealthy = true;
        var metrics = new Dictionary<string, object>();

        try
        {
            // Check Redis connectivity if available
            if (_redis != null)
            {
                var database = _redis.GetDatabase();
                var testKey = "health_check_test";
                var testValue = DateTimeOffset.UtcNow.Ticks.ToString();

                // Test Redis write/read
                var startTime = DateTimeOffset.UtcNow;
                await database.StringSetAsync(testKey, testValue, TimeSpan.FromSeconds(10));
                var retrievedValue = await database.StringGetAsync(testKey);
                var latency = DateTimeOffset.UtcNow - startTime;

                metrics["redisLatencyMs"] = latency.TotalMilliseconds;

                if (!retrievedValue.Equals(testValue))
                {
                    isHealthy = false;
                    issues.Add("Redis read/write test failed");
                }

                if (latency.TotalMilliseconds > 1000) // > 1 second is concerning
                {
                    isHealthy = false;
                    issues.Add($"High Redis latency: {latency.TotalMilliseconds:F2}ms");
                }

                // Clean up test key
                await database.KeyDeleteAsync(testKey);

                // Check Redis server info
                var endpoints = _redis.GetEndPoints();
                var server = _redis.GetServer(endpoints[0]);
                var info = await server.InfoAsync("memory");
                metrics["redisMemoryUsed"] = ExtractRedisMemoryUsage(info);
            }
            else
            {
                metrics["redisAvailable"] = false;
            }

            // Check cache hit ratio
            var productionMetrics = _metricsCollector.GetHealthMetrics();
            metrics["cacheHitRatio"] = productionMetrics.CacheHitRatio;

            if (productionMetrics.CacheHitRatio < 0.5) // < 50% hit ratio is concerning
            {
                isHealthy = false;
                issues.Add($"Low cache hit ratio: {productionMetrics.CacheHitRatio:P2}");
            }
        }
        catch (Exception ex)
        {
            isHealthy = false;
            issues.Add($"Cache connectivity failed: {ex.Message}");
            _logger.LogError(ex, "Cache health check failed");
        }

        return new ComponentHealthStatus
        {
            IsHealthy = isHealthy,
            Issues = issues,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Checks memory health and GC pressure.
    /// </summary>
    /// <returns>Memory health status.</returns>
    private ComponentHealthStatus CheckMemoryHealth()
    {
        var issues = new List<string>();
        var isHealthy = true;

        var memoryUsage = GC.GetTotalMemory(false);
        var memoryMB = memoryUsage / (1024 * 1024);

        var metrics = new Dictionary<string, object>
        {
            ["memoryUsageBytes"] = memoryUsage,
            ["memoryUsageMB"] = memoryMB,
            ["gen0Collections"] = GC.CollectionCount(0),
            ["gen1Collections"] = GC.CollectionCount(1),
            ["gen2Collections"] = GC.CollectionCount(2)
        };

        // Check for high memory usage (> 2GB is concerning for most apps)
        if (memoryMB > 2048)
        {
            isHealthy = false;
            issues.Add($"High memory usage: {memoryMB}MB");
        }

        // Check for excessive Gen2 collections (indicator of memory pressure)
        var gen2Collections = GC.CollectionCount(2);
        if (gen2Collections > 1000) // This threshold may need adjustment
        {
            issues.Add($"High Gen2 GC collections: {gen2Collections}");
        }

        return new ComponentHealthStatus
        {
            IsHealthy = isHealthy,
            Issues = issues,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Checks connection pool health metrics.
    /// </summary>
    /// <returns>Connection pool health status.</returns>
    private ComponentHealthStatus CheckConnectionPoolHealth()
    {
        var issues = new List<string>();
        var isHealthy = true;

        var utilization = _connectionPoolMetrics.GetPoolUtilization();
        var failures = _connectionPoolMetrics.GetTotalFailures();
        var timeouts = _connectionPoolMetrics.GetTotalTimeouts();

        var metrics = new Dictionary<string, object>
        {
            ["utilization"] = utilization,
            ["failures"] = failures,
            ["timeouts"] = timeouts
        };

        if (utilization > 0.8)
        {
            issues.Add($"High pool utilization: {utilization:P2}");
            if (utilization > 0.95)
            {
                isHealthy = false;
            }
        }

        if (failures > 0)
        {
            isHealthy = false;
            issues.Add($"Connection failures: {failures}");
        }

        if (timeouts > 0)
        {
            isHealthy = false;
            issues.Add($"Connection timeouts: {timeouts}");
        }

        return new ComponentHealthStatus
        {
            IsHealthy = isHealthy,
            Issues = issues,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Extracts memory usage from Redis INFO response.
    /// </summary>
    /// <param name="info">Redis INFO response.</param>
    /// <returns>Memory usage in bytes or null if not found.</returns>
    private static long? ExtractRedisMemoryUsage(string info)
    {
        try
        {
            var lines = info.Split('\n');
            var memoryLine = lines.FirstOrDefault(l => l.StartsWith("used_memory:"));
            if (memoryLine != null)
            {
                var value = memoryLine.Split(':')[1].Trim();
                return long.Parse(value);
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }
}

/// <summary>
/// Health status for a component.
/// </summary>
internal sealed class ComponentHealthStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether the component is healthy.
    /// </summary>
    public required bool IsHealthy { get; set; }

    /// <summary>
    /// Gets or sets the list of issues found.
    /// </summary>
    public required List<string> Issues { get; set; }

    /// <summary>
    /// Gets or sets component-specific metrics.
    /// </summary>
    public Dictionary<string, object> Metrics { get; set; } = new();
}