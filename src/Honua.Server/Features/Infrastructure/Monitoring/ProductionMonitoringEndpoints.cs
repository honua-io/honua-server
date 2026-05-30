// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.Import;
using Honua.Server.Features.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Endpoints for production monitoring, metrics, and health checks.
/// </summary>
internal static class ProductionMonitoringEndpoints
{
    /// <summary>
    /// Maps production monitoring endpoints.
    /// </summary>
    /// <param name="app">The web application.</param>
    public static void MapProductionMonitoringEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/monitoring")
            .WithTags("Monitoring")
            .RequireAdminAuthorization()
            .ExcludeFromDescription();

        // Production health dashboard endpoint
        group.MapGet("/health/production", GetProductionHealth)
            .WithName("GetProductionHealth")
            .WithSummary("Gets comprehensive production health metrics")
            .WithDescription("Returns detailed health metrics for production monitoring and alerting");

        // Connection pool metrics endpoint
        group.MapGet("/metrics/connection-pool", GetConnectionPoolMetrics)
            .WithName("GetConnectionPoolMetrics")
            .WithSummary("Gets database connection pool metrics")
            .WithDescription("Returns connection pool utilization, latency, and failure metrics");

        // Cache performance endpoint
        group.MapGet("/metrics/cache", GetCacheMetrics)
            .WithName("GetProductionCacheMetrics")
            .WithSummary("Gets cache performance metrics")
            .WithDescription("Returns cache hit ratios, eviction rates, and performance data");

        // System resource usage endpoint
        group.MapGet("/metrics/resources", GetResourceMetrics)
            .WithName("GetResourceMetrics")
            .WithSummary("Gets system resource usage metrics")
            .WithDescription("Returns memory, CPU, and other system resource metrics");

        // Alert conditions endpoint
        group.MapGet("/alerts", GetActiveAlerts)
            .WithName("GetActiveAlerts")
            .WithSummary("Gets active alert conditions")
            .WithDescription("Returns current alert conditions and thresholds");

        // File upload queue metrics endpoint
        group.MapGet("/metrics/upload-queue", GetUploadQueueMetrics)
            .WithName("GetUploadQueueMetrics")
            .WithSummary("Gets file upload queue metrics")
            .WithDescription("Returns upload queue depth and processing metrics");

        // Comprehensive health status endpoint
        group.MapGet("/health/comprehensive", GetComprehensiveHealth)
            .WithName("GetComprehensiveHealth")
            .WithSummary("Gets comprehensive health status")
            .WithDescription("Returns detailed health status from all critical dependencies");

        // Database circuit breaker status endpoint
        group.MapGet("/metrics/database-resilience", GetDatabaseResilienceMetrics)
            .WithName("GetDatabaseResilienceMetrics")
            .WithSummary("Gets database resilience metrics")
            .WithDescription("Returns database circuit breaker and connection metrics");
    }

    /// <summary>
    /// Gets comprehensive production health metrics.
    /// </summary>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <param name="licenseEntitlementService">Runtime license entitlement snapshot provider.</param>
    /// <returns>Production health metrics.</returns>
    private static IResult GetProductionHealth(
        [FromServices] ProductionMetricsCollector metricsCollector,
        [FromServices] ILicenseEntitlementService licenseEntitlementService)
    {
        var metrics = metricsCollector.GetHealthMetrics();
        var alertConditions = metrics.GetAlertConditions();

        var response = new ProductionHealthResponse
        {
            IsHealthy = metrics.IsHealthy(),
            OverallStatus = metrics.IsHealthy() ? "Healthy" : "Degraded",
            Metrics = metrics,
            AlertConditions = alertConditions,
            License = FileBackedLicenseService.ToHealthSummary(licenseEntitlementService.GetSnapshot()),
            LastUpdated = DateTimeOffset.UtcNow
        };

        return Results.Json(response, MetricsJsonContext.Default.ProductionHealthResponse);
    }

    /// <summary>
    /// Gets database connection pool metrics.
    /// </summary>
    /// <param name="connectionPoolMetrics">Connection pool metrics.</param>
    /// <param name="serviceProvider">Service provider used to resolve optional provider-specific metrics.</param>
    /// <returns>Connection pool metrics.</returns>
    private static IResult GetConnectionPoolMetrics(
        [FromServices] ConnectionPoolMetrics connectionPoolMetrics,
        [FromServices] IServiceProvider serviceProvider)
    {
        var hasUtilization = connectionPoolMetrics.TryGetPoolUtilization(out var utilization);
        var failures = connectionPoolMetrics.GetTotalFailures();
        var timeouts = connectionPoolMetrics.GetTotalTimeouts();
        var healthStatus = ResolveConnectionPoolHealthStatus(hasUtilization, utilization);
        var queryConcurrencyGate = serviceProvider.GetService<QueryConcurrencyGate>();

        var response = new ConnectionPoolMetricsResponse
        {
            Utilization = hasUtilization ? utilization : 0.0,
            HasUtilizationData = hasUtilization,
            UtilizationStatus = hasUtilization ? "available" : "unavailable",
            UtilizationPercentage = hasUtilization ? FormatPercentage(utilization) : "unavailable",
            TotalFailures = failures,
            TotalTimeouts = timeouts,
            HealthStatus = healthStatus,
            IsHealthy = string.Equals(healthStatus, "Healthy", StringComparison.Ordinal),
            QueryAdmission = queryConcurrencyGate is null
                ? null
                : QueryAdmissionMetricsResponse.FromSnapshot(queryConcurrencyGate.GetSnapshot()),
            Timestamp = DateTimeOffset.UtcNow
        };

        return Results.Json(response, MetricsJsonContext.Default.ConnectionPoolMetricsResponse);
    }

    /// <summary>
    /// Gets cache performance metrics.
    /// </summary>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <returns>Cache performance metrics.</returns>
    private static IResult GetCacheMetrics(
        [FromServices] ProductionMetricsCollector metricsCollector)
    {
        var healthMetrics = metricsCollector.GetHealthMetrics();

        var response = new CacheMetricsResponse
        {
            HitRatio = healthMetrics.CacheHitRatio,
            HitRatioPercentage = FormatPercentage(healthMetrics.CacheHitRatio),
            IsHealthy = healthMetrics.CacheHitRatio >= 0.8,
            Recommendations = GetCacheRecommendations(healthMetrics.CacheHitRatio),
            Timestamp = DateTimeOffset.UtcNow
        };

        return Results.Json(response, MetricsJsonContext.Default.CacheMetricsResponse);
    }

    /// <summary>
    /// Gets system resource usage metrics.
    /// </summary>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <returns>System resource metrics.</returns>
    private static IResult GetResourceMetrics(
        [FromServices] ProductionMetricsCollector metricsCollector)
    {
        var healthMetrics = metricsCollector.GetHealthMetrics();

        var response = new ResourceMetricsResponse
        {
            MemoryUsageBytes = healthMetrics.MemoryUsageBytes,
            MemoryUsageMB = healthMetrics.MemoryUsageBytes / (1024 * 1024),
            MemoryPressureLevel = healthMetrics.MemoryPressureLevel,
            GcInfo = GetGcInfo(),
            IsHealthy = healthMetrics.MemoryPressureLevel != "critical",
            Timestamp = DateTimeOffset.UtcNow
        };

        return Results.Json(response, MetricsJsonContext.Default.ResourceMetricsResponse);
    }

    /// <summary>
    /// Gets active alert conditions.
    /// </summary>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <returns>Active alert conditions.</returns>
    private static IResult GetActiveAlerts(
        [FromServices] ProductionMetricsCollector metricsCollector)
    {
        var healthMetrics = metricsCollector.GetHealthMetrics();
        var alertConditions = healthMetrics.GetAlertConditions();

        var response = new AlertsResponse
        {
            HasActiveAlerts = alertConditions.Count > 0,
            AlertCount = alertConditions.Count,
            Alerts = alertConditions.Select(condition => new AlertCondition
            {
                Condition = condition,
                Severity = DetermineAlertSeverity(condition),
                Timestamp = DateTimeOffset.UtcNow
            }).ToList(),
            Timestamp = DateTimeOffset.UtcNow
        };

        return Results.Json(response, MetricsJsonContext.Default.AlertsResponse);
    }

    /// <summary>
    /// Gets file upload queue metrics.
    /// </summary>
    /// <param name="uploadQueueMetricsProvider">Upload queue metrics provider.</param>
    /// <returns>Upload queue metrics.</returns>
    private static IResult GetUploadQueueMetrics(
        [FromServices] Abstractions.IUploadQueueMetricsProvider uploadQueueMetricsProvider)
    {
        var queueSnapshot = uploadQueueMetricsProvider.GetQueueSnapshot();

        var response = new UploadQueueMetricsResponse
        {
            QueueDepth = queueSnapshot.QueueDepth,
            MaxQueueDepth = queueSnapshot.MaxQueueDepth,
            ActiveUploads = queueSnapshot.ActiveUploads,
            MaxConcurrentUploads = queueSnapshot.MaxConcurrentUploads,
            QueueUtilization = GetQueueUtilization(queueSnapshot.QueueDepth, queueSnapshot.MaxQueueDepth),
            IsHealthy = IsUploadQueueHealthy(queueSnapshot.QueueDepth, queueSnapshot.MaxQueueDepth),
            Timestamp = DateTimeOffset.UtcNow
        };

        return Results.Json(response, MetricsJsonContext.Default.UploadQueueMetricsResponse);
    }

    /// <summary>
    /// Gets cache performance recommendations.
    /// </summary>
    /// <param name="hitRatio">Current hit ratio.</param>
    /// <returns>List of recommendations.</returns>
    private static List<string> GetCacheRecommendations(double hitRatio)
    {
        var recommendations = new List<string>();

        if (hitRatio < 0.5)
        {
            recommendations.Add("Critical: Cache hit ratio is very low. Consider increasing cache TTL or reviewing cache keys.");
            recommendations.Add("Review query patterns for opportunities to improve caching.");
        }
        else if (hitRatio < 0.8)
        {
            recommendations.Add("Consider tuning cache TTL values or cache size limits.");
            recommendations.Add("Monitor for cache eviction patterns.");
        }

        return recommendations;
    }

    /// <summary>
    /// Gets garbage collection information.
    /// </summary>
    /// <returns>GC information.</returns>
    private static ResourceGcInfoResponse GetGcInfo()
    {
        return new ResourceGcInfoResponse
        {
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            TotalMemory = GC.GetTotalMemory(false),
            LastGen0Time = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Determines alert severity based on condition text.
    /// </summary>
    /// <param name="condition">Alert condition text.</param>
    /// <returns>Alert severity.</returns>
    private static string DetermineAlertSeverity(string condition)
    {
        return condition.ToLowerInvariant() switch
        {
            var c when c.Contains("critical") => "Critical",
            var c when c.Contains("high") => "High",
            var c when c.Contains("failure") => "High",
            _ => "Medium"
        };
    }

    /// <summary>
    /// Gets comprehensive health status from all production health checks.
    /// </summary>
    /// <param name="healthCheckService">Health check service.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <returns>Comprehensive health status.</returns>
    private static async Task<IResult> GetComprehensiveHealth(
        [FromServices] Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService healthCheckService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ProductionMonitoringEndpoints));

        try
        {
            var healthReport = await healthCheckService.CheckHealthAsync();

            var response = new ComprehensiveHealthResponse
            {
                Status = healthReport.Status.ToString(),
                TotalDuration = healthReport.TotalDuration,
                Entries = healthReport.Entries.ToDictionary(
                    entry => entry.Key,
                    entry => new HealthCheckEntryResponse
                    {
                        Status = entry.Value.Status.ToString(),
                        Duration = entry.Value.Duration,
                        Description = entry.Value.Description,
                        Exception = entry.Value.Exception is null ? null : "See server logs for details.",
                        Data = SanitizeHealthCheckData(entry.Value.Data)
                    }),
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(response, MetricsJsonContext.Default.ComprehensiveHealthResponse);
        }
        catch (Exception ex)
        {
            MonitoringLog.ComprehensiveHealthEndpointFailed(logger, ex);
            return Results.Problem(
                title: "Health check failed",
                detail: "The comprehensive health report could not be generated. See server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Gets database resilience and circuit breaker metrics.
    /// </summary>
    /// <param name="connectionPoolMetrics">Connection pool metrics.</param>
    /// <param name="metricsCollector">Production metrics collector.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <returns>Database resilience metrics.</returns>
    private static IResult GetDatabaseResilienceMetrics(
        [FromServices] ConnectionPoolMetrics connectionPoolMetrics,
        [FromServices] ProductionMetricsCollector metricsCollector,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ProductionMonitoringEndpoints));

        try
        {
            var hasPoolUtilization = connectionPoolMetrics.TryGetPoolUtilization(out var poolUtilization);
            var totalFailures = connectionPoolMetrics.GetTotalFailures();
            var totalTimeouts = connectionPoolMetrics.GetTotalTimeouts();
            var healthMetrics = metricsCollector.GetHealthMetrics();
            var healthStatus = ResolveConnectionPoolHealthStatus(hasPoolUtilization, poolUtilization);

            var response = new DatabaseResilienceMetricsResponse
            {
                ConnectionPool = new DatabaseConnectionPoolMetrics
                {
                    Utilization = hasPoolUtilization ? poolUtilization : 0.0,
                    HasUtilizationData = hasPoolUtilization,
                    UtilizationStatus = hasPoolUtilization ? "available" : "unavailable",
                    UtilizationPercentage = hasPoolUtilization ? FormatPercentage(poolUtilization) : "unavailable",
                    ActiveConnections = healthMetrics.ActiveConnections,
                    TotalFailures = totalFailures,
                    TotalTimeouts = totalTimeouts,
                    HealthStatus = healthStatus,
                    IsHealthy = string.Equals(healthStatus, "Healthy", StringComparison.Ordinal)
                },
                Resilience = new DatabaseResilienceStatus
                {
                    CircuitBreakerEnabled = true, // This would come from configuration
                    ConnectionFailures = totalFailures,
                    QueryFailures = healthMetrics.TotalErrors,
                    ErrorRate = healthMetrics.ErrorRate,
                    ErrorRatePercentage = FormatPercentage(healthMetrics.ErrorRate)
                },
                Alerts = GetDatabaseAlerts(hasPoolUtilization, poolUtilization, healthMetrics.ErrorRate),
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(response, MetricsJsonContext.Default.DatabaseResilienceMetricsResponse);
        }
        catch (Exception ex)
        {
            MonitoringLog.DatabaseResilienceMetricsEndpointFailed(logger, ex);
            return Results.Problem(
                title: "Database resilience metrics failed",
                detail: "The database resilience metrics could not be generated. See server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Gets database-specific alerts based on metrics.
    /// </summary>
    /// <param name="hasPoolUtilization">Whether pool utilization data is available.</param>
    /// <param name="poolUtilization">Pool utilization ratio.</param>
    /// <param name="errorRate">Query error rate.</param>
    /// <returns>List of database alerts.</returns>
    private static List<string> GetDatabaseAlerts(bool hasPoolUtilization, double poolUtilization, double errorRate)
    {
        var alerts = new List<string>();

        if (!hasPoolUtilization)
        {
            alerts.Add("Warning: Database connection pool utilization telemetry is unavailable.");
        }
        else if (poolUtilization > 0.9)
        {
            alerts.Add($"Critical: Database connection pool utilization is very high ({poolUtilization:P2})");
        }
        else if (poolUtilization > 0.8)
        {
            alerts.Add($"Warning: Database connection pool utilization is high ({poolUtilization:P2})");
        }

        if (errorRate > 0.1)
        {
            alerts.Add($"Critical: Database query error rate is very high ({errorRate:P2})");
        }
        else if (errorRate > 0.05)
        {
            alerts.Add($"Warning: Database query error rate is elevated ({errorRate:P2})");
        }

        return alerts;
    }

    private static string FormatPercentage(double value)
        => value.ToString("0.00%", CultureInfo.InvariantCulture);

    private static double GetQueueUtilization(int queueDepth, int maxQueueDepth)
        => maxQueueDepth > 0 ? (double)queueDepth / maxQueueDepth : 0.0;

    private static bool IsUploadQueueHealthy(int queueDepth, int maxQueueDepth)
        => maxQueueDepth > 0 && queueDepth < maxQueueDepth * 0.8;

    private static string ResolveConnectionPoolHealthStatus(bool hasUtilization, double utilization)
    {
        if (!hasUtilization)
        {
            return "Unknown";
        }

        return utilization <= 0.8 ? "Healthy" : "Degraded";
    }

    private static Dictionary<string, JsonElement>? SanitizeHealthCheckData(
        IReadOnlyDictionary<string, object>? data)
    {
        if (data == null || data.Count == 0)
        {
            return null;
        }

        var sanitized = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in data)
        {
            sanitized[key] = IsSensitiveHealthKey(key)
                ? JsonSerializer.SerializeToElement("[redacted]")
                : SanitizeHealthValue(value);
        }

        return sanitized;
    }

    private static bool IsSensitiveHealthKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        return normalized == "config" ||
               normalized.EndsWith("configuration", StringComparison.Ordinal) ||
               normalized.EndsWith("secret", StringComparison.Ordinal) ||
               normalized.EndsWith("token", StringComparison.Ordinal) ||
               normalized.EndsWith("password", StringComparison.Ordinal) ||
               normalized.EndsWith("connectionstring", StringComparison.Ordinal);
    }

    private static JsonElement SanitizeHealthValue(object? value)
        => value switch
        {
            null => JsonSerializer.SerializeToElement((string?)null),
            string stringValue => JsonSerializer.SerializeToElement(stringValue),
            bool boolValue => JsonSerializer.SerializeToElement(boolValue),
            byte byteValue => JsonSerializer.SerializeToElement(byteValue),
            sbyte sbyteValue => JsonSerializer.SerializeToElement(sbyteValue),
            short shortValue => JsonSerializer.SerializeToElement(shortValue),
            ushort ushortValue => JsonSerializer.SerializeToElement(ushortValue),
            int intValue => JsonSerializer.SerializeToElement(intValue),
            uint uintValue => JsonSerializer.SerializeToElement(uintValue),
            long longValue => JsonSerializer.SerializeToElement(longValue),
            ulong ulongValue => JsonSerializer.SerializeToElement(ulongValue),
            float floatValue => JsonSerializer.SerializeToElement(floatValue),
            double doubleValue => JsonSerializer.SerializeToElement(doubleValue),
            decimal decimalValue => JsonSerializer.SerializeToElement(decimalValue),
            _ => JsonSerializer.SerializeToElement(value.ToString())
        };
}

/// <summary>
/// Response model for production health metrics.
/// </summary>
internal sealed class ProductionHealthResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the system is healthy.
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    /// <summary>
    /// Gets or sets the overall status.
    /// </summary>
    [JsonPropertyName("overallStatus")]
    public required string OverallStatus { get; set; }

    /// <summary>
    /// Gets or sets the detailed metrics.
    /// </summary>
    [JsonPropertyName("metrics")]
    public required ProductionHealthMetrics Metrics { get; set; }

    /// <summary>
    /// Gets or sets active alert conditions.
    /// </summary>
    [JsonPropertyName("alertConditions")]
    public required List<string> AlertConditions { get; set; }

    /// <summary>
    /// Gets or sets the active license summary.
    /// </summary>
    [JsonPropertyName("license")]
    public required LicenseHealthSummary License { get; set; }

    /// <summary>
    /// Gets or sets the last updated timestamp.
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public required DateTimeOffset LastUpdated { get; set; }
}

/// <summary>
/// Response model for connection pool metrics.
/// </summary>
internal sealed class ConnectionPoolMetricsResponse
{
    /// <summary>
    /// Gets or sets the utilization ratio.
    /// </summary>
    [JsonPropertyName("utilization")]
    public required double Utilization { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether utilization data is available.
    /// </summary>
    [JsonPropertyName("hasUtilizationData")]
    public required bool HasUtilizationData { get; set; }

    /// <summary>
    /// Gets or sets the utilization availability status.
    /// </summary>
    [JsonPropertyName("utilizationStatus")]
    public required string UtilizationStatus { get; set; }

    /// <summary>
    /// Gets or sets the utilization percentage string.
    /// </summary>
    [JsonPropertyName("utilizationPercentage")]
    public required string UtilizationPercentage { get; set; }

    /// <summary>
    /// Gets or sets the total failures count.
    /// </summary>
    [JsonPropertyName("totalFailures")]
    public required long TotalFailures { get; set; }

    /// <summary>
    /// Gets or sets the total timeouts count.
    /// </summary>
    [JsonPropertyName("totalTimeouts")]
    public required long TotalTimeouts { get; set; }

    /// <summary>
    /// Gets or sets the connection pool health status.
    /// </summary>
    [JsonPropertyName("healthStatus")]
    public required string HealthStatus { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the connection pool is healthy.
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    /// <summary>
    /// Gets query admission-control telemetry when the active provider exposes it.
    /// </summary>
    [JsonPropertyName("queryAdmission")]
    public QueryAdmissionMetricsResponse? QueryAdmission { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Response model for provider-level query admission metrics.
/// </summary>
internal sealed class QueryAdmissionMetricsResponse
{
    /// <summary>
    /// Gets a value indicating whether adaptive admission is enabled.
    /// </summary>
    [JsonPropertyName("adaptiveEnabled")]
    public required bool AdaptiveEnabled { get; set; }

    /// <summary>
    /// Gets the current logical concurrent query limit.
    /// </summary>
    [JsonPropertyName("currentLimit")]
    public required int CurrentLimit { get; set; }

    /// <summary>
    /// Gets the minimum adaptive logical query limit.
    /// </summary>
    [JsonPropertyName("minLimit")]
    public required int MinLimit { get; set; }

    /// <summary>
    /// Gets the maximum logical query limit.
    /// </summary>
    [JsonPropertyName("maxLimit")]
    public required int MaxLimit { get; set; }

    /// <summary>
    /// Gets the number of requests currently holding a query slot.
    /// </summary>
    [JsonPropertyName("inFlight")]
    public required int InFlight { get; set; }

    /// <summary>
    /// Gets the number of query slots currently available.
    /// </summary>
    [JsonPropertyName("availableSlots")]
    public required int AvailableSlots { get; set; }

    /// <summary>
    /// Gets the number of requests waiting on a query slot.
    /// </summary>
    [JsonPropertyName("queuedWaiters")]
    public required int QueuedWaiters { get; set; }

    /// <summary>
    /// Gets the adaptive controller target database lease duration in milliseconds.
    /// </summary>
    [JsonPropertyName("targetDurationMs")]
    public required double TargetDurationMs { get; set; }

    /// <summary>
    /// Gets the observed database lease duration EWMA in milliseconds.
    /// </summary>
    [JsonPropertyName("durationEwmaMs")]
    public required double DurationEwmaMs { get; set; }

    /// <summary>
    /// Gets the observed admission queue wait EWMA in milliseconds.
    /// </summary>
    [JsonPropertyName("queueWaitEwmaMs")]
    public required double QueueWaitEwmaMs { get; set; }

    /// <summary>
    /// Gets the number of logical target-limit changes.
    /// </summary>
    [JsonPropertyName("adjustmentCount")]
    public required long AdjustmentCount { get; set; }

    /// <summary>
    /// Gets the most recent target-limit adjustment direction.
    /// </summary>
    [JsonPropertyName("lastAdjustmentDirection")]
    public required string LastAdjustmentDirection { get; set; }

    internal static QueryAdmissionMetricsResponse FromSnapshot(QueryConcurrencyGateSnapshot snapshot)
        => new()
        {
            AdaptiveEnabled = snapshot.AdaptiveEnabled,
            CurrentLimit = snapshot.CurrentLimit,
            MinLimit = snapshot.MinLimit,
            MaxLimit = snapshot.MaxLimit,
            InFlight = snapshot.InFlight,
            AvailableSlots = snapshot.AvailableSlots,
            QueuedWaiters = snapshot.QueuedWaiters,
            TargetDurationMs = snapshot.TargetDurationMs,
            DurationEwmaMs = snapshot.DurationEwmaMs,
            QueueWaitEwmaMs = snapshot.QueueWaitEwmaMs,
            AdjustmentCount = snapshot.AdjustmentCount,
            LastAdjustmentDirection = snapshot.LastAdjustmentDirection
        };
}

/// <summary>
/// Response model for cache metrics.
/// </summary>
internal sealed class CacheMetricsResponse
{
    /// <summary>
    /// Gets or sets the cache hit ratio.
    /// </summary>
    [JsonPropertyName("hitRatio")]
    public required double HitRatio { get; set; }

    /// <summary>
    /// Gets or sets the hit ratio percentage string.
    /// </summary>
    [JsonPropertyName("hitRatioPercentage")]
    public required string HitRatioPercentage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cache is healthy.
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    /// <summary>
    /// Gets or sets cache recommendations.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public required List<string> Recommendations { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Response model for resource metrics.
/// </summary>
internal sealed class ResourceMetricsResponse
{
    /// <summary>
    /// Gets or sets memory usage in bytes.
    /// </summary>
    [JsonPropertyName("memoryUsageBytes")]
    public required long MemoryUsageBytes { get; set; }

    /// <summary>
    /// Gets or sets memory usage in megabytes.
    /// </summary>
    [JsonPropertyName("memoryUsageMB")]
    public required long MemoryUsageMB { get; set; }

    /// <summary>
    /// Gets or sets memory pressure level.
    /// </summary>
    [JsonPropertyName("memoryPressureLevel")]
    public required string MemoryPressureLevel { get; set; }

    /// <summary>
    /// Gets or sets garbage collection information.
    /// </summary>
    [JsonPropertyName("gcInfo")]
    public required ResourceGcInfoResponse GcInfo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether resources are healthy.
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Response model for garbage collection metrics.
/// </summary>
internal sealed class ResourceGcInfoResponse
{
    /// <summary>
    /// Gets or sets the number of generation 0 collections.
    /// </summary>
    [JsonPropertyName("gen0Collections")]
    public required int Gen0Collections { get; set; }

    /// <summary>
    /// Gets or sets the number of generation 1 collections.
    /// </summary>
    [JsonPropertyName("gen1Collections")]
    public required int Gen1Collections { get; set; }

    /// <summary>
    /// Gets or sets the number of generation 2 collections.
    /// </summary>
    [JsonPropertyName("gen2Collections")]
    public required int Gen2Collections { get; set; }

    /// <summary>
    /// Gets or sets the total memory observed during the snapshot.
    /// </summary>
    [JsonPropertyName("totalMemory")]
    public required long TotalMemory { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the GC snapshot was captured.
    /// </summary>
    [JsonPropertyName("lastGen0Time")]
    public required DateTimeOffset LastGen0Time { get; set; }
}

/// <summary>
/// Response model for alerts.
/// </summary>
internal sealed class AlertsResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether there are active alerts.
    /// </summary>
    [JsonPropertyName("hasActiveAlerts")]
    public required bool HasActiveAlerts { get; set; }

    /// <summary>
    /// Gets or sets the alert count.
    /// </summary>
    [JsonPropertyName("alertCount")]
    public required int AlertCount { get; set; }

    /// <summary>
    /// Gets or sets the list of alerts.
    /// </summary>
    [JsonPropertyName("alerts")]
    public required List<AlertCondition> Alerts { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Alert condition model.
/// </summary>
internal sealed class AlertCondition
{
    /// <summary>
    /// Gets or sets the alert condition text.
    /// </summary>
    [JsonPropertyName("condition")]
    public required string Condition { get; set; }

    /// <summary>
    /// Gets or sets the alert severity.
    /// </summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Response model for upload queue metrics.
/// </summary>
internal sealed class UploadQueueMetricsResponse
{
    /// <summary>
    /// Gets or sets the current queue depth.
    /// </summary>
    [JsonPropertyName("queueDepth")]
    public required int QueueDepth { get; set; }

    /// <summary>
    /// Gets or sets the maximum queue depth.
    /// </summary>
    [JsonPropertyName("maxQueueDepth")]
    public required int MaxQueueDepth { get; set; }

    /// <summary>
    /// Gets or sets the number of active uploads.
    /// </summary>
    [JsonPropertyName("activeUploads")]
    public required int ActiveUploads { get; set; }

    /// <summary>
    /// Gets or sets the maximum concurrent uploads.
    /// </summary>
    [JsonPropertyName("maxConcurrentUploads")]
    public required int MaxConcurrentUploads { get; set; }

    /// <summary>
    /// Gets or sets the queue utilization ratio.
    /// </summary>
    [JsonPropertyName("queueUtilization")]
    public required double QueueUtilization { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the upload queue is healthy.
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Response model for comprehensive health status.
/// </summary>
internal sealed class ComprehensiveHealthResponse
{
    /// <summary>
    /// Gets or sets the overall health status.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    /// <summary>
    /// Gets or sets the total duration of health checks.
    /// </summary>
    [JsonPropertyName("totalDuration")]
    public required TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Gets or sets the health check entries.
    /// </summary>
    [JsonPropertyName("entries")]
    public required Dictionary<string, HealthCheckEntryResponse> Entries { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Response model for individual health check entry.
/// </summary>
internal sealed class HealthCheckEntryResponse
{
    /// <summary>
    /// Gets or sets the health status.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    /// <summary>
    /// Gets or sets the check duration.
    /// </summary>
    [JsonPropertyName("duration")]
    public required TimeSpan Duration { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the exception message if any.
    /// </summary>
    [JsonPropertyName("exception")]
    public string? Exception { get; set; }

    /// <summary>
    /// Gets or sets additional data.
    /// </summary>
    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement>? Data { get; set; }
}

/// <summary>
/// Response model for database resilience metrics.
/// </summary>
internal sealed class DatabaseResilienceMetricsResponse
{
    /// <summary>
    /// Gets or sets connection pool metrics.
    /// </summary>
    [JsonPropertyName("connectionPool")]
    public required DatabaseConnectionPoolMetrics ConnectionPool { get; set; }

    /// <summary>
    /// Gets or sets resilience status.
    /// </summary>
    [JsonPropertyName("resilience")]
    public required DatabaseResilienceStatus Resilience { get; set; }

    /// <summary>
    /// Gets or sets alert conditions.
    /// </summary>
    [JsonPropertyName("alerts")]
    public required List<string> Alerts { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Database connection pool metrics.
/// </summary>
internal sealed class DatabaseConnectionPoolMetrics
{
    /// <summary>
    /// Gets or sets the utilization ratio.
    /// </summary>
    [JsonPropertyName("utilization")]
    public required double Utilization { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether utilization data is available.
    /// </summary>
    [JsonPropertyName("hasUtilizationData")]
    public required bool HasUtilizationData { get; set; }

    /// <summary>
    /// Gets or sets the utilization availability status.
    /// </summary>
    [JsonPropertyName("utilizationStatus")]
    public required string UtilizationStatus { get; set; }

    /// <summary>
    /// Gets or sets the utilization percentage.
    /// </summary>
    [JsonPropertyName("utilizationPercentage")]
    public required string UtilizationPercentage { get; set; }

    /// <summary>
    /// Gets or sets the number of active connections.
    /// </summary>
    [JsonPropertyName("activeConnections")]
    public required int ActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets the total failures.
    /// </summary>
    [JsonPropertyName("totalFailures")]
    public required long TotalFailures { get; set; }

    /// <summary>
    /// Gets or sets the total timeouts.
    /// </summary>
    [JsonPropertyName("totalTimeouts")]
    public required long TotalTimeouts { get; set; }

    /// <summary>
    /// Gets or sets the connection pool health status.
    /// </summary>
    [JsonPropertyName("healthStatus")]
    public required string HealthStatus { get; set; }

    /// <summary>
    /// Gets or sets whether the connection pool is healthy.
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }
}

/// <summary>
/// Database resilience status.
/// </summary>
internal sealed class DatabaseResilienceStatus
{
    /// <summary>
    /// Gets or sets whether circuit breakers are enabled.
    /// </summary>
    [JsonPropertyName("circuitBreakerEnabled")]
    public required bool CircuitBreakerEnabled { get; set; }

    /// <summary>
    /// Gets or sets the connection failures count.
    /// </summary>
    [JsonPropertyName("connectionFailures")]
    public required long ConnectionFailures { get; set; }

    /// <summary>
    /// Gets or sets the query failures count.
    /// </summary>
    [JsonPropertyName("queryFailures")]
    public required long QueryFailures { get; set; }

    /// <summary>
    /// Gets or sets the error rate.
    /// </summary>
    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; set; }

    /// <summary>
    /// Gets or sets the error rate percentage.
    /// </summary>
    [JsonPropertyName("errorRatePercentage")]
    public required string ErrorRatePercentage { get; set; }
}
