// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Redis;
using Polly.CircuitBreaker;

namespace Honua.Infrastructure.Models;

internal static class ExceptionMapper
{
    public static ExceptionMappingResult Map(Exception exception, bool includeDebugDetails = false)
    {
        var (statusCode, title, safeDetail, details) = MapExceptionCore(exception);

        // Only include exception message in debug details when explicitly enabled.
        // Oversized-body exceptions are also suppressed because their framework
        // messages leak internal limit values (e.g. "Form value length limit
        // 4194304 exceeded.") that must not reach clients in any environment.
        string? debugInfo = null;
        if (includeDebugDetails && !IsSafeException(exception) && !IsOversizedRequestException(exception))
        {
            debugInfo = exception.Message;
        }

        return new ExceptionMappingResult(statusCode, title, safeDetail, details, debugInfo);
    }

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
        // Oversized request bodies / form values surface as framework BadHttpRequestException
        // (request body too large) or InvalidDataException (form key/value/count limits).
        // Map them to 413 with a clean, fixed message so the internal limit values are
        // never leaked to clients.
        if (IsOversizedRequestException(exception))
        {
            return (
                StatusCodes.Status413PayloadTooLarge,
                "Payload Too Large",
                "The request body exceeds the maximum allowed size.",
                null
            );
        }

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

            BrokenCircuitException => (
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable",
                "The service is temporarily unavailable. Please try again later.",
                null
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

            InvalidOperationException invalidOperationEx when IsAuthenticationInvalidOperation(invalidOperationEx) => (
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "Authentication is required to access this resource.",
                null
            ),

            // Shared progress state is transiently unreachable (the type's contract): the
            // caller must retry, so answer 503 rather than a server-bug 500 (honua-server#6753).
            Honua.Infrastructure.Progress.DistributedProgressStateUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable",
                "The service is temporarily unavailable. Please try again later.",
                null
            ),

            // A Redis pipe completed under an in-flight command, bare or wrapped by a store's
            // own exception. Map that one fault to 503; every other InvalidOperationException
            // stays 500 (honua-server#6753).
            InvalidOperationException invalidOperationEx when RedisClientFault.ContainsCompletedReader(invalidOperationEx) => (
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable",
                "The service is temporarily unavailable. Please try again later.",
                null
            ),

            InvalidOperationException => (
                StatusCodes.Status500InternalServerError,
                "Internal Server Error",
                "An unexpected error occurred while processing the request.",
                null
            ),

            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "Bad Request",
                "Invalid request body.",
                null
            ),

            JsonException => (
                StatusCodes.Status400BadRequest,
                "Bad Request",
                "Invalid JSON payload.",
                null
            ),

            ResourceAccessDeniedException => (
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "The operation is not permitted for this resource.",
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

    // A framework body-limit exception is mapped to 413 without echoing the internal limit values in the framework message.
    private static bool IsOversizedRequestException(Exception exception)
    {
        // Request body limit (MaxRequestBodySize) surfaces as BadHttpRequestException with
        // StatusCode 413.
        if (exception is BadHttpRequestException badRequest &&
            badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return true;
        }

        // Form key/value length, value count, and multipart body length limits surface as
        // InvalidDataException. Match on the framework's "limit ... exceeded" message shape
        // rather than the exact wording so future framework revisions stay covered.
        if (exception is InvalidDataException invalidData &&
            !string.IsNullOrEmpty(invalidData.Message) &&
            invalidData.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) &&
            invalidData.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsSafeException(Exception exception)
    {
        return exception is ValidationException
            || exception is ResourceNotFoundException
            || exception is ResourceConflictException
            || exception is ServiceUnavailableException;
    }

    private static bool IsAuthenticationInvalidOperation(InvalidOperationException ex)
    {
        if (string.IsNullOrWhiteSpace(ex.Message))
        {
            return false;
        }

        return ex.Message.StartsWith("IDX", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQueryParameterException(ArgumentException ex)
    {
        // Check for common query parameter validation patterns
        var message = ex.Message;
        return message.Contains("filter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("query", StringComparison.OrdinalIgnoreCase)
            || message.Contains("parameter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("orderby", StringComparison.OrdinalIgnoreCase)
            || message.Contains("CQL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bbox", StringComparison.OrdinalIgnoreCase)
            || message.Contains("datetime", StringComparison.OrdinalIgnoreCase);
    }

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
                errorPart = SanitizeCqlErrorDetail(errorPart);
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

        if (message.Contains("$orderby", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = message.Trim();
            if (trimmed.Length > 200)
            {
                trimmed = trimmed[..200] + "...";
            }
            return trimmed;
        }

        if (message.Contains("orderby", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid orderBy parameter.";
        }

        if (message.Contains("datetime", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid datetime parameter format.";
        }

        // Generic safe message for other parameter errors
        return "Invalid query parameter value.";
    }

    private static string SanitizeCqlErrorDetail(string errorPart)
    {
        if (string.IsNullOrWhiteSpace(errorPart))
        {
            return "Syntax error in filter expression.";
        }

        var sanitized = Regex.Replace(errorPart, @"\bSystem\.[\w\.]+", string.Empty, RegexOptions.IgnoreCase);
        sanitized = sanitized.Replace("Exception", string.Empty, StringComparison.OrdinalIgnoreCase);
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Syntax error in filter expression." : sanitized;
    }
}

internal readonly record struct ExceptionMappingResult(
    int StatusCode,
    string Title,
    string Detail,
    IReadOnlyList<string>? Details,
    string? DebugInfo);
