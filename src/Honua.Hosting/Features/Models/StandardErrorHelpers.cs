// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Models;

internal static class StandardErrorHelpers
{
    internal static IResult CreateBadRequest(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.BadRequest(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateUnauthorized(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.Unauthorized(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    // A GeoServices invalid token is HTTP 200 with body code 498, and an OGC request keeps HTTP 401.
    internal static IResult CreateInvalidToken(HttpContext context, string detail = "Invalid token.")
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status401Unauthorized,
            "Invalid Token",
            detail);
        return StandardErrorResponseFormatter.FormatError(
            context,
            errorResponse,
            new ErrorResponseFormatterOptions { GeoServicesBodyCode = GeoServicesErrorCodes.InvalidToken });
    }

    internal static IResult CreateForbidden(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.Forbidden(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreatePaymentRequired(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.PaymentRequired(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateNotFound(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.NotFound(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateConflict(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.Conflict(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreatePreconditionFailed(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.PreconditionFailed(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateInternalServerError(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.InternalServerError(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateMethodNotAllowed(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status405MethodNotAllowed,
            "Method Not Allowed",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateNotImplemented(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status501NotImplemented,
            "Not Implemented",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateNotAcceptable(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status406NotAcceptable,
            "Not Acceptable",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateUnsupportedMediaType(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status415UnsupportedMediaType,
            "Unsupported Media Type",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreatePayloadTooLarge(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status413PayloadTooLarge,
            "Payload Too Large",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateUnprocessableEntity(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status422UnprocessableEntity,
            "Unprocessable Entity",
            detail,
            additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateServiceUnavailable(
        HttpContext context,
        string detail,
        int? retryAfterSeconds = null,
        IReadOnlyList<string>? additionalDetails = null,
        bool retryable = false)
    {
        retryable |= retryAfterSeconds.HasValue;
        var errorResponse = StandardErrorResponse.ServiceUnavailable(detail, retryAfterSeconds, additionalDetails);

        var options = CreateBackpressureOptions(
            context,
            BackpressureMetadata.ServiceUnavailableCode,
            retryAfterSeconds,
            retryable);

        return StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
    }

    public static IResult CreateTooManyRequests(HttpContext context, string detail, int? retryAfterSeconds = null, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.TooManyRequests(detail, retryAfterSeconds, additionalDetails);

        var options = CreateBackpressureOptions(
            context,
            BackpressureMetadata.RateLimitExceededCode,
            retryAfterSeconds,
            retryable: true);

        return StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
    }

    internal static IResult CreateRequestTimeout(HttpContext context, string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = StandardErrorResponse.RequestTimeout(detail, additionalDetails);
        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateFromException(HttpContext context, Exception exception, bool includeDebugDetails = false)
    {
        var errorResponse = StandardErrorResponse.FromException(exception, includeDebugDetails);

        ErrorResponseFormatterOptions options = new()
        {
            IncludeDebugInfo = includeDebugDetails
        };

        if (errorResponse.StatusCode == StatusCodes.Status503ServiceUnavailable)
        {
            var retryAfterSeconds = (exception as ServiceUnavailableException)?.RetryAfterSeconds;
            options = CreateBackpressureOptions(
                context,
                BackpressureMetadata.ServiceUnavailableCode,
                retryAfterSeconds,
                retryable: true,
                includeDebugInfo: includeDebugDetails);
        }

        return StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
    }

    internal static IResult CreateValidationError(HttpContext context, ValidationException validationException)
    {
        var errorResponse = StandardErrorResponse.BadRequest(
            validationException.Message,
            validationException.Details);

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    internal static IResult CreateQueryParameterError(HttpContext context, string parameterName, string errorMessage)
    {
        // Use ExceptionMapper's sanitization logic for query parameter errors
        var argumentException = new ArgumentException(errorMessage, parameterName);
        return CreateFromException(context, argumentException, false);
    }

    public static IResult CreateCqlFilterError(HttpContext context, string cqlError)
    {
        var detail = $"Invalid CQL filter syntax: {SanitizeCqlErrorMessage(cqlError)}";
        return CreateBadRequest(context, detail);
    }

    public static IResult CreateGeometryError(HttpContext context, string geometryError)
    {
        var detail = $"Invalid geometry: {geometryError}";
        return CreateBadRequest(context, detail);
    }

    private static string SanitizeCqlErrorMessage(string errorMessage)
        => ErrorMessageSanitizer.Sanitize(errorMessage, "Syntax error in filter expression.");

    private static ErrorResponseFormatterOptions CreateBackpressureOptions(
        HttpContext context,
        string machineCode,
        int? retryAfterSeconds,
        bool retryable,
        bool includeDebugInfo = false)
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Correlation-ID"] = context.TraceIdentifier,
            ["Honua-Retryable"] = retryable.ToString().ToLowerInvariant(),
        };

        if (retryAfterSeconds.HasValue)
        {
            headers["Retry-After"] = retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ErrorResponseFormatterOptions
        {
            IncludeDebugInfo = includeDebugInfo,
            AdditionalHeaders = headers,
            MachineCode = machineCode,
            ODataErrorCode = machineCode,
            WfsExceptionCode = machineCode,
            WmsExceptionCode = machineCode,
            Retryable = retryable,
            RetryAfterSeconds = retryAfterSeconds,
        };
    }
}
