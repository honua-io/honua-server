// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Endpoints for exposing performance metrics and telemetry data to monitoring tools.
/// </summary>
public static class MetricsEndpoints
{
    /// <summary>
    /// Maps metrics endpoints to the application.
    /// </summary>
    /// <param name="app">The web application</param>
    /// <returns>The web application for chaining</returns>
    public static WebApplication MapMetricsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/metrics")
            .WithTags("Metrics");

        // Public health metrics (no auth required)
        group.MapGet("/health", GetHealthMetrics)
            .WithName("GetHealthMetrics")
            .WithSummary("Get basic health metrics")
            .Produces<HealthMetrics>();

        // Detailed metrics (auth required in production)
        var detailedGroup = group.RequireAuthorization();

        detailedGroup.MapGet("/performance", GetPerformanceMetrics)
            .WithName("GetPerformanceMetrics")
            .WithSummary("Get detailed performance metrics")
            .Produces<PerformanceMetricsResponse>();

        detailedGroup.MapGet("/database", GetDatabaseMetrics)
            .WithName("GetDatabaseMetrics")
            .WithSummary("Get database performance metrics")
            .Produces<DatabaseMetrics>();

        detailedGroup.MapGet("/cache", GetCacheMetrics)
            .WithName("GetCacheMetrics")
            .WithSummary("Get cache performance metrics")
            .Produces<CacheMetrics>();

        detailedGroup.MapGet("/memory", GetMemoryMetrics)
            .WithName("GetMemoryMetrics")
            .WithSummary("Get memory usage metrics")
            .Produces<MemoryUsage>();

        // Prometheus-compatible endpoint
        group.MapGet("/prometheus", GetPrometheusMetrics)
            .WithName("GetPrometheusMetrics")
            .WithSummary("Get metrics in Prometheus format")
            .Produces<string>(200, "text/plain");

        return app;
    }

    /// <summary>
    /// Gets basic health metrics without authentication.
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
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Failed to retrieve health metrics");
        }
    }

    /// <summary>
    /// Gets comprehensive performance metrics.
    /// </summary>
    private static IResult GetPerformanceMetrics()
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();

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
                }
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Failed to retrieve performance metrics");
        }
    }

    /// <summary>
    /// Gets database-specific performance metrics.
    /// </summary>
    private static IResult GetDatabaseMetrics()
    {
        try
        {
            // Get metrics from PostgresFeatureStore's PerformanceMetrics
            var postgresMetrics = Honua.Postgres.Features.FeatureStore.PostgresFeatureStore.PerformanceMetrics.GetMetrics();

            var databaseMetrics = new DatabaseMetrics
            {
                Timestamp = DateTimeOffset.UtcNow,
                CacheHitRate = (double)postgresMetrics.GetValueOrDefault("cache_hit_rate", 0.0),
                CacheHits = (long)postgresMetrics.GetValueOrDefault("cache_hits", 0L),
                CacheMisses = (long)postgresMetrics.GetValueOrDefault("cache_misses", 0L),
                Operations = postgresMetrics
                    .Where(kvp => kvp.Key.EndsWith("_count") && !kvp.Key.Contains("cache"))
                    .ToDictionary(
                        kvp => kvp.Key.Replace("_count", ""),
                        kvp => new DatabaseOperationMetrics
                        {
                            Count = (long)kvp.Value,
                            TotalTimeMs = (long)postgresMetrics.GetValueOrDefault($"{kvp.Key.Replace("_count", "")}_total_ms", 0L),
                            MaxTimeMs = (long)postgresMetrics.GetValueOrDefault($"{kvp.Key.Replace("_count", "")}_max_ms", 0L),
                            AvgTimeMs = (double)postgresMetrics.GetValueOrDefault($"{kvp.Key.Replace("_count", "")}_avg_ms", 0.0)
                        })
            };

            return Results.Ok(databaseMetrics);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Failed to retrieve database metrics");
        }
    }

    /// <summary>
    /// Gets cache-specific performance metrics.
    /// </summary>
    private static IResult GetCacheMetrics()
    {
        try
        {
            // This would be enhanced with actual cache implementation metrics
            var cacheMetrics = new CacheMetrics
            {
                Timestamp = DateTimeOffset.UtcNow,
                TotalRequests = 0, // Would come from IPerformanceMonitor
                HitRatio = 0.0,   // Would be calculated from hit/miss counts
                Types = new Dictionary<string, CacheTypeMetrics>()
            };

            return Results.Ok(cacheMetrics);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
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
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Failed to retrieve memory metrics");
        }
    }

    /// <summary>
    /// Gets metrics in Prometheus format for monitoring integrations.
    /// </summary>
    private static IResult GetPrometheusMetrics()
    {
        try
        {
            var memoryUsage = MemoryMonitor.GetMemoryUsage();
            var postgresMetrics = Honua.Postgres.Features.FeatureStore.PostgresFeatureStore.PerformanceMetrics.GetMetrics();

            var prometheus = new PrometheusFormatter();

            // Memory metrics
            prometheus.AddGauge("honua_memory_allocated_bytes", memoryUsage.AllocatedBytes, "Currently allocated memory in bytes");
            prometheus.AddGauge("honua_memory_heap_size_bytes", memoryUsage.HeapSizeBytes, "Heap size in bytes");
            prometheus.AddGauge("honua_memory_pressure_percent", memoryUsage.MemoryPressurePercentage, "Memory pressure percentage");

            // GC metrics
            prometheus.AddCounter("honua_gc_collections_total", memoryUsage.Gen0Collections, "generation", "0");
            prometheus.AddCounter("honua_gc_collections_total", memoryUsage.Gen1Collections, "generation", "1");
            prometheus.AddCounter("honua_gc_collections_total", memoryUsage.Gen2Collections, "generation", "2");

            // Database metrics
            prometheus.AddGauge("honua_database_cache_hit_rate", (double)postgresMetrics.GetValueOrDefault("cache_hit_rate", 0.0), "Database cache hit rate");
            prometheus.AddCounter("honua_database_cache_hits_total", (long)postgresMetrics.GetValueOrDefault("cache_hits", 0L), "Database cache hits");
            prometheus.AddCounter("honua_database_cache_misses_total", (long)postgresMetrics.GetValueOrDefault("cache_misses", 0L), "Database cache misses");

            foreach (var (key, value) in postgresMetrics.Where(kvp => kvp.Key.EndsWith("_count")))
            {
                var operationType = key.Replace("_count", "");
                prometheus.AddCounter($"honua_database_operations_total", (long)value, "operation", operationType);

                if (postgresMetrics.TryGetValue($"{operationType}_total_ms", out var totalMs))
                {
                    prometheus.AddCounter($"honua_database_operation_duration_ms_total", (long)totalMs, "operation", operationType);
                }
            }

            return Results.Text(prometheus.ToString(), "text/plain");
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Failed to generate Prometheus metrics");
        }
    }
}
