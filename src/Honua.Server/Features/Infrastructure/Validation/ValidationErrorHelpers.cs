// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Infrastructure.Validation;

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
        return Results.Problem(
            title: "Method Not Allowed",
            detail: $"Allowed methods: {string.Join(", ", allowedMethods)}",
            statusCode: StatusCodes.Status405MethodNotAllowed);
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
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = statusCode,
                Message = title,
                Details = [detail]
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
