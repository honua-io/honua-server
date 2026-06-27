// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints that expose the usage-ranked geoprocessing (GP) tool tiering
/// view (#2144). Reads the real <see cref="IProcessUsageTelemetry"/> store the
/// dispatcher records into and returns GP tools ranked by invocation count.
/// </summary>
internal static class GeoprocessingUsageEndpoints
{
    internal sealed class GeoprocessingUsageEndpointsLog;

    public static void MapGeoprocessingUsageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/geoprocessing")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Geoprocessing")
            .RequireAdminAuthorization();

        group.MapGet("/tools/usage-ranking", HandleGetUsageRanking)
            .WithDisplayName("Get Geoprocessing Tool Usage Ranking")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<GeoprocessingUsageRankingResponse>>();
    }

    private static IResult HandleGetUsageRanking(
        [FromServices] IProcessUsageTelemetry usageTelemetry,
        [FromServices] ILogger<GeoprocessingUsageEndpointsLog> logger,
        [FromQuery] int? limit)
    {
        if (limit is <= 0)
        {
            return Results.Json(
                ApiResponse<GeoprocessingUsageRankingResponse>.Failure("The 'limit' parameter must be a positive integer."),
                GeoprocessingUsageJsonContext.Default.ApiResponseGeoprocessingUsageRankingResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var ranking = usageTelemetry.GetRanking(limit);

        var tools = new GeoprocessingToolUsageRankEntry[ranking.Count];
        for (var i = 0; i < ranking.Count; i++)
        {
            var entry = ranking[i];
            tools[i] = new GeoprocessingToolUsageRankEntry
            {
                Rank = i + 1,
                ProcessId = entry.ProcessId,
                InvocationCount = entry.InvocationCount,
                SuccessCount = entry.SuccessCount,
                FailureCount = entry.FailureCount,
                LastInvokedAt = entry.LastInvokedAt
            };
        }

        AdminLog.GeoprocessingUsageRankingQueried(logger, tools.Length);

        var response = new GeoprocessingUsageRankingResponse
        {
            Count = tools.Length,
            Tools = tools
        };

        return Results.Json(
            ApiResponse<GeoprocessingUsageRankingResponse>.CreateSuccess(response),
            GeoprocessingUsageJsonContext.Default.ApiResponseGeoprocessingUsageRankingResponse);
    }
}
