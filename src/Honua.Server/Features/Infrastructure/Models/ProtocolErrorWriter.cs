// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.Infrastructure.Models;

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

        if (IsOData(path))
        {
            return CreateODataError(context, statusCode, title, detail);
        }

        if (IsOgc(path))
        {
            return ProblemDetailsHelpers.CreateOgcProblem(context, statusCode, title, detail);
        }

        if (IsAdmin(path))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, title, detail);
        }

        if (IsGeoServices(path))
        {
            return CreateGeoServicesError(statusCode, title, detail);
        }

        return ProblemDetailsHelpers.CreateProblem(context, "about:blank", statusCode, title, detail);
    }

    private static IResult CreateODataError(HttpContext context, int statusCode, string title, string detail)
    {
        SetODataHeaders(context);

        var code = MapODataCode(statusCode);
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

    private static string MapODataCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "BadRequest",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "ResourceNotFound",
        StatusCodes.Status408RequestTimeout => "RequestTimeout",
        StatusCodes.Status413PayloadTooLarge => "PayloadTooLarge",
        StatusCodes.Status429TooManyRequests => "TooManyRequests",
        StatusCodes.Status500InternalServerError => "InternalServerError",
        StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
        _ => "Error"
    };

    private static bool IsOData(PathString path) => path.StartsWithSegments("/odata");

    private static bool IsOgc(PathString path) =>
        path.StartsWithSegments("/ogc/features") ||
        path.StartsWithSegments("/ogc/tiles") ||
        path.StartsWithSegments("/collections");

    private static bool IsAdmin(PathString path) =>
        path.StartsWithSegments("/api/v1/admin") ||
        path.StartsWithSegments("/api/admin") ||
        path.StartsWithSegments("/api/import");

    private static bool IsGeoServices(PathString path) =>
        path.StartsWithSegments("/rest/services") ||
        path.StartsWithSegments("/tiles");
}
