// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Features.Capabilities.Models;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Capabilities;

internal static class CapabilityManifestEndpoints
{
    public static void MapCapabilityManifestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/capabilities")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Capabilities")
            .AllowAnonymous();

        group.MapGet("/manifest", HandleGetManifest)
            .WithDisplayName("Get Capability Manifest")
            .WithName("GetCapabilityManifest")
            .WithSummary("Returns the server capability manifest for the current tenant, workspace, environment, and caller policy scope.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<CapabilityManifestDocument>(
                StatusCodes.Status200OK,
                CapabilityManifestConstants.JsonContentType,
                CapabilityManifestConstants.ManifestContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .AllowAnonymous();
    }

    private static async Task<IResult> HandleGetManifest(
        [FromServices] ICapabilityManifestService manifestService,
        [FromServices] ITenantContext tenantContext,
        [FromServices] ILogger<CapabilityManifestEndpointLog> logger,
        HttpContext context,
        [FromQuery] string? environment = null,
        [FromQuery] string? workspaceId = null)
    {
        SetNoStoreHeaders(context);

        if (!TryValidateOptionalScopeIdentifier(environment, "environment", out var environmentError))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                environmentError ?? "Invalid capability manifest scope.");
        }

        if (!TryValidateOptionalScopeIdentifier(workspaceId, "workspaceId", out var workspaceError))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                workspaceError ?? "Invalid capability manifest scope.");
        }

        try
        {
            var request = new CapabilityManifestRequest(
                context.User,
                tenantContext.TenantId,
                tenantContext.Source,
                NormalizeOptional(environment),
                NormalizeOptional(workspaceId),
                context.User.Identity?.IsAuthenticated == true);
            var manifest = await manifestService
                .GetManifestAsync(request, context.RequestAborted)
                .ConfigureAwait(false);

            return Results.Json(
                manifest,
                CapabilityManifestJsonContext.Default.CapabilityManifestDocument,
                contentType: ResolveResponseContentType(context));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CapabilityManifestEndpointLogging.ManifestFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "Failed to generate the capability manifest.");
        }
    }

    private static void SetNoStoreHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = CapabilityManifestConstants.NoStoreCacheControl;
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static string ResolveResponseContentType(HttpContext context)
    {
        var accept = context.Request.Headers.Accept.ToString();
        return accept.Contains(CapabilityManifestConstants.ManifestContentType, StringComparison.OrdinalIgnoreCase)
            ? CapabilityManifestConstants.ManifestContentType
            : CapabilityManifestConstants.JsonContentType;
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryValidateOptionalScopeIdentifier(
        string? value,
        string parameterName,
        out string? error)
    {
        error = null;
        if (value is null)
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            error = $"{parameterName} must not be empty when supplied.";
            return false;
        }

        if (trimmed.Length > 128)
        {
            error = $"{parameterName} must be 128 characters or fewer.";
            return false;
        }

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            var safe = c is >= 'A' and <= 'Z'
                || c is >= 'a' and <= 'z'
                || c is >= '0' and <= '9'
                || c is '-' or '_' or '.' or ':' or '@';
            if (!safe)
            {
                error = $"{parameterName} contains unsupported characters.";
                return false;
            }
        }

        return true;
    }
}

internal sealed class CapabilityManifestEndpointLog;

internal static partial class CapabilityManifestEndpointLogging
{
    [LoggerMessage(
        EventId = 8723,
        Level = LogLevel.Error,
        Message = "Capability manifest endpoint failed.")]
    public static partial void ManifestFailed(ILogger logger, Exception exception);
}
