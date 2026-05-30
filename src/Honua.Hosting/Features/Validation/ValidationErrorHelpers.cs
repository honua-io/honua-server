// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Validation;

/// <summary>
/// Standardized error response helpers for validation failures.
/// Provides consistent error formatting across all protocols and endpoints.
/// </summary>
public static class ValidationErrorHelpers
{
    /// <summary>
    /// Creates a standardized BadRequest response for validation failures.
    /// Used by GeoServices REST API endpoints.
    /// </summary>
    /// <param name="message">Primary error message</param>
    /// <param name="details">Optional detailed error information</param>
    /// <returns>BadRequest result with standardized error format</returns>
    public static IResult CreateGeoServicesValidationError(string message, string[]? details = null)
    {
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = GeoServicesErrorCodes.BadRequest,
                Message = message,
                Details = details
            }
        };

        return Results.BadRequest(errorResponse);
    }

    /// <summary>
    /// Creates a standardized BadRequest response for OGC API Features endpoints.
    /// </summary>
    /// <param name="title">Error title</param>
    /// <param name="detail">Detailed error description</param>
    /// <param name="instance">Optional instance identifier</param>
    /// <returns>BadRequest result with RFC 7807 Problem Details format</returns>
    public static IResult CreateOgcValidationError(string title, string detail, string? instance = null)
    {
        return ProblemDetailsHelpers.CreateOgcProblem(
            StatusCodes.Status400BadRequest,
            title,
            detail,
            instance);
    }

    /// <summary>
    /// Creates a standardized BadRequest response for OData endpoints.
    /// </summary>
    /// <param name="code">OData error code</param>
    /// <param name="message">Error message</param>
    /// <returns>BadRequest result with OData error format</returns>
    public static IResult CreateODataValidationError(string code, string message)
    {
        var errorResponse = new
        {
            error = new
            {
                code,
                message,
                target = (string?)null,
                details = Array.Empty<object>(),
                innererror = (object?)null
            }
        };

        return Results.BadRequest(errorResponse);
    }

    /// <summary>
    /// Creates a method not allowed response with proper Allow header.
    /// </summary>
    /// <param name="allowedMethods">Set of allowed HTTP methods</param>
    /// <returns>MethodNotAllowed result with Allow header</returns>
    public static IResult CreateMethodNotAllowed(ISet<string> allowedMethods)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);
        return new MethodNotAllowedWithAllowHeaderResult(allowedMethods);
    }

    /// <summary>
    /// Creates a method not allowed response with protocol-specific formatting.
    /// </summary>
    /// <param name="context">HTTP context used for protocol detection.</param>
    /// <param name="allowedMethods">Set of allowed HTTP methods.</param>
    /// <returns>Protocol-specific MethodNotAllowed result with Allow header.</returns>
    public static IResult CreateMethodNotAllowed(HttpContext context, ISet<string> allowedMethods)
    {
        var allowHeader = string.Join(", ", allowedMethods);
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status405MethodNotAllowed,
            "Method Not Allowed",
            $"Allowed methods: {allowHeader}");

        return StandardErrorResponseFormatter.FormatError(
            context,
            errorResponse,
            new ErrorResponseFormatterOptions
            {
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Allow"] = allowHeader
                }
            });
    }

    private sealed class MethodNotAllowedWithAllowHeaderResult(ISet<string> allowedMethods) : IResult
    {
        private readonly string _allowHeader = string.Join(", ", allowedMethods);

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.Headers.Allow = _allowHeader;
            return Results.Problem(
                title: "Method Not Allowed",
                detail: $"Allowed methods: {_allowHeader}",
                statusCode: StatusCodes.Status405MethodNotAllowed)
                .ExecuteAsync(httpContext);
        }
    }

    /// <summary>
    /// Writes an admin-formatted method not allowed response with an Allow header.
    /// </summary>
    /// <param name="context">HTTP context for the current request.</param>
    /// <param name="allowedMethods">Set of allowed HTTP methods.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteAdminMethodNotAllowedAsync(
        HttpContext context,
        ISet<string> allowedMethods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allowedMethods);

        var allowHeader = string.Join(", ", allowedMethods);
        var problemDetails = ProblemDetailsHelpers.CreateAdminProblemDetails(
            context,
            StatusCodes.Status405MethodNotAllowed,
            $"Allowed methods: {allowHeader}");

        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.ContentType = ProblemDetailsHelpers.ContentType;
        context.Response.Headers["Allow"] = allowHeader;

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problemDetails,
            ProblemJsonContext.Default.ProblemDetailsResponse,
            cancellationToken);
    }

    /// <summary>
    /// Creates a unsupported media type response for content type validation failures.
    /// </summary>
    /// <param name="receivedType">The content type that was received</param>
    /// <param name="allowedTypes">Set of allowed content types</param>
    /// <returns>UnsupportedMediaType result with detailed message</returns>
    public static IResult CreateUnsupportedMediaType(string receivedType, ISet<string> allowedTypes)
    {
        var message = $"Unsupported media type '{receivedType}'. Supported types: {string.Join(", ", allowedTypes)}";

        return Results.Problem(
            title: "Unsupported Media Type",
            detail: message,
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    /// <summary>
    /// Creates an unsupported media type response with protocol-specific formatting.
    /// </summary>
    /// <param name="context">HTTP context used for protocol detection.</param>
    /// <param name="receivedType">The content type that was received.</param>
    /// <param name="allowedTypes">Set of allowed content types.</param>
    /// <returns>Protocol-specific UnsupportedMediaType result.</returns>
    public static IResult CreateUnsupportedMediaType(HttpContext context, string receivedType, ISet<string> allowedTypes)
    {
        var message = $"Unsupported media type '{receivedType}'. Supported types: {string.Join(", ", allowedTypes)}";
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status415UnsupportedMediaType,
            "Unsupported Media Type",
            message);

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Writes a validation error response directly to the HTTP context.
    /// Used when we need to bypass the normal result pipeline.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="title">Error title</param>
    /// <param name="detail">Error detail message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task WriteValidationErrorAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await WriteValidationErrorAsync(context, statusCode, title, new[] { detail }, cancellationToken);
    }

    /// <summary>
    /// Writes a validation error response directly to the HTTP context with optional details.
    /// Used when we need to bypass the normal result pipeline.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="title">Error title</param>
    /// <param name="details">Optional error details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task WriteValidationErrorAsync(
        HttpContext context,
        int statusCode,
        string title,
        string[]? details,
        CancellationToken cancellationToken = default)
    {
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = statusCode,
                Message = title,
                Details = details
            }
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            errorResponse,
            LimitsEnforcementJsonContext.Default.ApiErrorResponse,
            cancellationToken);
    }

    /// <summary>
    /// Validates and creates appropriate error response based on validation result.
    /// </summary>
    /// <param name="validationResult">Validation result to check</param>
    /// <param name="errorResponseFactory">Factory function to create error response</param>
    /// <returns>Error result if validation failed, null if validation succeeded</returns>
    public static IResult? CreateErrorIfInvalid(
        ValidationResult validationResult,
        Func<string, IResult> errorResponseFactory)
    {
        return validationResult.IsValid ? null : errorResponseFactory(validationResult.ErrorMessage!);
    }

    /// <summary>
    /// Validates and creates appropriate error response for typed validation results.
    /// </summary>
    /// <typeparam name="T">Type of validation result value</typeparam>
    /// <param name="validationResult">Typed validation result to check</param>
    /// <param name="errorResponseFactory">Factory function to create error response</param>
    /// <returns>Error result if validation failed, null if validation succeeded</returns>
    public static IResult? CreateErrorIfInvalid<T>(
        ValidationResult<T> validationResult,
        Func<string, IResult> errorResponseFactory)
    {
        return validationResult.IsValid ? null : errorResponseFactory(validationResult.ErrorMessage!);
    }

    /// <summary>
    /// Combines multiple validation results into a single result.
    /// Returns first failure encountered or success if all validations pass.
    /// </summary>
    /// <param name="validationResults">Validation results to combine</param>
    /// <returns>Combined validation result</returns>
    public static ValidationResult CombineValidationResults(params ValidationResult[] validationResults)
    {
        foreach (var result in validationResults)
        {
            if (!result.IsValid)
            {
                return result;
            }
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Creates a validation result from multiple error messages.
    /// </summary>
    /// <param name="errors">Collection of error messages</param>
    /// <returns>Success if no errors, failure with combined message if errors exist</returns>
    public static ValidationResult FromErrors(IEnumerable<string> errors)
    {
        var errorList = errors.ToList();
        if (errorList.Count == 0)
        {
            return ValidationResult.Success();
        }

        var combinedMessage = errorList.Count == 1
            ? errorList[0]
            : $"Multiple validation errors: {string.Join("; ", errorList)}";

        return ValidationResult.Failure(combinedMessage);
    }
}
