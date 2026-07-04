// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security;
using Honua.Infrastructure.Middleware;
using Honua.ServiceDefaults;

namespace Honua.Infrastructure.Models;

/// <summary>
/// Provides protocol-specific formatting for StandardErrorResponse instances.
/// Supports GeoServices, OGC API, OData (via delegate registration), and
/// generic Problem Details formats.
/// </summary>
/// <remarks>
/// Audit-A1: this type lives in <c>Honua.Infrastructure.Models</c>
/// and must not take a using-clause dependency on any protocol assembly so the
/// Honua.Infrastructure.Models sub-area can extract into a <c>Honua.Hosting.Models</c>
/// assembly without a back-edge. Protocol-specific formatters that need a
/// payload shape only the protocol owns (today only OData's
/// <c>ODataError</c> JSON envelope) plug in via
/// <see cref="ODataErrorFormatterOverride"/>. The OData wiring sets the
/// override during <c>ODataServiceCollectionExtensions.AddOData</c>.
/// </remarks>
internal static class StandardErrorResponseFormatter
{
    /// <summary>
    /// Optional OData error formatter. When set, requests classified as OData
    /// (<see cref="ProtocolRequestClassifier.IsOData"/>) are formatted via the
    /// delegate. When null, OData requests fall through to the generic Problem
    /// Details path. The OData protocol's service-registration entry point
    /// (<c>AddOData</c>) installs the delegate at startup.
    /// </summary>
    internal static Func<HttpContext, StandardErrorResponse, ErrorResponseFormatterOptions, IResult>? ODataErrorFormatterOverride { get; set; }

    /// <summary>
    /// Optional sink for recording errors into the server-side
    /// <c>RecentErrorBuffer</c>. When set, every formatted error is also
    /// recorded via the delegate. The buffer lives in
    /// <c>Honua.Infrastructure.Monitoring</c> (a Server-side
    /// sub-area that has not been carved into <c>Honua.Hosting</c>); the
    /// observability service-registration entry point in Server installs the
    /// delegate at startup. Mirrors the
    /// <see cref="ODataErrorFormatterOverride"/> pattern for the
    /// audit-A1 Hosting-vs-Server boundary.
    /// </summary>
    internal static Action<HttpContext, StandardErrorResponse>? RecentErrorBufferRecordOverride { get; set; }

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
        RecordErrorTelemetry(context, errorResponse.StatusCode);

        var path = context.Request.Path;

        if (ProtocolRequestClassifier.IsOData(path))
        {
            return FormatODataError(context, errorResponse, options);
        }

        if (ProtocolRequestClassifier.IsOgcServiceAlias(path))
        {
            return FormatWfsError(context, errorResponse, options);
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
    /// Formats error for OData v4 protocol via the registered override
    /// delegate, or falls back to generic Problem Details when no OData
    /// formatter has been wired (e.g. OData is disabled by configuration).
    /// </summary>
    private static IResult FormatODataError(HttpContext context, StandardErrorResponse errorResponse, ErrorResponseFormatterOptions options)
    {
        var formatter = ODataErrorFormatterOverride;
        if (formatter is null)
        {
            return FormatGenericError(context, errorResponse, options);
        }
        AddResponseHeaders(context, options);
        return formatter(context, errorResponse, options);
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

        // OWS 1.1 XSD (owsExceptionReport.xsd) declares `language` as use="required" on the
        // ExceptionReport element. Omitting it causes schema-level rejection by strict validators.
        var xmlContent = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1" version="2.0.0" language="en">
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
        => RecentErrorBufferRecordOverride?.Invoke(context, errorResponse);

    /// <summary>
    /// Emits the keystone error-rate telemetry (#2243) for a produced error
    /// envelope. Every error-envelope construction path funnels here — both the
    /// central <see cref="FormatError"/> dispatch and the GeoServices builders in
    /// <c>ValidationErrorHelpers</c> that bypass this formatter — so the platform
    /// can aggregate and alert on its own compat error rate. Classifies the
    /// surface from the request path and delegates to
    /// <see cref="HonuaTelemetry.RecordErrorEnvelope"/>.
    /// </summary>
    /// <param name="context">The HTTP context for protocol/operation classification.</param>
    /// <param name="statusCode">The HTTP status carried by the error envelope.</param>
    internal static void RecordErrorTelemetry(HttpContext context, int statusCode)
    {
        var path = context.Request.Path;
        var (serviceType, isGeoServices) = ClassifyServiceType(path);
        HonuaTelemetry.RecordErrorEnvelope(serviceType, ResolveOperation(path), statusCode, isGeoServices);
    }

    private static readonly string[] GeoServicesServiceTypes =
    [
        "FeatureServer",
        "MapServer",
        "ImageServer",
        "VectorTileServer",
        "GPServer",
        "GeocodeServer",
        "GeometryServer",
        "NAServer",
        "SceneServer"
    ];

    private static (string ServiceType, bool IsGeoServices) ClassifyServiceType(PathString path)
    {
        if (ProtocolRequestClassifier.IsOData(path))
        {
            return ("OData", false);
        }

        if (ProtocolRequestClassifier.IsOgcServiceAlias(path))
        {
            return ("OGC-Service", false);
        }

        if (ProtocolRequestClassifier.IsOgc(path))
        {
            return ("OGC", false);
        }

        if (ProtocolRequestClassifier.IsWfs(path))
        {
            return ("WFS", false);
        }

        if (ProtocolRequestClassifier.IsAdmin(path))
        {
            return ("Admin", false);
        }

        if (ProtocolRequestClassifier.IsGeoServices(path))
        {
            return (ExtractGeoServicesServiceType(path), true);
        }

        return ("Generic", false);
    }

    private static string ExtractGeoServicesServiceType(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return "GeoServices";
        }

        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var type in GeoServicesServiceTypes)
            {
                if (string.Equals(segment, type, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return "GeoServices";
    }

    private static string ResolveOperation(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "unknown";
        }

        // Use the last segment as the operation only when it is purely alphabetic
        // (Esri operations like query/addFeatures/applyEdits/exportImage). Numeric
        // or mixed segments are identifiers (layer/feature ids) and would explode
        // metric cardinality, so they collapse to "unknown".
        var last = segments[^1];
        foreach (var ch in last)
        {
            if (!char.IsLetter(ch))
            {
                return "unknown";
            }
        }

        return last.ToLowerInvariant();
    }
}
