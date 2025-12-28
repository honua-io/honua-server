// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Helper methods for common route parameter validation patterns
/// Reduces DRY violations across endpoint handlers
/// </summary>
internal static class RouteValidationHelpers
{
    /// <summary>
    /// Validates HTTP method and writes 405 Method Not Allowed response if invalid
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="allowedMethod">The allowed HTTP method</param>
    /// <returns>True if method is valid, false if 405 response was written</returns>
    public static bool ValidateHttpMethod(HttpContext context, string allowedMethod)
    {
        if (!string.Equals(context.Request.Method, allowedMethod, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validates and extracts serviceId from route values
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="serviceId">Extracted service ID if valid</param>
    /// <returns>True if serviceId is valid, false otherwise</returns>
    public static bool TryValidateServiceId(HttpContext context, out string serviceId)
    {
        string? rawServiceId = context.GetRouteValue("serviceId")?.ToString();
        serviceId = rawServiceId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(rawServiceId);
    }

    /// <summary>
    /// Validates and extracts layerId from route values
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="layerId">Extracted layer ID if valid</param>
    /// <returns>True if layerId is valid, false otherwise</returns>
    public static bool TryValidateLayerId(HttpContext context, out int layerId)
    {
        layerId = default;

        if (!context.Request.RouteValues.TryGetValue("layerId", out object? raw) || raw is null)
        {
            return false;
        }

        if (raw is int intValue)
        {
            layerId = intValue;
            return true;
        }

        return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId);
    }

    /// <summary>
    /// Writes standardized error response for validation failures
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="message">Error message</param>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="details">Optional error details</param>
    public static async Task WriteValidationErrorAsync(
        HttpContext context,
        string message,
        int statusCode = StatusCodes.Status400BadRequest,
        string[]? details = null)
    {
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = statusCode,
                Message = message,
                Details = details
            }
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            errorResponse,
            LimitsEnforcementJsonContext.Default.ApiErrorResponse,
            context.RequestAborted);
    }
}
