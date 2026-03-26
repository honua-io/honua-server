// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Provides protocol-specific formatting for StandardErrorResponse instances.
/// Supports GeoServices, OGC API, OData, and generic Problem Details formats.
/// </summary>
internal static class StandardErrorResponseFormatter
{
    private const string ODataContentType = "application/json;odata.metadata=minimal";
    private const string ODataVersion = "4.0";

    /// <summary>
    /// Formats a StandardErrorResponse into an appropriate IResult based on the request protocol.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="errorResponse">The standard error response to format.</param>
    /// <param name="options">Optional formatting options.</param>
    /// <returns>An IResult formatted for the detected protocol.</returns>
    internal static IResult FormatError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions? options = null)
    {
        options ??= new ErrorResponseFormatterOptions();

        TryRecordRecentError(context, errorResponse);

        var path = context.Request.Path;

        if (ProtocolRequestClassifier.IsOData(path))
        {
            return FormatODataError(context, errorResponse, options);
        }

        if (ProtocolRequestClassifier.IsOgc(path))
        {
            return FormatOgcError(context, errorResponse, options);
        }

        if (ProtocolRequestClassifier.IsWfs(path))
        {
            return FormatWfsError(context, errorResponse, options);
        }

        if (ProtocolRequestClassifier.IsAdmin(path))
        {
            return FormatAdminError(context, errorResponse, options);
        }

        if (ProtocolRequestClassifier.IsGeoServices(path))
        {
            return FormatGeoServicesError(context, errorResponse, options);
        }

        // Default to generic Problem Details format
        return FormatGenericError(context, errorResponse, options);
    }

    /// <summary>
    /// Writes a StandardErrorResponse directly to the HTTP response using protocol-specific formatting.
    /// </summary>
    /// <param name="context">The HTTP context for protocol detection.</param>
    /// <param name="errorResponse">The standard error response to write.</param>
    /// <param name="options">Optional formatting options.</param>
    /// <returns>A Task representing the asynchronous write operation.</returns>
    internal static Task WriteErrorAsync(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions? options = null)
    {
        var result = FormatError(context, errorResponse, options);
        return result.ExecuteAsync(context);
    }

    /// <summary>
    /// Formats error for OData v4 protocol.
    /// </summary>
    private static IResult FormatODataError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        SetODataHeaders(context, options);

        var code = ProtocolRequestClassifier.MapODataCode(errorResponse.StatusCode, includeConflict: true);
        var details = BuildODataDetails(context, errorResponse, options);

        var error = new ODataError
        {
            Error = new ErrorDetails
            {
                Code = code,
                Message = errorResponse.Title,
                Details = details
            }
        };

        return Results.Json(
            error,
            ODataJsonContext.Default.ODataError,
            contentType: options.ContentType ?? ODataContentType,
            statusCode: errorResponse.StatusCode);
    }

    /// <summary>
    /// Formats error for OGC API Features protocol.
    /// </summary>
    private static IResult FormatOgcError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        return ProblemDetailsHelpers.CreateProblem(
            context,
            type: "about:blank",
            statusCode: errorResponse.StatusCode,
            title: errorResponse.Title,
            detail: BuildDetailWithExtras(errorResponse, options));
    }

    /// <summary>
    /// Formats error for WFS 2.0 protocol.
    /// </summary>
    private static IResult FormatWfsError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        AddResponseHeaders(context, options);
        var exceptionCode = string.IsNullOrWhiteSpace(options.WfsExceptionCode)
            ? MapWfsCode(errorResponse)
            : options.WfsExceptionCode;
        var locatorAttribute = string.IsNullOrWhiteSpace(options.WfsExceptionLocator)
            ? string.Empty
            : $" locator=\"{EscapeForXml(options.WfsExceptionLocator)}\"";

        var xmlContent = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1" version="1.1.0">
              <ows:Exception exceptionCode="{{EscapeForXml(exceptionCode!)}}"{{locatorAttribute}}>
                <ows:ExceptionText>{{EscapeForXml(BuildDetailWithExtras(errorResponse, options))}}</ows:ExceptionText>
              </ows:Exception>
            </ows:ExceptionReport>
            """;

        return Results.Content(
            xmlContent,
            "application/xml",
            System.Text.Encoding.UTF8,
            errorResponse.StatusCode);
    }

    /// <summary>
    /// Formats error for Admin API protocol.
    /// </summary>
    private static IResult FormatAdminError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        return ProblemDetailsHelpers.CreateProblem(
            context,
            type: "https://honua.io/problems/admin",
            statusCode: errorResponse.StatusCode,
            title: errorResponse.Title,
            detail: BuildDetailWithExtras(errorResponse, options));
    }

    /// <summary>
    /// Formats error for GeoServices protocol.
    /// </summary>
    private static IResult FormatGeoServicesError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        var details = BuildGeoServicesDetails(context, errorResponse, options);

        var apiErrorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = GeoServicesErrorCodes.FromHttpStatusCode(errorResponse.StatusCode),
                Message = errorResponse.Title,
                Details = details?.Length > 0 ? details : null
            }
        };

        AddResponseHeaders(context, options);

        return Results.Json(
            apiErrorResponse,
            LimitsEnforcementJsonContext.Default.ApiErrorResponse,
            statusCode: errorResponse.StatusCode);
    }

    /// <summary>
    /// Formats error using generic Problem Details format.
    /// </summary>
    private static IResult FormatGenericError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        AddResponseHeaders(context, options);
        return ProblemDetailsHelpers.CreateProblem(
            context,
            type: "about:blank",
            statusCode: errorResponse.StatusCode,
            title: errorResponse.Title,
            detail: BuildDetailWithExtras(errorResponse, options));
    }

    /// <summary>
    /// Builds OData error details array.
    /// </summary>
    private static ErrorDetail[]? BuildODataDetails(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        var detailsList = new List<ErrorDetail>();

        // Always include the main detail
        if (!string.IsNullOrWhiteSpace(errorResponse.Detail))
        {
            detailsList.Add(new ErrorDetail
            {
                Code = ProtocolRequestClassifier.MapODataCode(errorResponse.StatusCode, includeConflict: true),
                Message = errorResponse.Detail
            });
        }

        // Include additional details if requested
        if (options.IncludeAdditionalDetails && errorResponse.AdditionalDetails is { Count: > 0 })
        {
            foreach (var detail in errorResponse.AdditionalDetails)
            {
                detailsList.Add(new ErrorDetail
                {
                    Code = ProtocolRequestClassifier.MapODataCode(errorResponse.StatusCode, includeConflict: true),
                    Message = detail
                });
            }
        }

        // Include debug info if requested
        if (options.IncludeDebugInfo && !string.IsNullOrWhiteSpace(errorResponse.DebugInfo))
        {
            detailsList.Add(new ErrorDetail
            {
                Code = "Debug",
                Message = errorResponse.DebugInfo
            });
        }

        var metadata = BuildErrorMetadata(context);
        if (!string.IsNullOrWhiteSpace(metadata.CorrelationId))
        {
            detailsList.Add(new ErrorDetail
            {
                Code = "CorrelationId",
                Message = metadata.CorrelationId
            });
        }

        detailsList.Add(new ErrorDetail
        {
            Code = "Timestamp",
            Message = metadata.Timestamp
        });

        return detailsList.Count > 0 ? [.. detailsList] : null;
    }

    /// <summary>
    /// Builds GeoServices error details array.
    /// </summary>
    private static string[]? BuildGeoServicesDetails(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        var detailsList = new List<string>();

        // Always include the main detail
        if (!string.IsNullOrWhiteSpace(errorResponse.Detail))
        {
            detailsList.Add(errorResponse.Detail);
        }

        // Include additional details if requested
        if (options.IncludeAdditionalDetails && errorResponse.AdditionalDetails is { Count: > 0 })
        {
            detailsList.AddRange(errorResponse.AdditionalDetails);
        }

        // Include debug info if requested
        if (options.IncludeDebugInfo && !string.IsNullOrWhiteSpace(errorResponse.DebugInfo))
        {
            detailsList.Add($"Debug: {errorResponse.DebugInfo}");
        }

        var metadata = BuildErrorMetadata(context);
        if (!string.IsNullOrWhiteSpace(metadata.CorrelationId))
        {
            detailsList.Add($"CorrelationId: {metadata.CorrelationId}");
        }

        detailsList.Add($"Timestamp: {metadata.Timestamp}");

        return detailsList.Count > 0 ? [.. detailsList] : null;
    }

    /// <summary>
    /// Builds combined detail message with extras for Problem Details.
    /// </summary>
    private static string BuildDetailWithExtras(StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        var detailParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(errorResponse.Detail))
        {
            detailParts.Add(errorResponse.Detail);
        }

        if (options.IncludeAdditionalDetails && errorResponse.AdditionalDetails is { Count: > 0 })
        {
            detailParts.AddRange(errorResponse.AdditionalDetails);
        }

        if (options.IncludeDebugInfo && !string.IsNullOrWhiteSpace(errorResponse.DebugInfo))
        {
            detailParts.Add($"Debug: {errorResponse.DebugInfo}");
        }

        return detailParts.Count > 0 ? string.Join(" ", detailParts) : errorResponse.Title;
    }

    private static string MapWfsCode(StandardErrorResponse errorResponse)
    {
        if (errorResponse.StatusCode == StatusCodes.Status501NotImplemented)
        {
            return "OperationNotSupported";
        }

        if (errorResponse.StatusCode == StatusCodes.Status400BadRequest)
        {
            if (!string.IsNullOrWhiteSpace(errorResponse.Detail) &&
                errorResponse.Detail.Contains("missing", StringComparison.OrdinalIgnoreCase))
            {
                return "MissingParameterValue";
            }

            if (!string.IsNullOrWhiteSpace(errorResponse.Detail) &&
                errorResponse.Detail.Contains("version", StringComparison.OrdinalIgnoreCase))
            {
                return "VersionNegotiationFailed";
            }

            return "InvalidParameterValue";
        }

        return "NoApplicableCode";
    }

    private static string EscapeForXml(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    /// <summary>
    /// Sets OData-specific headers.
    /// </summary>
    private static void SetODataHeaders(HttpContext context, ErrorResponseFormatterOptions options)
    {
        context.Response.Headers["OData-Version"] = ODataVersion;
        AddResponseHeaders(context, options);
    }

    /// <summary>
    /// Adds additional headers if specified in options.
    /// </summary>
    private static void AddResponseHeaders(HttpContext context, ErrorResponseFormatterOptions options)
    {
        if (options.AdditionalHeaders is { Count: > 0 })
        {
            foreach (var header in options.AdditionalHeaders)
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }
    }

    private readonly record struct ErrorMetadata(string? CorrelationId, string Timestamp);

    private static ErrorMetadata BuildErrorMetadata(HttpContext context)
    {
        var correlationId = context.TraceIdentifier;
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new ErrorMetadata(correlationId, timestamp);
    }

    private static void TryRecordRecentError(HttpContext context, StandardErrorResponse errorResponse)
    {
        var services = context.RequestServices;
        if (services is null)
        {
            return;
        }

        var buffer = services.GetService<RecentErrorBuffer>();
        buffer?.Record(context, errorResponse);
    }

}
