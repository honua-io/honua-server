// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Authentication;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Endpoints for exposing performance metrics and telemetry data to monitoring tools.
/// </summary>
public static class MetricsEndpoints
{
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
    private static IResult GetHealthMetrics()
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();

            var healthMetrics = new HealthMetrics
            {
                Status = "healthy",
                Timestamp = DateTimeOffset.UtcNow,
                MemoryUsageMB = memoryUsage.AllocatedBytes / (1024.0 * 1024.0),
                MemoryPressurePercent = memoryUsage.MemoryPressurePercentage,
                GCCollections = memoryUsage.TotalGCCollections
            };

            return Results.Ok(healthMetrics);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve health metrics");
        }
    }

    /// <summary>
    /// Gets comprehensive performance metrics.
    /// </summary>
    private static IResult GetPerformanceMetrics(IHttpRequestMetricsSnapshotProvider httpMetricsProvider)
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
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve performance metrics");
        }
    }

    /// <summary>
    /// Gets database-specific performance metrics.
    /// </summary>
    private static IResult GetDatabaseMetrics(IDatabasePerformanceMetricsProvider databaseMetricsProvider)
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
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve database metrics");
        }
    }

    /// <summary>
    /// Gets cache-specific performance metrics.
    /// </summary>
    private static IResult GetCacheMetrics(ICacheMetricsSnapshotProvider cacheMetricsSnapshotProvider)
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
                        Evictions = kvp.Value.Evictions,
                        AvgOperationTimeMs = 0.0
                    })
            };

            return Results.Ok(cacheMetrics);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve cache metrics");
        }
    }

    /// <summary>
    /// Gets current memory usage metrics.
    /// </summary>
    private static IResult GetMemoryMetrics()
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();
            return Results.Ok(memoryUsage);
        }
        catch (Exception)
        {
            return Results.Problem(
                detail: "See server logs for details.",
                statusCode: 500,
                title: "Failed to retrieve memory metrics");
        }
    }
}
