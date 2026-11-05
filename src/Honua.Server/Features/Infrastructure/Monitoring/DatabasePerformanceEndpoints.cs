// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

internal static class DatabasePerformanceEndpoints
{
    public static void MapDatabasePerformanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/performance/database")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Database Performance")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .RequireAdminAuthorization();

        group.MapGet("/query-cache/statistics", GetQueryCacheStatistics)
            .WithName("GetQueryCacheStatistics")
            .WithSummary("Get prepared statement cache performance statistics")
            .WithDescription("Returns detailed statistics about query plan caching performance including hit ratios and cache utilization")
            .Produces<QueryCacheStatisticsResponse>(200)
            .Produces<ProblemDetails>(500);
    }

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
        // Intentional catch-all request-handling boundary: this is the query
        // cache statistics endpoint; the failure is logged and mapped to a
        // generic error response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

internal sealed record QueryCacheStatisticsResponse
{
    public int TotalStatements { get; init; }

    public int PreparedStatements { get; init; }

    public int CacheHits { get; init; }

    public int CacheMisses { get; init; }

    public double HitRatio { get; init; }

    public double CacheUtilization { get; init; }

    public DateTime CollectedAt { get; init; }
}
