// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Infrastructure.Models;

internal static class ProblemDetailsHelpers
{
    internal const string ContentType = "application/problem+json";
    private const string OgcType = "about:blank";
    private const string AdminType = "https://honua.io/problems/admin";
    private const string SecurityType = "https://honua.io/problems/security";

    public static IResult CreateOgcProblem(HttpContext context, int statusCode, string detail)
        => CreateProblem(context, OgcType, statusCode, GetTitle(statusCode), detail);

    public static IResult CreateOgcProblem(HttpContext context, int statusCode, string title, string detail)
        => CreateProblem(context, OgcType, statusCode, title, detail);

    public static IResult CreateOgcProblem(int statusCode, string title, string detail, string? instance = null)
        => CreateProblem(OgcType, statusCode, title, detail, instance);

    public static IResult CreateAdminProblem(HttpContext context, int statusCode, string detail)
        => CreateProblem(context, AdminType, statusCode, GetTitle(statusCode), detail);

    public static IResult CreateAdminProblem(HttpContext context, int statusCode, string title, string detail)
        => CreateProblem(context, AdminType, statusCode, title, detail);

    public static ProblemDetailsResponse CreateAdminProblemDetails(HttpContext context, int statusCode, string detail)
        => CreateProblemDetails(AdminType, statusCode, GetTitle(statusCode), detail, BuildInstance(context), context);

    public static ProblemDetailsResponse CreateAdminProblemDetails(HttpContext context, int statusCode, string title, string detail)
        => CreateProblemDetails(AdminType, statusCode, title, detail, BuildInstance(context), context);

    public static IResult CreateAdminProblem(int statusCode, string title, string detail, string? instance = null)
        => CreateProblem(AdminType, statusCode, title, detail, instance);

    public static IResult CreateSecurityProblem(HttpContext context, int statusCode, string title, string detail)
        => CreateProblem(context, SecurityType, statusCode, title, detail);

    public static IResult CreateProblem(HttpContext context, string type, int statusCode, string title, string detail)
    {
        var instance = BuildInstance(context);
        var problemDetails = CreateProblemDetails(type, statusCode, title, detail, instance, context);

        return Results.Json(
            problemDetails,
            ProblemJsonContext.Default.ProblemDetailsResponse,
            statusCode: statusCode,
            contentType: ContentType);
    }

    public static IResult CreateProblem(string type, int statusCode, string title, string detail, string? instance = null)
    {
        var problemDetails = CreateProblemDetails(type, statusCode, title, detail, instance, context: null);

        return Results.Json(
            problemDetails,
            ProblemJsonContext.Default.ProblemDetailsResponse,
            statusCode: statusCode,
            contentType: ContentType);
    }

    private static string? BuildInstance(HttpContext context)
    {
        if (!context.Request.Path.HasValue)
        {
            return null;
        }

        return context.Request.Path.Value ?? string.Empty;
    }

    private static ProblemDetailsResponse CreateProblemDetails(
        string type,
        int statusCode,
        string title,
        string detail,
        string? instance,
        HttpContext? context)
    {
        var correlationId = context?.TraceIdentifier;
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        return new ProblemDetailsResponse
        {
            Type = type,
            Title = title,
            Status = statusCode,
            Detail = detail,
            Instance = instance,
            CorrelationId = correlationId,
            Timestamp = timestamp
        };
    }

    internal static string GetTitle(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status405MethodNotAllowed => "Method Not Allowed",
        StatusCodes.Status408RequestTimeout => "Request Timeout",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported Media Type",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        _ => "Error"
    };
}
