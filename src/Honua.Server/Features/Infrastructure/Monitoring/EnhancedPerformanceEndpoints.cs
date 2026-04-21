// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Enhanced performance monitoring endpoints providing detailed insights into
/// database query performance, resource usage, exception telemetry, and caching effectiveness.
/// </summary>
/// <remarks>
/// These endpoints provide real-time performance metrics to help identify optimization
/// opportunities and monitor the health of performance-critical systems.
/// </remarks>
internal static partial class EnhancedPerformanceEndpoints
{
    /// <summary>
    /// Registers enhanced performance monitoring endpoints with formal API versioning.
    /// </summary>
    /// <param name="app">Web application builder</param>
    public static void MapEnhancedPerformanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/performance/enhanced")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Enhanced Performance Monitoring")
            .RequireAdminAuthorization();

        // Database query performance endpoints
        group.MapGet("/database/query-performance", GetQueryPerformanceStatistics)
            .WithName("GetQueryPerformanceStatistics")
            .WithSummary("Get database query performance statistics")
            .WithDescription("Returns detailed statistics about query execution performance including slow query detection")
            .Produces<QueryPerformanceStatistics>(200)
            .Produces<ProblemDetails>(500);

        group.MapGet("/database/slow-queries", GetSlowQueries)
            .WithName("GetSlowQueries")
            .WithSummary("Get recent slow queries")
            .WithDescription("Returns details of recently detected slow queries for performance analysis")
            .Produces<SlowQueryResponse>(200)
            .Produces<ProblemDetails>(500);

        // Resource tracking endpoints
        group.MapGet("/resources/tracking", GetResourceTrackingStatistics)
            .WithName("GetResourceTrackingStatistics")
            .WithSummary("Get resource tracking statistics")
            .WithDescription("Returns statistics about resource allocation and potential leaks")
            .Produces<ResourceTrackingStatistics>(200)
            .Produces<ProblemDetails>(500);

        group.MapGet("/resources/potential-leaks", GetPotentialResourceLeaks)
            .WithName("GetPotentialResourceLeaks")
            .WithSummary("Get potential resource leaks")
            .WithDescription("Returns details of resources that may have leaked")
            .Produces<ResourceLeakResponse>(200)
            .Produces<ProblemDetails>(500);

#pragma warning disable ASP0016 // Async endpoints with IResult return type are valid in minimal APIs
        group.MapPost("/resources/scan-leaks", ScanForResourceLeaks)
            .WithName("ScanForResourceLeaks")
            .WithSummary("Perform resource leak scan")
            .WithDescription("Triggers a manual scan for resource leaks");
#pragma warning restore ASP0016

        // Exception telemetry endpoints
        group.MapGet("/exceptions/statistics", GetExceptionStatistics)
            .WithName("GetExceptionStatistics")
            .WithSummary("Get exception statistics")
            .WithDescription("Returns comprehensive statistics about application exceptions")
            .Produces<ExceptionStatistics>(200)
            .Produces<ProblemDetails>(500);

        group.MapGet("/exceptions/recent", GetRecentExceptions)
            .WithName("GetRecentExceptions")
            .WithSummary("Get recent exceptions")
            .WithDescription("Returns details of recently recorded exceptions with filtering options")
            .Produces<ExceptionHistoryResponse>(200)
            .Produces<ProblemDetails>(500);

        // Cache performance endpoints
        group.MapGet("/cache/statistics", GetCacheStatistics)
            .WithName("GetCacheStatistics")
            .WithSummary("Get cache performance statistics")
            .WithDescription("Returns comprehensive cache performance metrics")
            .Produces<QueryCacheStatistics>(200)
            .Produces<ProblemDetails>(500);

        group.MapGet("/cache/effectiveness", GetCacheEffectiveness)
            .WithName("GetCacheEffectiveness")
            .WithSummary("Get cache effectiveness metrics")
            .WithDescription("Returns cache effectiveness analysis for optimization")
            .Produces<CacheEffectivenessMetrics>(200)
            .Produces<ProblemDetails>(500);

        group.MapDelete("/cache/invalidate", InvalidateCache)
            .WithName("InvalidateCache")
            .WithSummary("Invalidate cache entries")
            .WithDescription("Invalidates cache entries matching the specified pattern")
            .Produces<CacheInvalidationResponse>(200)
            .Produces<ProblemDetails>(400)
            .Produces<ProblemDetails>(500);

        // Overall performance summary endpoint
        group.MapGet("/summary", GetPerformanceSummary)
            .WithName("GetPerformanceSummary")
            .WithSummary("Get overall performance summary")
            .WithDescription("Returns a comprehensive summary of all performance metrics")
            .Produces<PerformanceSummaryResponse>(200)
            .Produces<ProblemDetails>(500);
    }

    private static IResult GetQueryPerformanceStatistics(HttpContext httpContext)
    {
        try
        {
            var monitor = httpContext.RequestServices.GetService<IDatabaseQueryPerformanceMonitor>();
            if (monitor == null)
            {
                return Results.Ok(new QueryPerformanceStatistics());
            }

            var statistics = monitor.GetStatistics();
            return Results.Ok(statistics);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.QueryPerformanceStatisticsFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Query performance statistics error. See server logs for details.");
        }
    }

    private static IResult GetSlowQueries(HttpContext httpContext, [FromQuery] int maxCount = 50)
    {
        try
        {
            var monitor = httpContext.RequestServices.GetService<IDatabaseQueryPerformanceMonitor>();
            if (monitor == null)
            {
                return Results.Ok(new SlowQueryResponse { SlowQueries = Array.Empty<SlowQueryRecord>() });
            }

            var slowQueries = monitor.GetRecentSlowQueries(Math.Min(maxCount, 200));
            var response = new SlowQueryResponse
            {
                SlowQueries = slowQueries,
                TotalCount = slowQueries.Count,
                CollectedAt = DateTime.UtcNow
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.SlowQueriesFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Slow queries retrieval error. See server logs for details.");
        }
    }

    private static IResult GetResourceTrackingStatistics(HttpContext httpContext)
    {
        try
        {
            var detector = httpContext.RequestServices.GetService<IResourceLeakDetector>();
            if (detector == null)
            {
                return Results.Ok(new ResourceTrackingStatistics());
            }

            var statistics = detector.GetStatistics();
            return Results.Ok(statistics);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.ResourceTrackingStatisticsFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Resource tracking statistics error. See server logs for details.");
        }
    }

    private static IResult GetPotentialResourceLeaks(HttpContext httpContext, [FromQuery] int maxResults = 100)
    {
        try
        {
            var detector = httpContext.RequestServices.GetService<IResourceLeakDetector>();
            if (detector == null)
            {
                return Results.Ok(new ResourceLeakResponse { PotentialLeaks = Array.Empty<ResourceLeakInfo>() });
            }

            var leaks = detector.GetPotentialLeaks();
            var response = new ResourceLeakResponse
            {
                PotentialLeaks = leaks.Take(maxResults).ToList(),
                TotalCount = leaks.Count,
                CollectedAt = DateTime.UtcNow
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.ResourceLeaksFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Resource leaks retrieval error. See server logs for details.");
        }
    }

    private static async Task<IResult> ScanForResourceLeaks(HttpContext httpContext)
    {
        try
        {
            var detector = httpContext.RequestServices.GetService<IResourceLeakDetector>();
            if (detector == null)
            {
                return Results.Ok(new ResourceLeakScanResult());
            }

            var result = await detector.ScanForLeaksAsync();
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.ResourceLeakScanFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Resource leak scan error. See server logs for details.");
        }
    }

    private static IResult GetExceptionStatistics(HttpContext httpContext)
    {
        try
        {
            var telemetry = httpContext.RequestServices.GetService<IEnhancedExceptionTelemetry>();
            if (telemetry == null)
            {
                return Results.Ok(new ExceptionStatistics());
            }

            var statistics = telemetry.GetStatistics();
            return Results.Ok(statistics);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.ExceptionStatisticsFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Exception statistics error. See server logs for details.");
        }
    }

    private static IResult GetRecentExceptions(HttpContext httpContext,
        [FromQuery] int maxCount = 100,
        [FromQuery] string? severity = null)
    {
        try
        {
            var telemetry = httpContext.RequestServices.GetService<IEnhancedExceptionTelemetry>();
            if (telemetry == null)
            {
                return Results.Ok(new ExceptionHistoryResponse { Exceptions = Array.Empty<ExceptionRecord>() });
            }

            ExceptionSeverity? minSeverity = null;
            if (!string.IsNullOrEmpty(severity) && Enum.TryParse<ExceptionSeverity>(severity, true, out var parsedSeverity))
            {
                minSeverity = parsedSeverity;
            }

            var exceptions = telemetry.GetRecentExceptions(Math.Min(maxCount, 500), minSeverity);
            var response = new ExceptionHistoryResponse
            {
                Exceptions = exceptions,
                TotalCount = exceptions.Count,
                CollectedAt = DateTime.UtcNow
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.RecentExceptionsFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Recent exceptions retrieval error. See server logs for details.");
        }
    }

    private static IResult GetCacheStatistics(HttpContext httpContext)
    {
        try
        {
            var cacheManager = httpContext.RequestServices.GetService<IQueryResultCacheManager>();
            if (cacheManager == null)
            {
                return Results.Ok(new QueryCacheStatistics());
            }

            var statistics = cacheManager.GetStatistics();
            return Results.Ok(statistics);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.CacheStatisticsFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Cache statistics error. See server logs for details.");
        }
    }

    private static IResult GetCacheEffectiveness(HttpContext httpContext)
    {
        try
        {
            var cacheManager = httpContext.RequestServices.GetService<IQueryResultCacheManager>();
            if (cacheManager == null)
            {
                return Results.Ok(new CacheEffectivenessMetrics());
            }

            var metrics = cacheManager.GetEffectivenessMetrics();
            return Results.Ok(metrics);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.CacheEffectivenessFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Cache effectiveness error. See server logs for details.");
        }
    }

    private static async Task<IResult> InvalidateCache(HttpContext httpContext, [FromQuery] string? pattern = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return Results.BadRequest("Pattern parameter is required for cache invalidation");
            }

            var cacheManager = httpContext.RequestServices.GetService<IQueryResultCacheManager>();
            if (cacheManager == null)
            {
                return Results.Ok(new CacheInvalidationResponse { EntriesInvalidated = 0 });
            }

            var invalidatedCount = await cacheManager.InvalidateAsync(pattern);
            var response = new CacheInvalidationResponse
            {
                Pattern = pattern,
                EntriesInvalidated = invalidatedCount,
                InvalidatedAt = DateTime.UtcNow
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.CacheInvalidationFailed(logger, pattern ?? "Unknown", ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Cache invalidation error. See server logs for details.");
        }
    }

    private static IResult GetPerformanceSummary(HttpContext httpContext)
    {
        try
        {
            var summary = new PerformanceSummaryResponse
            {
                CollectedAt = DateTime.UtcNow
            };

            // Query performance
            var queryMonitor = httpContext.RequestServices.GetService<IDatabaseQueryPerformanceMonitor>();
            if (queryMonitor != null)
            {
                summary.QueryPerformance = queryMonitor.GetStatistics();
            }

            // Resource tracking
            var resourceDetector = httpContext.RequestServices.GetService<IResourceLeakDetector>();
            if (resourceDetector != null)
            {
                summary.ResourceTracking = resourceDetector.GetStatistics();
            }

            // Exception telemetry
            var exceptionTelemetry = httpContext.RequestServices.GetService<IEnhancedExceptionTelemetry>();
            if (exceptionTelemetry != null)
            {
                summary.ExceptionStatistics = exceptionTelemetry.GetStatistics();
            }

            // Cache performance
            var cacheManager = httpContext.RequestServices.GetService<IQueryResultCacheManager>();
            if (cacheManager != null)
            {
                summary.CacheStatistics = cacheManager.GetStatistics();
            }

            // Calculate overall health score
            summary.OverallHealthScore = CalculateHealthScore(summary);

            return Results.Ok(summary);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.PerformanceSummaryFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Performance summary error. See server logs for details.");
        }
    }

    private static double CalculateHealthScore(PerformanceSummaryResponse summary)
    {
        var score = 100.0;

        // Deduct for slow queries
        if (summary.QueryPerformance != null)
        {
            var slowQueryRatio = summary.QueryPerformance.TotalQueries > 0
                ? (double)summary.QueryPerformance.SlowQueries / summary.QueryPerformance.TotalQueries
                : 0;
            score -= Math.Min(slowQueryRatio * 30, 20); // Max 20 point deduction
        }

        // Deduct for resource leaks
        if (summary.ResourceTracking != null && summary.ResourceTracking.PotentialLeaks > 0)
        {
            score -= Math.Min(summary.ResourceTracking.PotentialLeaks * 2, 15); // Max 15 point deduction
        }

        // Deduct for high exception rate
        if (summary.ExceptionStatistics != null && summary.ExceptionStatistics.ExceptionsPerMinute > 1)
        {
            score -= Math.Min(summary.ExceptionStatistics.ExceptionsPerMinute * 5, 20); // Max 20 point deduction
        }

        // Bonus for good cache performance
        if (summary.CacheStatistics != null && summary.CacheStatistics.HitRatio > 0.8)
        {
            score += (summary.CacheStatistics.HitRatio - 0.8) * 25; // Up to 5 point bonus
        }

        return Math.Max(0, Math.Min(100, score));
    }

    private static partial class MonitoringLog
    {
        [LoggerMessage(
            EventId = 9401,
            Level = LogLevel.Error,
            Message = "Query performance statistics retrieval failed")]
        public static partial void QueryPerformanceStatisticsFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9402,
            Level = LogLevel.Error,
            Message = "Slow queries retrieval failed")]
        public static partial void SlowQueriesFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9403,
            Level = LogLevel.Error,
            Message = "Resource tracking statistics retrieval failed")]
        public static partial void ResourceTrackingStatisticsFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9404,
            Level = LogLevel.Error,
            Message = "Resource leaks retrieval failed")]
        public static partial void ResourceLeaksFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9405,
            Level = LogLevel.Error,
            Message = "Resource leak scan failed")]
        public static partial void ResourceLeakScanFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9406,
            Level = LogLevel.Error,
            Message = "Exception statistics retrieval failed")]
        public static partial void ExceptionStatisticsFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9407,
            Level = LogLevel.Error,
            Message = "Recent exceptions retrieval failed")]
        public static partial void RecentExceptionsFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9408,
            Level = LogLevel.Error,
            Message = "Cache statistics retrieval failed")]
        public static partial void CacheStatisticsFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9409,
            Level = LogLevel.Error,
            Message = "Cache effectiveness retrieval failed")]
        public static partial void CacheEffectivenessFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 9410,
            Level = LogLevel.Error,
            Message = "Cache invalidation failed for pattern: {Pattern}")]
        public static partial void CacheInvalidationFailed(ILogger logger, string pattern, Exception exception);

        [LoggerMessage(
            EventId = 9411,
            Level = LogLevel.Error,
            Message = "Performance summary retrieval failed")]
        public static partial void PerformanceSummaryFailed(ILogger logger, Exception exception);
    }
}

// Response models for the monitoring endpoints

/// <summary>
/// Response model for slow query requests.
/// </summary>
internal sealed class SlowQueryResponse
{
    public IReadOnlyList<SlowQueryRecord> SlowQueries { get; set; } = Array.Empty<SlowQueryRecord>();
    public int TotalCount { get; set; }
    public DateTime CollectedAt { get; set; }
}

/// <summary>
/// Response model for resource leak requests.
/// </summary>
internal sealed class ResourceLeakResponse
{
    public IReadOnlyList<ResourceLeakInfo> PotentialLeaks { get; set; } = Array.Empty<ResourceLeakInfo>();
    public int TotalCount { get; set; }
    public DateTime CollectedAt { get; set; }
}

/// <summary>
/// Response model for exception history requests.
/// </summary>
internal sealed class ExceptionHistoryResponse
{
    public IReadOnlyList<ExceptionRecord> Exceptions { get; set; } = Array.Empty<ExceptionRecord>();
    public int TotalCount { get; set; }
    public DateTime CollectedAt { get; set; }
}

/// <summary>
/// Response model for cache invalidation requests.
/// </summary>
internal sealed class CacheInvalidationResponse
{
    public string Pattern { get; set; } = string.Empty;
    public int EntriesInvalidated { get; set; }
    public DateTime InvalidatedAt { get; set; }
}

/// <summary>
/// Comprehensive performance summary response.
/// </summary>
internal sealed class PerformanceSummaryResponse
{
    public QueryPerformanceStatistics? QueryPerformance { get; set; }
    public ResourceTrackingStatistics? ResourceTracking { get; set; }
    public ExceptionStatistics? ExceptionStatistics { get; set; }
    public QueryCacheStatistics? CacheStatistics { get; set; }
    public double OverallHealthScore { get; set; }
    public DateTime CollectedAt { get; set; }
}