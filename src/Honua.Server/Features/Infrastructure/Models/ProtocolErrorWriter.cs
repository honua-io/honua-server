// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Legacy protocol error writer. This class is deprecated in favor of StandardErrorResponseFormatter.
/// </summary>
[Obsolete("Use StandardErrorResponseFormatter instead for consistent error handling across all protocols.")]
internal static class ProtocolErrorWriter
{
    private const string ODataContentType = "application/json;odata.metadata=minimal";
    private const string ODataVersion = "4.0";

    public static Task WriteErrorAsync(HttpContext context, int statusCode, string title, string detail)
    {
        var result = CreateErrorResult(context, statusCode, title, detail);
        return result.ExecuteAsync(context);
    }

    public static IResult CreateErrorResult(HttpContext context, int statusCode, string title, string detail)
    {
        var path = context.Request.Path;

        if (ProtocolRequestClassifier.IsOData(path))
        {
            return CreateODataError(context, statusCode, title, detail);
        }

        if (ProtocolRequestClassifier.IsOgc(path))
        {
            return ProblemDetailsHelpers.CreateOgcProblem(context, statusCode, title, detail);
        }

        if (ProtocolRequestClassifier.IsAdmin(path))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, title, detail);
        }

        if (ProtocolRequestClassifier.IsGeoServices(path))
        {
            return CreateGeoServicesError(statusCode, title, detail);
        }

        return ProblemDetailsHelpers.CreateProblem(context, "about:blank", statusCode, title, detail);
    }

    private static IResult CreateODataError(HttpContext context, int statusCode, string title, string detail)
    {
        SetODataHeaders(context);

        var code = ProtocolRequestClassifier.MapODataCode(statusCode, includeConflict: false);
        ErrorDetail[]? details = string.IsNullOrWhiteSpace(detail)
            ? null
            : [new ErrorDetail { Code = code, Message = detail }];

        var error = new ODataError
        {
            Error = new ErrorDetails
            {
                Code = code,
                Message = title,
                Details = details
            }
        };

        return Results.Json(error, ODataJsonContext.Default.ODataError, contentType: ODataContentType, statusCode: statusCode);
    }

    private static IResult CreateGeoServicesError(int statusCode, string title, string detail)
    {
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = GeoServicesErrorCodes.FromHttpStatusCode(statusCode),
                Message = title,
                Details = string.IsNullOrWhiteSpace(detail) ? null : [detail]
            }
        };

        return Results.Json(errorResponse, LimitsEnforcementJsonContext.Default.ApiErrorResponse, statusCode: statusCode);
    }

    private static void SetODataHeaders(HttpContext context)
    {
        context.Response.Headers["OData-Version"] = ODataVersion;
    }

}
