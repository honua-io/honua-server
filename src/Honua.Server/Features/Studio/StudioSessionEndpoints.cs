// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Studio.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Studio;

internal static partial class StudioSessionEndpoints
{
    internal const string EnvironmentIdKey = "Studio:Session:EnvironmentId";
    internal const string SourcePathBaseKey = "Studio:Session:SourcePathBase";
    internal const string DefaultEnvironmentId = "default";

    public static void MapStudioSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Same gate as the Studio lifecycle routes the Console calls next: admin (cookie session or
        // operator bearer) always; any authenticated principal once end-user Studio authorization
        // is enabled.
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/studio")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Studio")
            .RequireStudioLifecycleAuthorization();

        group.MapGet("/session", HandleGetSession)
            .WithName("GetStudioSession")
            .WithDisplayName("Get Studio Session")
            .WithSummary("Returns the authenticated caller's Studio principal id, the server's environment id and its API path base.")
            .Produces<ApiResponse<StudioSessionBootstrap>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult HandleGetSession(
        HttpContext context,
        [FromServices] StudioEndpointAuthorization authorization,
        [FromServices] IConfiguration configuration)
    {
        context.Response.Headers.CacheControl = "no-store";

        var principalId = authorization.ResolveCallerId(context.User);
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "No Studio principal",
                detail: "The credential does not carry a stable principal id.");
        }

        var environmentId = ResolveEnvironmentId(configuration);
        var sourcePathBase = ResolveSourcePathBase(configuration, context.Request.PathBase);
        if (environmentId is null || sourcePathBase is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Studio session is misconfigured",
                detail: $"Check {EnvironmentIdKey} and {SourcePathBaseKey}.");
        }

        var session = new StudioSessionBootstrap
        {
            PrincipalId = principalId,
            EnvironmentId = environmentId,
            SourcePathBase = sourcePathBase,
            TenantId = StudioEndpointAuthorization.ResolveRequestTenantId(context),
        };
        return Results.Json(
            ApiResponse<StudioSessionBootstrap>.CreateSuccess(session),
            StudioApiJsonContext.Default.ApiResponseStudioSessionBootstrap);
    }

    internal static string? ResolveEnvironmentId(IConfiguration configuration)
    {
        var configured = configuration[EnvironmentIdKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultEnvironmentId;
        }

        return EnvironmentIdPattern().IsMatch(configured) ? configured : null;
    }

    internal static string? ResolveSourcePathBase(IConfiguration configuration, PathString requestPathBase)
    {
        var configured = configuration[SourcePathBaseKey];
        var value = string.IsNullOrWhiteSpace(configured) ? requestPathBase.Value ?? string.Empty : configured.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)
            || value.IndexOfAny(['?', '#', '\\']) >= 0 || value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return value.TrimEnd('/');
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentIdPattern();
}
