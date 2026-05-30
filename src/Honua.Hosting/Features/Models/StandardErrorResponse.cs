// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Models;

/// <summary>
/// Standard error response that can be formatted for different protocols.
/// Contains all information needed to generate protocol-specific error responses.
/// </summary>
internal sealed record StandardErrorResponse(
    int StatusCode,
    string Title,
    string Detail,
    IReadOnlyList<string>? AdditionalDetails = null,
    string? DebugInfo = null)
{
    /// <summary>
    /// Creates a StandardErrorResponse from an exception using the ExceptionMapper.
    /// </summary>
    /// <param name="exception">The exception to convert.</param>
    /// <param name="includeDebugDetails">Whether to include debug details (typically only in Development).</param>
    /// <returns>A StandardErrorResponse instance.</returns>
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

    /// <summary>
    /// Creates a BadRequest StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A BadRequest StandardErrorResponse.</returns>
    public static StandardErrorResponse BadRequest(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status400BadRequest,
            Title: "Bad Request",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates an Unauthorized StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>An Unauthorized StandardErrorResponse.</returns>
    public static StandardErrorResponse Unauthorized(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status401Unauthorized,
            Title: "Unauthorized",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates a Forbidden StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A Forbidden StandardErrorResponse.</returns>
    public static StandardErrorResponse Forbidden(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status403Forbidden,
            Title: "Forbidden",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates a PaymentRequired StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A PaymentRequired StandardErrorResponse.</returns>
    public static StandardErrorResponse PaymentRequired(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status402PaymentRequired,
            Title: "Payment Required",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates a NotFound StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A NotFound StandardErrorResponse.</returns>
    public static StandardErrorResponse NotFound(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status404NotFound,
            Title: "Not Found",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates a Conflict StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A Conflict StandardErrorResponse.</returns>
    public static StandardErrorResponse Conflict(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status409Conflict,
            Title: "Conflict",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates a PreconditionFailed StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A PreconditionFailed StandardErrorResponse.</returns>
    public static StandardErrorResponse PreconditionFailed(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status412PreconditionFailed,
            Title: "Precondition Failed",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates an InternalServerError StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>An InternalServerError StandardErrorResponse.</returns>
    public static StandardErrorResponse InternalServerError(string detail, IReadOnlyList<string>? additionalDetails = null)
    {
        return new StandardErrorResponse(
            StatusCode: StatusCodes.Status500InternalServerError,
            Title: "Internal Server Error",
            Detail: detail,
            AdditionalDetails: additionalDetails
        );
    }

    /// <summary>
    /// Creates a ServiceUnavailable StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="retryAfterSeconds">Optional retry-after seconds.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A ServiceUnavailable StandardErrorResponse.</returns>
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

    /// <summary>
    /// Creates a RequestTimeout StandardErrorResponse.
    /// </summary>
    /// <param name="detail">The error detail message.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A RequestTimeout StandardErrorResponse.</returns>
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

/// <summary>
/// Response formatter options for protocol-specific error responses.
/// </summary>
internal sealed class ErrorResponseFormatterOptions
{
    /// <summary>
    /// Whether to include additional details in the response.
    /// </summary>
    public bool IncludeAdditionalDetails { get; init; } = true;

    /// <summary>
    /// Whether to include debug information (only in Development).
    /// </summary>
    public bool IncludeDebugInfo { get; init; }

    /// <summary>
    /// Protocol-specific content type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Additional headers to include in the response.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; init; }

    /// <summary>
    /// Explicit WFS exception code to emit instead of inferring one heuristically.
    /// </summary>
    public string? WfsExceptionCode { get; init; }

    /// <summary>
    /// Optional WFS exception locator attribute.
    /// </summary>
    public string? WfsExceptionLocator { get; init; }

    /// <summary>
    /// Explicit OData error code to emit instead of inferring one from the HTTP status code.
    /// </summary>
    public string? ODataErrorCode { get; init; }
}
