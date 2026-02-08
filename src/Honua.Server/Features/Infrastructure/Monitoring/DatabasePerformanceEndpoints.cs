// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// API endpoints for database performance monitoring and diagnostics
/// </summary>
/// <remarks>
/// Provides real-time insights into database performance optimizations
/// including prepared statement cache efficiency and query execution patterns.
/// These endpoints are intended for development and monitoring purposes.
/// </remarks>
internal static class DatabasePerformanceEndpoints
{
    /// <summary>
    /// Registers database performance monitoring endpoints with formal API versioning
    /// </summary>
    /// <param name="app">Web application builder</param>
    public static void MapDatabasePerformanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/performance/database")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Database Performance")
            .RequireAdminAuthorization();

        group.MapGet("/query-cache/statistics", GetQueryCacheStatistics)
            .WithName("GetQueryCacheStatistics")
            .WithSummary("Get prepared statement cache performance statistics")
            .WithDescription("Returns detailed statistics about query plan caching performance including hit ratios and cache utilization")
            .Produces<QueryCacheStatisticsResponse>(200)
            .Produces<ProblemDetails>(500);
    }

    /// <summary>
    /// Gets current query cache performance statistics
    /// </summary>
    /// <param name="httpContext">HTTP context for resolving services</param>
    /// <returns>Cache performance statistics</returns>
    private static IResult GetQueryCacheStatistics(HttpContext httpContext)
    {
        try
        {
            var cache = httpContext.RequestServices.GetRequiredService<IPreparedStatementCacheStatisticsProvider>();
            var stats = cache.GetStatistics();
            var options = httpContext.RequestServices.GetRequiredService<IOptions<QueryCacheOptions>>().Value;

            var response = new QueryCacheStatisticsResponse
            {
                TotalStatements = stats.TotalStatements,
                PreparedStatements = stats.PreparedStatements,
                CacheHits = stats.CacheHits,
                CacheMisses = stats.CacheMisses,
                HitRatio = stats.HitRatio,
                CacheUtilization = options.MaxCachedStatements > 0
                    ? (double)stats.PreparedStatements / options.MaxCachedStatements
                    : 0,
                CollectedAt = DateTime.UtcNow
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Monitoring");
            MonitoringLog.QueryCacheStatisticsFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                httpContext,
                "Query cache statistics error. See server logs for details.");
        }
    }
}

/// <summary>
/// Response model for query cache statistics
/// </summary>
/// <remarks>
/// Provides comprehensive metrics about prepared statement cache performance
/// to help monitor and optimize database query execution.
/// </remarks>
internal sealed record QueryCacheStatisticsResponse
{
    /// <summary>
    /// Total number of unique statements tracked
    /// </summary>
    public int TotalStatements { get; init; }

    /// <summary>
    /// Number of statements currently prepared and cached
    /// </summary>
    public int PreparedStatements { get; init; }

    /// <summary>
    /// Total cache hit count
    /// </summary>
    public int CacheHits { get; init; }

    /// <summary>
    /// Total cache miss count
    /// </summary>
    public int CacheMisses { get; init; }

    /// <summary>
    /// Cache hit ratio (0.0 to 1.0)
    /// </summary>
    public double HitRatio { get; init; }

    /// <summary>
    /// Cache utilization percentage (0.0 to 1.0)
    /// </summary>
    public double CacheUtilization { get; init; }

    /// <summary>
    /// Timestamp when statistics were collected
    /// </summary>
    public DateTime CollectedAt { get; init; }
}
