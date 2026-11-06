// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Middleware;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Monitoring;

internal static class ProductionMonitoringEndpoints
{
    public static void MapProductionMonitoringEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/monitoring")
            .WithTags("Monitoring")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic problem response below.
            MonitoringLog.ComprehensiveHealthEndpointFailed(logger, ex);
            return Results.Problem(
                title: "Health check failed",
                detail: "The comprehensive health report could not be generated. See server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and maps any failure
            // to a generic problem response below.
            MonitoringLog.DatabaseResilienceMetricsEndpointFailed(logger, ex);
            return Results.Problem(
                title: "Database resilience metrics failed",
                detail: "The database resilience metrics could not be generated. See server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

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

    // The projection is internal so the AOT regression test can call it directly.
    internal static Dictionary<string, JsonElement>? SanitizeHealthCheckData(
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
                ? JsonSerializer.SerializeToElement("[redacted]", MetricsJsonContext.Default.String)
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

    // Each arm passes its type info, because the parameterless serializer uses reflection and returns 500 under native AOT.
    private static JsonElement SanitizeHealthValue(object? value)
        => value switch
        {
            null => JsonSerializer.SerializeToElement((string?)null, MetricsJsonContext.Default.String),
            string stringValue => JsonSerializer.SerializeToElement(stringValue, MetricsJsonContext.Default.String),
            bool boolValue => JsonSerializer.SerializeToElement(boolValue, MetricsJsonContext.Default.Boolean),
            byte byteValue => JsonSerializer.SerializeToElement(byteValue, MetricsJsonContext.Default.Byte),
            sbyte sbyteValue => JsonSerializer.SerializeToElement(sbyteValue, MetricsJsonContext.Default.SByte),
            short shortValue => JsonSerializer.SerializeToElement(shortValue, MetricsJsonContext.Default.Int16),
            ushort ushortValue => JsonSerializer.SerializeToElement(ushortValue, MetricsJsonContext.Default.UInt16),
            int intValue => JsonSerializer.SerializeToElement(intValue, MetricsJsonContext.Default.Int32),
            uint uintValue => JsonSerializer.SerializeToElement(uintValue, MetricsJsonContext.Default.UInt32),
            long longValue => JsonSerializer.SerializeToElement(longValue, MetricsJsonContext.Default.Int64),
            ulong ulongValue => JsonSerializer.SerializeToElement(ulongValue, MetricsJsonContext.Default.UInt64),
            float floatValue => JsonSerializer.SerializeToElement(floatValue, MetricsJsonContext.Default.Single),
            double doubleValue => JsonSerializer.SerializeToElement(doubleValue, MetricsJsonContext.Default.Double),
            decimal decimalValue => JsonSerializer.SerializeToElement(decimalValue, MetricsJsonContext.Default.Decimal),
            _ => JsonSerializer.SerializeToElement(value.ToString(), MetricsJsonContext.Default.String)
        };
}

internal sealed class ProductionHealthResponse
{
    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    [JsonPropertyName("overallStatus")]
    public required string OverallStatus { get; set; }

    [JsonPropertyName("metrics")]
    public required ProductionHealthMetrics Metrics { get; set; }

    [JsonPropertyName("alertConditions")]
    public required List<string> AlertConditions { get; set; }

    [JsonPropertyName("license")]
    public required LicenseHealthSummary License { get; set; }

    [JsonPropertyName("lastUpdated")]
    public required DateTimeOffset LastUpdated { get; set; }
}

internal sealed class ConnectionPoolMetricsResponse
{
    [JsonPropertyName("utilization")]
    public required double Utilization { get; set; }

    [JsonPropertyName("hasUtilizationData")]
    public required bool HasUtilizationData { get; set; }

    [JsonPropertyName("utilizationStatus")]
    public required string UtilizationStatus { get; set; }

    [JsonPropertyName("utilizationPercentage")]
    public required string UtilizationPercentage { get; set; }

    [JsonPropertyName("totalFailures")]
    public required long TotalFailures { get; set; }

    [JsonPropertyName("totalTimeouts")]
    public required long TotalTimeouts { get; set; }

    [JsonPropertyName("healthStatus")]
    public required string HealthStatus { get; set; }

    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    [JsonPropertyName("queryAdmission")]
    public QueryAdmissionMetricsResponse? QueryAdmission { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class QueryAdmissionMetricsResponse
{
    [JsonPropertyName("adaptiveEnabled")]
    public required bool AdaptiveEnabled { get; set; }

    [JsonPropertyName("currentLimit")]
    public required int CurrentLimit { get; set; }

    [JsonPropertyName("minLimit")]
    public required int MinLimit { get; set; }

    [JsonPropertyName("maxLimit")]
    public required int MaxLimit { get; set; }

    [JsonPropertyName("inFlight")]
    public required int InFlight { get; set; }

    [JsonPropertyName("availableSlots")]
    public required int AvailableSlots { get; set; }

    [JsonPropertyName("queuedWaiters")]
    public required int QueuedWaiters { get; set; }

    [JsonPropertyName("targetDurationMs")]
    public required double TargetDurationMs { get; set; }

    [JsonPropertyName("durationEwmaMs")]
    public required double DurationEwmaMs { get; set; }

    [JsonPropertyName("queueWaitEwmaMs")]
    public required double QueueWaitEwmaMs { get; set; }

    [JsonPropertyName("adjustmentCount")]
    public required long AdjustmentCount { get; set; }

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

internal sealed class CacheMetricsResponse
{
    [JsonPropertyName("hitRatio")]
    public required double HitRatio { get; set; }

    [JsonPropertyName("hitRatioPercentage")]
    public required string HitRatioPercentage { get; set; }

    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    [JsonPropertyName("recommendations")]
    public required List<string> Recommendations { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class ResourceMetricsResponse
{
    [JsonPropertyName("memoryUsageBytes")]
    public required long MemoryUsageBytes { get; set; }

    [JsonPropertyName("memoryUsageMB")]
    public required long MemoryUsageMB { get; set; }

    [JsonPropertyName("memoryPressureLevel")]
    public required string MemoryPressureLevel { get; set; }

    [JsonPropertyName("gcInfo")]
    public required ResourceGcInfoResponse GcInfo { get; set; }

    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class ResourceGcInfoResponse
{
    [JsonPropertyName("gen0Collections")]
    public required int Gen0Collections { get; set; }

    [JsonPropertyName("gen1Collections")]
    public required int Gen1Collections { get; set; }

    [JsonPropertyName("gen2Collections")]
    public required int Gen2Collections { get; set; }

    [JsonPropertyName("totalMemory")]
    public required long TotalMemory { get; set; }

    [JsonPropertyName("lastGen0Time")]
    public required DateTimeOffset LastGen0Time { get; set; }
}

internal sealed class AlertsResponse
{
    [JsonPropertyName("hasActiveAlerts")]
    public required bool HasActiveAlerts { get; set; }

    [JsonPropertyName("alertCount")]
    public required int AlertCount { get; set; }

    [JsonPropertyName("alerts")]
    public required List<AlertCondition> Alerts { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class AlertCondition
{
    [JsonPropertyName("condition")]
    public required string Condition { get; set; }

    [JsonPropertyName("severity")]
    public required string Severity { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class UploadQueueMetricsResponse
{
    [JsonPropertyName("queueDepth")]
    public required int QueueDepth { get; set; }

    [JsonPropertyName("maxQueueDepth")]
    public required int MaxQueueDepth { get; set; }

    [JsonPropertyName("activeUploads")]
    public required int ActiveUploads { get; set; }

    [JsonPropertyName("maxConcurrentUploads")]
    public required int MaxConcurrentUploads { get; set; }

    [JsonPropertyName("queueUtilization")]
    public required double QueueUtilization { get; set; }

    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class ComprehensiveHealthResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("totalDuration")]
    public required TimeSpan TotalDuration { get; set; }

    [JsonPropertyName("entries")]
    public required Dictionary<string, HealthCheckEntryResponse> Entries { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class HealthCheckEntryResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("duration")]
    public required TimeSpan Duration { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("exception")]
    public string? Exception { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement>? Data { get; set; }
}

internal sealed class DatabaseResilienceMetricsResponse
{
    [JsonPropertyName("connectionPool")]
    public required DatabaseConnectionPoolMetrics ConnectionPool { get; set; }

    [JsonPropertyName("resilience")]
    public required DatabaseResilienceStatus Resilience { get; set; }

    [JsonPropertyName("alerts")]
    public required List<string> Alerts { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class DatabaseConnectionPoolMetrics
{
    [JsonPropertyName("utilization")]
    public required double Utilization { get; set; }

    [JsonPropertyName("hasUtilizationData")]
    public required bool HasUtilizationData { get; set; }

    [JsonPropertyName("utilizationStatus")]
    public required string UtilizationStatus { get; set; }

    [JsonPropertyName("utilizationPercentage")]
    public required string UtilizationPercentage { get; set; }

    [JsonPropertyName("activeConnections")]
    public required int ActiveConnections { get; set; }

    [JsonPropertyName("totalFailures")]
    public required long TotalFailures { get; set; }

    [JsonPropertyName("totalTimeouts")]
    public required long TotalTimeouts { get; set; }

    [JsonPropertyName("healthStatus")]
    public required string HealthStatus { get; set; }

    [JsonPropertyName("isHealthy")]
    public required bool IsHealthy { get; set; }
}

internal sealed class DatabaseResilienceStatus
{
    [JsonPropertyName("circuitBreakerEnabled")]
    public required bool CircuitBreakerEnabled { get; set; }

    [JsonPropertyName("connectionFailures")]
    public required long ConnectionFailures { get; set; }

    [JsonPropertyName("queryFailures")]
    public required long QueryFailures { get; set; }

    [JsonPropertyName("errorRate")]
    public required double ErrorRate { get; set; }

    [JsonPropertyName("errorRatePercentage")]
    public required string ErrorRatePercentage { get; set; }
}
