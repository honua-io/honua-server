// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Endpoints for exposing performance metrics and telemetry data to monitoring tools.
/// </summary>
public static class MetricsEndpoints
{
    private static ILogger GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Honua.Monitoring");

    /// <summary>
    /// Maps metrics endpoints to the application with formal API versioning.
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication MapMetricsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/metrics")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Metrics")
            .RequireAdminAuthorization();

        group.MapGet("/health", GetHealthMetrics)
            .WithName("GetHealthMetrics")
            .WithSummary("Get basic health metrics")
            .Produces<HealthMetrics>();

        group.MapGet("/performance", GetPerformanceMetrics)
            .WithName("GetPerformanceMetrics")
            .WithSummary("Get detailed performance metrics")
            .Produces<PerformanceMetricsResponse>();

        group.MapGet("/database", GetDatabaseMetrics)
            .WithName("GetDatabaseMetrics")
            .WithSummary("Get database performance metrics")
            .Produces<DatabaseMetrics>();

        group.MapGet("/cache", GetCacheMetrics)
            .WithName("GetCacheMetrics")
            .WithSummary("Get cache performance metrics")
            .Produces<CacheMetrics>();

        group.MapGet("/memory", GetMemoryMetrics)
            .WithName("GetMemoryMetrics")
            .WithSummary("Get memory usage metrics")
            .Produces<MemoryUsage>();

        return app;
    }

    /// <summary>
    /// Gets basic health metrics.
    /// </summary>
    private static IResult GetHealthMetrics(HttpContext context, [FromServices] MigrationState migrationState)
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();
            var migration = new MigrationHealthMetrics
            {
                Status = GetMigrationStatusLabel(migrationState.Status),
                IsReady = migrationState.IsReady,
                IsFailed = migrationState.IsFailed,
                Message = GetMigrationStatusMessage(migrationState)
            };

            var healthMetrics = new HealthMetrics
            {
                Status = migrationState.IsFailed
                    ? "unhealthy"
                    : migrationState.IsReady
                        ? "healthy"
                        : "initializing",
                Timestamp = DateTimeOffset.UtcNow,
                MemoryUsageMB = memoryUsage.AllocatedBytes / (1024.0 * 1024.0),
                MemoryPressurePercent = memoryUsage.MemoryPressurePercentage,
                GCCollections = memoryUsage.TotalGCCollections,
                Migration = migration
            };

            return Results.Ok(healthMetrics);
        }
        catch (Exception ex)
        {
            MonitoringLog.HealthMetricsFailed(GetLogger(context), ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Failed to retrieve health metrics. See server logs for details.");
        }
    }

    private static string GetMigrationStatusLabel(MigrationLifecycleStatus status) =>
        status switch
        {
            MigrationLifecycleStatus.Running => "running",
            MigrationLifecycleStatus.Succeeded => "succeeded",
            MigrationLifecycleStatus.Skipped => "skipped",
            MigrationLifecycleStatus.Failed => "failed",
            _ => "unknown"
        };

    private static string? GetMigrationStatusMessage(MigrationState migrationState)
    {
        if (!string.IsNullOrWhiteSpace(migrationState.StatusMessage))
        {
            return migrationState.StatusMessage;
        }

        return migrationState.Status switch
        {
            MigrationLifecycleStatus.Running => "Database migrations in progress.",
            MigrationLifecycleStatus.Failed => "Database migrations failed.",
            MigrationLifecycleStatus.Unknown => "Database migrations not completed.",
            _ => null
        };
    }

    /// <summary>
    /// Gets comprehensive performance metrics.
    /// </summary>
    private static IResult GetPerformanceMetrics(
        HttpContext context,
        [FromServices] IHttpRequestMetricsSnapshotProvider httpMetricsProvider)
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();
            var httpSnapshot = httpMetricsProvider.GetHttpRequestMetricsSnapshot();
            var serverErrorRate = httpSnapshot.TotalRequests > 0
                ? (double)httpSnapshot.TotalServerErrors / httpSnapshot.TotalRequests
                : 0.0;

            var response = new PerformanceMetricsResponse
            {
                Timestamp = DateTimeOffset.UtcNow,
                Memory = memoryUsage,
                SystemInfo = new SystemInfo
                {
                    ProcessorCount = Environment.ProcessorCount,
                    MachineName = Environment.MachineName,
                    WorkingSet = Environment.WorkingSet,
                    FrameworkVersion = Environment.Version.ToString()
                },
                Http = new HttpRequestMetrics
                {
                    TotalRequests = httpSnapshot.TotalRequests,
                    TotalServerErrors = httpSnapshot.TotalServerErrors,
                    TotalClientErrors = httpSnapshot.TotalClientErrors,
                    ActiveRequests = httpSnapshot.ActiveRequests,
                    AvgDurationMs = httpSnapshot.AvgDurationMs,
                    MaxDurationMs = httpSnapshot.MaxDurationMs,
                    P95DurationMs = httpSnapshot.P95DurationMs,
                    SlowRequests = httpSnapshot.SlowRequestCount,
                    SlowRequestThresholdMs = httpSnapshot.SlowRequestThresholdMs,
                    ServerErrorRate = serverErrorRate
                }
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            MonitoringLog.PerformanceMetricsFailed(GetLogger(context), ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Failed to retrieve performance metrics. See server logs for details.");
        }
    }

    /// <summary>
    /// Gets database-specific performance metrics.
    /// </summary>
    private static IResult GetDatabaseMetrics(
        HttpContext context,
        [FromServices] IDatabasePerformanceMetricsProvider databaseMetricsProvider)
    {
        try
        {
            var snapshot = databaseMetricsProvider.GetMetrics();

            var databaseMetrics = new DatabaseMetrics
            {
                Timestamp = DateTimeOffset.UtcNow,
                CacheHitRate = snapshot.CacheHitRate,
                CacheHits = snapshot.CacheHits,
                CacheMisses = snapshot.CacheMisses,
                Operations = snapshot.Operations.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new DatabaseOperationMetrics
                    {
                        Count = kvp.Value.Count,
                        TotalTimeMs = kvp.Value.TotalTimeMs,
                        MaxTimeMs = kvp.Value.MaxTimeMs,
                        AvgTimeMs = kvp.Value.AvgTimeMs
                    })
            };

            return Results.Ok(databaseMetrics);
        }
        catch (Exception ex)
        {
            MonitoringLog.DatabaseMetricsFailed(GetLogger(context), ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Failed to retrieve database metrics. See server logs for details.");
        }
    }

    /// <summary>
    /// Gets cache-specific performance metrics.
    /// </summary>
    private static IResult GetCacheMetrics(
        HttpContext context,
        [FromServices] ICacheMetricsSnapshotProvider cacheMetricsSnapshotProvider)
    {
        try
        {
            var snapshot = cacheMetricsSnapshotProvider.GetCacheMetricsSnapshot();
            var totalRequests = snapshot.TotalHits + snapshot.TotalMisses;

            var cacheMetrics = new CacheMetrics
            {
                Timestamp = DateTimeOffset.UtcNow,
                TotalRequests = totalRequests,
                HitRatio = totalRequests > 0
                    ? (double)snapshot.TotalHits / totalRequests
                    : 0.0,
                Types = snapshot.Types.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new CacheTypeMetrics
                    {
                        Hits = kvp.Value.Hits,
                        Misses = kvp.Value.Misses,
                        Evictions = kvp.Value.Evictions
                    })
            };

            return Results.Ok(cacheMetrics);
        }
        catch (Exception ex)
        {
            MonitoringLog.CacheMetricsFailed(GetLogger(context), ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Failed to retrieve cache metrics. See server logs for details.");
        }
    }

    /// <summary>
    /// Gets current memory usage metrics.
    /// </summary>
    private static IResult GetMemoryMetrics(HttpContext context)
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();
            return Results.Ok(memoryUsage);
        }
        catch (Exception ex)
        {
            MonitoringLog.MemoryMetricsFailed(GetLogger(context), ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Failed to retrieve memory metrics. See server logs for details.");
        }
    }
}
