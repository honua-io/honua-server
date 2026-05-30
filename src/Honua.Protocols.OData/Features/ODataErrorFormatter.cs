// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;

namespace Honua.Protocols.OData;

/// <summary>
/// Protocol-specific error formatter for OData v4 responses. Owns the
/// OData-Version header, the OData JSON envelope (<see cref="ODataError"/>),
/// and the OData details array. Registered as the
/// <c>StandardErrorResponseFormatter.ODataErrorFormatterOverride</c> delegate
/// during <c>ODataServiceCollectionExtensions.AddOData</c> so the
/// Infrastructure.Models error router does not need to take a using-clause
/// dependency on the OData protocol assembly.
/// </summary>
/// <remarks>
/// Audit-A1 unblock: removing the
/// <c>using Honua.Protocols.OData.Models</c> and
/// <c>using Honua.Protocols.OData.Services</c> back-edge
/// from <c>Honua.Server.Features.Infrastructure.Models.StandardErrorResponseFormatter</c>
/// is what lets the Infrastructure.Models sub-area extract into a
/// <c>Honua.Hosting.Models</c> assembly without ProjectReferencing OData.
/// </remarks>
internal static class ODataErrorFormatter
{
    private const string ODataContentType = "application/json;metadata=minimal";

    /// <summary>
    /// Formats a <see cref="StandardErrorResponse"/> as an OData v4 error
    /// payload. Signature matches the
    /// <c>StandardErrorResponseFormatter.ODataErrorFormatterOverride</c>
    /// delegate so the formatter can be registered directly.
    /// </summary>
    internal static IResult Format(
        HttpContext context,
        StandardErrorResponse errorResponse,
        ErrorResponseFormatterOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(errorResponse);
        ArgumentNullException.ThrowIfNull(options);

        SetODataHeaders(context, options);

        var code = string.IsNullOrWhiteSpace(options.ODataErrorCode)
            ? ProtocolRequestClassifier.MapODataCode(errorResponse.StatusCode, includeConflict: true)
            : options.ODataErrorCode;
        var details = BuildODataDetails(context, errorResponse, options);

        var error = new ODataError
        {
            Error = new ErrorDetails
            {
                Code = code,
                Message = string.IsNullOrWhiteSpace(errorResponse.Detail)
                    ? errorResponse.Title
                    : errorResponse.Detail,
                Details = details
            }
        };

        return Results.Json(
            error,
            ODataJsonContext.Default.ODataError,
            contentType: options.ContentType ?? ODataContentType,
            statusCode: errorResponse.StatusCode);
    }

    private static void SetODataHeaders(HttpContext context, ErrorResponseFormatterOptions options)
    {
        ODataProtocolHeaders.SetVersionHeader(context);

        if (options.AdditionalHeaders is { Count: > 0 })
        {
            foreach (var header in options.AdditionalHeaders)
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }
    }

    private static ErrorDetail[]? BuildODataDetails(
        HttpContext context,
        StandardErrorResponse errorResponse,
        ErrorResponseFormatterOptions options)
    {
        var detailsList = new List<ErrorDetail>();

        if (!string.IsNullOrWhiteSpace(errorResponse.Detail))
        {
            detailsList.Add(new ErrorDetail
            {
                Code = ProtocolRequestClassifier.MapODataCode(errorResponse.StatusCode, includeConflict: true),
                Message = errorResponse.Detail
            });
        }

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

        if (options.IncludeDebugInfo && !string.IsNullOrWhiteSpace(errorResponse.DebugInfo))
        {
            detailsList.Add(new ErrorDetail
            {
                Code = "Debug",
                Message = errorResponse.DebugInfo
            });
        }

        var correlationId = context.TraceIdentifier;
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            detailsList.Add(new ErrorDetail
            {
                Code = "CorrelationId",
                Message = correlationId
            });
        }

        detailsList.Add(new ErrorDetail
        {
            Code = "Timestamp",
            Message = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });

        return detailsList.Count > 0 ? [.. detailsList] : null;
    }
}
