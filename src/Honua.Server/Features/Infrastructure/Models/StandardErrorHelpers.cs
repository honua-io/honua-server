// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Helper methods for creating standardized error responses across all protocols.
/// These methods provide a consistent API for error handling while maintaining
/// protocol-specific formatting through the StandardErrorResponseFormatter.
/// </summary>
internal static class StandardErrorHelpers
{
    /// <summary>
    /// Creates a Bad Request error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Bad Request response.</returns>
    internal static IResult CreateBadRequest(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.BadRequest(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates an Unauthorized error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Unauthorized response.</returns>
    internal static IResult CreateUnauthorized(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.Unauthorized(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a Forbidden error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Forbidden response.</returns>
    internal static IResult CreateForbidden(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.Forbidden(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a Not Found error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Not Found response.</returns>
    internal static IResult CreateNotFound(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.NotFound(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a Conflict error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Conflict response.</returns>
    internal static IResult CreateConflict(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.Conflict(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a Precondition Failed error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Precondition Failed response.</returns>
    internal static IResult CreatePreconditionFailed(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.PreconditionFailed(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates an Internal Server Error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Internal Server Error response.</returns>
    internal static IResult CreateInternalServerError(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.InternalServerError(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a Not Implemented error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Not Implemented response.</returns>
    internal static IResult CreateNotImplemented(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status501NotImplemented,
            "Not Implemented",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a Not Acceptable error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Not Acceptable response.</returns>
    internal static IResult CreateNotAcceptable(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status406NotAcceptable,
            "Not Acceptable",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a Service Unavailable error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="retryAfterSeconds">Optional retry-after seconds.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Service Unavailable response.</returns>
    internal static IResult CreateServiceUnavailable(HttpContext context, string detail, int? retryAfterSeconds = null, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.ServiceUnavailable(detail, retryAfterSeconds, additionalDetails);

        // Add Retry-After header if specified
        var options = new ErrorResponseFormatterOptions();
        if (retryAfterSeconds.HasValue)
        {
            options = new ErrorResponseFormatterOptions
            {
                IncludeAdditionalDetails = options.IncludeAdditionalDetails,
                IncludeDebugInfo = options.IncludeDebugInfo,
                ContentType = options.ContentType,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Retry-After"] = retryAfterSeconds.Value.ToString()
                }
            };
        }

        return StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
    }

    /// <summary>
    /// Creates a Request Timeout error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A protocol-specific Request Timeout response.</returns>
    internal static IResult CreateRequestTimeout(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.RequestTimeout(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates an error response from an exception using the centralized ExceptionMapper.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="exception">The exception to convert to an error response.</param>
    /// <param name="includeDebugDetails">Whether to include debug details (typically only in Development).</param>
    /// <returns>A protocol-specific error response.</returns>
    internal static IResult CreateFromException(HttpContext context, Exception exception, bool includeDebugDetails = false)
    {
        var errorResponse = StandardErrorResponse.FromException(exception, includeDebugDetails);

        var options = new ErrorResponseFormatterOptions
        {
            IncludeDebugInfo = includeDebugDetails
        };

        // Add special handling for ServiceUnavailableException
        if (exception is ServiceUnavailableException serviceEx && serviceEx.RetryAfterSeconds.HasValue)
        {
            options = new ErrorResponseFormatterOptions
            {
                IncludeAdditionalDetails = options.IncludeAdditionalDetails,
                IncludeDebugInfo = options.IncludeDebugInfo,
                ContentType = options.ContentType,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Retry-After"] = serviceEx.RetryAfterSeconds.Value.ToString()
                }
            };
        }

        return StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
    }

    /// <summary>
    /// Creates a validation error response from a ValidationException.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="validationException">The validation exception containing error details.</param>
    /// <returns>A protocol-specific validation error response.</returns>
    internal static IResult CreateValidationError(HttpContext context, ValidationException validationException)
    {
        var errorResponse = StandardErrorResponse.BadRequest(
            validationException.Message,
            validationException.Details);

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates a query parameter error response with appropriate sanitization.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="parameterName">The name of the invalid parameter.</param>
    /// <param name="errorMessage">The error message (will be sanitized).</param>
    /// <returns>A protocol-specific query parameter error response.</returns>
    internal static IResult CreateQueryParameterError(HttpContext context, string parameterName, string errorMessage)
    {
        // Use ExceptionMapper's sanitization logic for query parameter errors
        var argumentException = new ArgumentException(errorMessage, parameterName);
        return CreateFromException(context, argumentException, false);
    }

    /// <summary>
    /// Creates a CQL filter error response with sanitized error message.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="cqlError">The CQL parsing error message.</param>
    /// <returns>A protocol-specific CQL filter error response.</returns>
    public static IResult CreateCqlFilterError(HttpContext context, string cqlError)
    {
        var detail = $"Invalid CQL filter syntax: {SanitizeCqlErrorMessage(cqlError)}";
        return CreateBadRequest(context, detail);
    }

    /// <summary>
    /// Creates a geometry validation error response.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="geometryError">The geometry validation error message.</param>
    /// <returns>A protocol-specific geometry validation error response.</returns>
    public static IResult CreateGeometryError(HttpContext context, string geometryError)
    {
        var detail = $"Invalid geometry: {geometryError}";
        return CreateBadRequest(context, detail);
    }

    /// <summary>
    /// Sanitizes CQL error messages to prevent information leakage while maintaining useful error information.
    /// </summary>
    /// <param name="errorMessage">The original error message.</param>
    /// <returns>A sanitized error message.</returns>
    private static string SanitizeCqlErrorMessage(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return "Syntax error in filter expression.";
        }

        // Limit length to prevent overly detailed messages
        if (errorMessage.Length > 200)
        {
            errorMessage = errorMessage[..200] + "...";
        }

        // Remove any potential sensitive information patterns
        // This is a basic sanitization - more sophisticated logic could be added as needed
        var sanitized = errorMessage
            .Replace("System.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Exception", "", StringComparison.OrdinalIgnoreCase);

        return sanitized.Trim();
    }
}
