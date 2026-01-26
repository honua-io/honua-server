// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;

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
        var validator = context.RequestServices.GetRequiredService<IRouteParameterValidator>();
        var allowedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { allowedMethod };
        return validator.ValidateHttpMethod(context, allowedMethods).IsValid;
    }

    /// <summary>
    /// Validates and extracts serviceId from route values
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="serviceId">Extracted service ID if valid</param>
    /// <returns>True if serviceId is valid, false otherwise</returns>
    public static bool TryValidateServiceId(HttpContext context, out string serviceId)
    {
        var validator = context.RequestServices.GetRequiredService<IRouteParameterValidator>();
        var result = validator.ValidateServiceId(context);
        serviceId = result.IsValid ? result.Value ?? string.Empty : string.Empty;
        return result.IsValid;
    }

    /// <summary>
    /// Validates serviceId and returns a standardized error result when missing or invalid.
    /// </summary>
    public static IResult? ValidateServiceId(HttpContext context, out string serviceId)
    {
        if (TryValidateServiceId(context, out serviceId))
        {
            return null;
        }

        return StandardErrorHelpers.CreateBadRequest(context, "Service ID is required");
    }

    /// <summary>
    /// Validates and extracts layerId from route values
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="layerId">Extracted layer ID if valid</param>
    /// <returns>True if layerId is valid, false otherwise</returns>
    public static bool TryValidateLayerId(HttpContext context, out int layerId)
    {
        var validator = context.RequestServices.GetRequiredService<IRouteParameterValidator>();
        var result = validator.ValidateLayerId(context);
        layerId = result.IsValid ? result.Value : default;
        return result.IsValid;
    }

    /// <summary>
    /// Validates layerId and returns a standardized error result when missing or invalid.
    /// </summary>
    public static IResult? ValidateLayerId(HttpContext context, out int layerId)
    {
        if (TryValidateLayerId(context, out layerId))
        {
            return null;
        }

        return StandardErrorHelpers.CreateBadRequest(context, "Layer ID is required");
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
        await ValidationErrorHelpers.WriteValidationErrorAsync(
            context,
            statusCode,
            message,
            details,
            context.RequestAborted);
    }
}
