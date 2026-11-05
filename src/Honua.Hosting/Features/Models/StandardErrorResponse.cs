// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Models;

internal sealed record StandardErrorResponse(
    int StatusCode,
    string Title,
    string Detail,
    IReadOnlyList<string>? AdditionalDetails = null,
    string? DebugInfo = null)
{
    public static StandardErrorResponse FromException(Exception exception, bool includeDebugDetails = false)
    {
        var result = ExceptionMapper.Map(exception, includeDebugDetails);

        return new StandardErrorResponse(
            StatusCode: result.StatusCode,
            Title: result.Title,
            Detail: result.Detail,
            AdditionalDetails: result.Details,
            DebugInfo: result.DebugInfo
        );
    }

    public static StandardErrorResponse BadRequest(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status400BadRequest,
            Title: "Bad Request",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse Unauthorized(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status401Unauthorized,
            Title: "Unauthorized",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse Forbidden(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status403Forbidden,
            Title: "Forbidden",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse PaymentRequired(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status402PaymentRequired,
            Title: "Payment Required",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse NotFound(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status404NotFound,
            Title: "Not Found",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse Conflict(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status409Conflict,
            Title: "Conflict",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse PreconditionFailed(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status412PreconditionFailed,
            Title: "Precondition Failed",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse InternalServerError(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status500InternalServerError,
            Title: "Internal Server Error",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    public static StandardErrorResponse ServiceUnavailable(string detail, int? retryAfterSeconds = null, IReadOnlyList<string>? additionalDetails = null)
    {
        var details = additionalDetails?.ToList() ?? [];
        if (retryAfterSeconds.HasValue)
        {
            details.Add($"Retry-After: {retryAfterSeconds}s");
        }

        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status503ServiceUnavailable,
            Title: "Service Unavailable",
            Detail: detail,
            AdditionalDetails: details.Count > 0 ? details : null
        );
    }

    public static StandardErrorResponse TooManyRequests(string detail, int? retryAfterSeconds = null, IReadOnlyList<string>? additionalDetails = null)
    {
        var details = additionalDetails?.ToList() ?? [];
        if (retryAfterSeconds.HasValue)
        {
            details.Add($"Retry-After: {retryAfterSeconds}s");
        }

        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status429TooManyRequests,
            Title: "Too Many Requests",
            Detail: detail,
            AdditionalDetails: details.Count > 0 ? details : null
        );
    }

    public static StandardErrorResponse RequestTimeout(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status408RequestTimeout,
            Title: "Request Timeout",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }
}

internal sealed class ErrorResponseFormatterOptions
{
    public bool IncludeAdditionalDetails { get; init; } = true;

    public bool IncludeDebugInfo { get; init; }

    public string? ContentType { get; init; }

    public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; init; }

    public string? WfsExceptionCode { get; init; }

    public string? WfsExceptionLocator { get; init; }

    public string? WcsExceptionCode { get; init; }

    public string? WmtsExceptionCode { get; init; }

    public string? WmsExceptionCode { get; init; }

    public string? ODataErrorCode { get; init; }

    public string? MachineCode { get; init; }

    public int? GeoServicesBodyCode { get; init; }

    // GeoServices errors stay HTTP 200 except a rejection the client reads from the transport status alone, and a token challenge stays on 200.
    public bool GeoServicesUseHttpStatus { get; init; }

    public bool? Retryable { get; init; }

    public int? RetryAfterSeconds { get; init; }

}
