// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Authentication;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Health check endpoints with full AOT compatibility
/// </summary>

internal static class HealthEndpoints
{
    private static readonly string[] NonGetMethods =
    [
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Delete,
        HttpMethods.Patch
    ];

    /// <summary>
    /// Configure health endpoints using AOT-compatible routing
    /// </summary>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.MapMethods("/healthz/live", [HttpMethods.Get], HandleLivenessProbe)
            .WithDisplayName("Liveness Probe");
        _ = endpoints.MapMethods("/healthz/live", NonGetMethods, HandleGetMethodNotAllowed)
            .WithDisplayName("Liveness Probe Method Not Allowed");

        _ = endpoints.MapMethods("/healthz/ready", [HttpMethods.Get], HandleReadinessProbe)
            .WithDisplayName("Readiness Probe");
        _ = endpoints.MapMethods("/healthz/ready", NonGetMethods, HandleGetMethodNotAllowed)
            .WithDisplayName("Readiness Probe Method Not Allowed");

        // PERFORMANCE OPTIMIZATION: Add performance metrics endpoint for monitoring
        var metricsEndpoint = endpoints.MapMethods("/healthz/metrics", [HttpMethods.Get], HandlePerformanceMetrics)
            .WithDisplayName("Performance Metrics")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        _ = endpoints.MapMethods("/healthz/metrics", NonGetMethods, HandleGetMethodNotAllowed)
            .WithDisplayName("Performance Metrics Method Not Allowed");

        metricsEndpoint.RequireAdminAuthorization();
    }

    /// <summary>
    /// Handle liveness probe - indicates if the process is running
    /// </summary>
    private static async Task HandleLivenessProbe(HttpContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Healthy");
    }

    /// <summary>
    /// Handle readiness probe - indicates if the service is ready to accept traffic
    /// Delegates health checking to dedicated service for better separation of concerns
    /// </summary>
    private static async Task HandleReadinessProbe(HttpContext context)
    {
        // Delegate health checking to dedicated service
        IReadinessCheckService readinessCheckService = context.RequestServices.GetRequiredService<IReadinessCheckService>();
        ReadinessResult result = await readinessCheckService.CheckReadinessAsync(context.RequestAborted);

        // Convert health check result to HTTP response
        await WriteHealthCheckResponse(context, result);
    }

    /// <summary>
    /// PERFORMANCE OPTIMIZATION: Endpoint to expose performance metrics
    /// Provides insights into query performance and system health
    /// </summary>
    private static async Task<IResult> HandlePerformanceMetrics(
        IDatabasePerformanceMetricsProvider databaseMetricsProvider,
        IReadinessCheckService readinessCheckService,
        ILoggerFactory loggerFactory,
        ICacheRefreshCoordinator? refreshCoordinator,
        CancellationToken cancellationToken)
    {
        try
        {
            var readiness = await readinessCheckService.CheckReadinessAsync(cancellationToken);
            var performanceMetrics = databaseMetricsProvider.GetMetrics();

            // Basic memory and GC info
            var memoryInfo = GC.GetGCMemoryInfo();
            var totalMemory = GC.GetTotalMemory(false);

            var response = new HealthPerformanceMetricsResponse
            {
                Timestamp = DateTimeOffset.UtcNow,
                Status = readiness.IsReady ? "healthy" : "not_ready",
                PerformanceScore = CalculateBasicPerformanceScore(totalMemory),
                Metrics = new HealthPerformanceMetrics
                {
                    QueryPerformance = performanceMetrics,
                    Memory = new HealthMemoryMetrics
                    {
                        TotalBytes = totalMemory,
                        HeapSizeBytes = memoryInfo.HeapSizeBytes,
                        MemoryLoadBytes = memoryInfo.MemoryLoadBytes,
                        TotalAvailableMemoryBytes = memoryInfo.TotalAvailableMemoryBytes
                    },
                    GcInfo = new HealthGcMetrics
                    {
                        Gen0Collections = GC.CollectionCount(0),
                        Gen1Collections = GC.CollectionCount(1),
                        Gen2Collections = GC.CollectionCount(2)
                    },
                    CacheRefresh = refreshCoordinator is not null
                        ? new HealthCacheRefreshMetrics
                        {
                            QueueDepth = refreshCoordinator.QueueDepth,
                            SuccessCount = refreshCoordinator.SuccessCount,
                            FailureCount = refreshCoordinator.FailureCount,
                            SkippedCount = refreshCoordinator.SkippedCount
                        }
                        : null
                }
            };

            return Results.Json(
                response,
                HealthJsonContext.Default.HealthPerformanceMetricsResponse,
                statusCode: readiness.StatusCode);
        }
        catch (Exception ex)
        {
            HealthEndpointsLog.PerformanceMetricsFailed(loggerFactory.CreateLogger("Honua.HealthCheck"), ex);

            var response = new HealthPerformanceErrorResponse
            {
                Status = "error",
                Message = "Failed to retrieve performance metrics",
                Details = "See server logs for details."
            };

            return Results.Json(
                response,
                HealthJsonContext.Default.HealthPerformanceErrorResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Calculates overall performance score based on comprehensive metrics
    /// </summary>
    private static double CalculateBasicPerformanceScore(long totalMemoryBytes)
    {
        try
        {
            double score = 100.0;
            var memoryMb = totalMemoryBytes / (1024 * 1024);

            // Deduct points based on memory usage
            score -= memoryMb switch
            {
                > 1024 => 20, // > 1GB
                > 500 => 10,  // > 500MB
                > 250 => 5,   // > 250MB
                _ => 0
            };

            // GC collection frequency
            var gen2Collections = GC.CollectionCount(2);
            if (gen2Collections > 100)
            {
                score -= 10;
            }
            else if (gen2Collections > 50)
            {
                score -= 5;
            }

            return Math.Max(0, Math.Min(100, score));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Writes health check result to HTTP response
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="result">Health check result</param>
    private static async Task WriteHealthCheckResponse(HttpContext context, ReadinessResult result)
    {
        context.Response.StatusCode = result.StatusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(result.IsReady ? "Ready" : "Not Ready");
    }

    private static Task HandleGetMethodNotAllowed(HttpContext context)
    {
        context.Response.Headers["Allow"] = HttpMethods.Get;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return context.Response.CompleteAsync();
    }
}
