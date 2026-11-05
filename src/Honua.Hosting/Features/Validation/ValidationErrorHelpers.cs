// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Infrastructure.Validation;

public static class ValidationErrorHelpers
{
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

        // #2243: this builder bypasses the central formatter, so emit the error
        // telemetry here too. No HttpContext is available, so the surface is
        // recorded as the generic GeoServices service type.
        HonuaTelemetry.RecordErrorEnvelope(
            "GeoServices",
            "validation",
            GeoServicesErrorCodes.BadRequest,
            isGeoServices: true);

        return Results.BadRequest(errorResponse);
    }

    public static IResult CreateOgcValidationError(string title, string detail, string? instance = null)
    {
        return ProblemDetailsHelpers.CreateOgcProblem(
            StatusCodes.Status400BadRequest,
            title,
            detail,
            instance);
    }

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

    public static IResult CreateMethodNotAllowed(ISet<string> allowedMethods)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);
        return new MethodNotAllowedWithAllowHeaderResult(allowedMethods);
    }

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

    public static IResult CreateUnsupportedMediaType(string receivedType, ISet<string> allowedTypes)
    {
        var message = $"Unsupported media type '{receivedType}'. Supported types: {string.Join(", ", allowedTypes)}";

        return Results.Problem(
            title: "Unsupported Media Type",
            detail: message,
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    public static IResult CreateUnsupportedMediaType(HttpContext context, string receivedType, ISet<string> allowedTypes)
    {
        var message = $"Unsupported media type '{receivedType}'. Supported types: {string.Join(", ", allowedTypes)}";
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status415UnsupportedMediaType,
            "Unsupported Media Type",
            message);

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    public static async Task WriteValidationErrorAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await WriteValidationErrorAsync(context, statusCode, title, new[] { detail }, cancellationToken);
    }

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

        // #2243: this writer bypasses the central formatter, so emit the error
        // telemetry through the same funnel using the request path for surface
        // classification.
        StandardErrorResponseFormatter.RecordErrorTelemetry(context, statusCode);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            errorResponse,
            LimitsEnforcementJsonContext.Default.ApiErrorResponse,
            cancellationToken);
    }

    public static IResult? CreateErrorIfInvalid(
        ValidationResult validationResult,
        Func<string, IResult> errorResponseFactory)
    {
        return validationResult.IsValid ? null : errorResponseFactory(validationResult.ErrorMessage!);
    }

    public static IResult? CreateErrorIfInvalid<T>(
        ValidationResult<T> validationResult,
        Func<string, IResult> errorResponseFactory)
    {
        return validationResult.IsValid ? null : errorResponseFactory(validationResult.ErrorMessage!);
    }

    public static ValidationResult CombineValidationResults(params ValidationResult[] validationResults)
    {
        return validationResults.FirstOrDefault(result => !result.IsValid) ?? ValidationResult.Success();
    }

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
