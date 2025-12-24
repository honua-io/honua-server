// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Middleware;

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Helper methods for creating GeoServices-compatible error responses
/// </summary>
internal static class GeoServicesErrorHelpers
{
    /// <summary>
    /// Creates a GeoServices-compatible 404 Not Found error response
    /// </summary>
    public static IResult CreateNotFoundError(string message)
    {
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = 404,
                Message = message,
                Details = null
            }
        };
        return Results.Json(errorResponse, LimitsEnforcementJsonContext.Default.ApiErrorResponse, statusCode: 404);
    }

    /// <summary>
    /// Creates a GeoServices-compatible 400 Bad Request error response
    /// </summary>
    public static IResult CreateBadRequestError(string message, string[]? details = null)
    {
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = 400,
                Message = message,
                Details = details
            }
        };
        return Results.Json(errorResponse, LimitsEnforcementJsonContext.Default.ApiErrorResponse, statusCode: 400);
    }

    /// <summary>
    /// Creates a GeoServices-compatible 500 Internal Server Error response
    /// </summary>
    public static IResult CreateInternalServerError(string message, string[]? details = null)
    {
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = 500,
                Message = message,
                Details = details
            }
        };
        return Results.Json(errorResponse, LimitsEnforcementJsonContext.Default.ApiErrorResponse, statusCode: 500);
    }
}
