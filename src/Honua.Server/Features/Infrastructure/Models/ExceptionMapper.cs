// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Centralized exception-to-error mapper that normalizes exceptions to ServiceError
/// with consistent status codes, safe messages, and optional debug details.
/// </summary>
/// <remarks>
/// This mapper ensures:
/// - Consistent HTTP status code mapping across all protocols
/// - Message sanitization to prevent information leakage
/// - Environment-aware debug detail exposure
/// - Support for domain-specific exceptions with safe messages
/// </remarks>
internal static class ExceptionMapper
{
    /// <summary>
    /// Maps an exception to a standardized error response.
    /// </summary>
    /// <param name="exception">The exception to map.</param>
    /// <param name="includeDebugDetails">Whether to include debug details (typically only in Development).</param>
    /// <returns>A tuple containing HTTP status code, title, safe detail message, and optional debug details.</returns>
    public static ExceptionMappingResult Map(Exception exception, bool includeDebugDetails = false)
    {
        var (statusCode, title, safeDetail, details) = MapExceptionCore(exception);

        // Only include exception message in debug details when explicitly enabled
        string? debugInfo = null;
        if (includeDebugDetails && !IsSafeException(exception))
        {
            debugInfo = exception.Message;
        }

        return new ExceptionMappingResult(statusCode, title, safeDetail, details, debugInfo);
    }

    /// <summary>
    /// Converts an exception to a ServiceError with appropriate sanitization.
    /// </summary>
    /// <param name="exception">The exception to convert.</param>
    /// <param name="includeDebugDetails">Whether to include debug details.</param>
    /// <returns>A ServiceError instance.</returns>
    public static ServiceError ToServiceError(Exception exception, bool includeDebugDetails = false)
    {
        var result = Map(exception, includeDebugDetails);
        var details = result.Details?.ToList() ?? [];

        if (!string.IsNullOrEmpty(result.DebugInfo))
        {
            details.Add($"Debug: {result.DebugInfo}");
        }

        return ServiceError.Create(
            result.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Title,
            null,
            details.Count > 0 ? details : null);
    }

    private static (int StatusCode, string Title, string Detail, IReadOnlyList<string>? Details) MapExceptionCore(Exception exception)
    {
        return exception switch
        {
            // Domain exceptions with safe messages
            ValidationException validationEx => (
                StatusCodes.Status400BadRequest,
                "Bad Request",
                validationEx.Message, // ValidationException messages are designed to be safe
                validationEx.Details
            ),

            ResourceNotFoundException => (
                StatusCodes.Status404NotFound,
                "Not Found",
                "The requested resource was not found.",
                null
            ),

            ResourceConflictException => (
                StatusCodes.Status409Conflict,
                "Conflict",
                "The request could not be completed due to a conflict with the current state.",
                null
            ),

            ServiceUnavailableException serviceEx => (
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable",
                "The service is temporarily unavailable. Please try again later.",
                serviceEx.RetryAfterSeconds.HasValue
                    ? [$"Retry-After: {serviceEx.RetryAfterSeconds}s"]
                    : null
            ),

            // Standard exceptions with generic safe messages
            ArgumentNullException => (
                StatusCodes.Status400BadRequest,
                "Bad Request",
                "A required parameter was not provided.",
                null
            ),

            ArgumentException argEx when IsQueryParameterException(argEx) => (
                StatusCodes.Status400BadRequest,
                "Bad Request",
                SanitizeQueryParameterMessage(argEx.Message),
                null
            ),

            ArgumentException => (
                StatusCodes.Status400BadRequest,
                "Bad Request",
                "Invalid request parameters.",
                null
            ),

            InvalidOperationException => (
                StatusCodes.Status400BadRequest,
                "Bad Request",
                "The requested operation is not valid in the current state.",
                null
            ),

            UnauthorizedAccessException => (
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "Authentication is required to access this resource.",
                null
            ),

            NotSupportedException => (
                StatusCodes.Status405MethodNotAllowed,
                "Method Not Allowed",
                "The requested operation is not supported.",
                null
            ),

            OperationCanceledException => (
                StatusCodes.Status408RequestTimeout,
                "Request Timeout",
                "The request was cancelled or timed out.",
                null
            ),

            TimeoutException => (
                StatusCodes.Status408RequestTimeout,
                "Request Timeout",
                "The request timed out.",
                null
            ),

            // Default - hide all internal details
            _ => (
                StatusCodes.Status500InternalServerError,
                "Internal Server Error",
                "An unexpected error occurred while processing the request.",
                null
            )
        };
    }

    /// <summary>
    /// Determines if an exception type has messages that are safe to expose.
    /// </summary>
    private static bool IsSafeException(Exception exception)
    {
        return exception is ValidationException
            || exception is ResourceNotFoundException
            || exception is ResourceConflictException
            || exception is ServiceUnavailableException;
    }

    /// <summary>
    /// Checks if an ArgumentException is related to query parameters
    /// and has a message that can be partially exposed.
    /// </summary>
    private static bool IsQueryParameterException(ArgumentException ex)
    {
        // Check for common query parameter validation patterns
        var message = ex.Message;
        return message.Contains("filter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("query", StringComparison.OrdinalIgnoreCase)
            || message.Contains("parameter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("CQL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bbox", StringComparison.OrdinalIgnoreCase)
            || message.Contains("datetime", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sanitizes query parameter error messages to expose only safe information.
    /// </summary>
    private static string SanitizeQueryParameterMessage(string message)
    {
        // Extract just the error type without exposing internal details
        // Look for common patterns and extract the relevant part
        if (message.Contains("CQL", StringComparison.OrdinalIgnoreCase))
        {
            // For CQL errors, extract the parsing error portion
            var colonIndex = message.IndexOf(':');
            if (colonIndex > 0 && colonIndex < message.Length - 1)
            {
                var errorPart = message[(colonIndex + 1)..].Trim();
                // Limit length to prevent overly detailed messages
                if (errorPart.Length > 200)
                {
                    errorPart = errorPart[..200] + "...";
                }
                return $"Invalid CQL filter syntax: {errorPart}";
            }
            return "Invalid CQL filter syntax.";
        }

        if (message.Contains("bbox", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid bbox parameter format.";
        }

        if (message.Contains("datetime", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid datetime parameter format.";
        }

        // Generic safe message for other parameter errors
        return "Invalid query parameter value.";
    }
}

/// <summary>
/// Result of mapping an exception to an error response.
/// </summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Title">Error title (e.g., "Bad Request", "Not Found").</param>
/// <param name="Detail">Safe detail message to expose to clients.</param>
/// <param name="Details">Additional details that are safe to expose.</param>
/// <param name="DebugInfo">Debug information only shown in development mode.</param>
internal readonly record struct ExceptionMappingResult(
    int StatusCode,
    string Title,
    string Detail,
    IReadOnlyList<string>? Details,
    string? DebugInfo);
