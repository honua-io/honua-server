// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for external service discovery.
/// </summary>
internal static class ExternalServiceDiscoveryEndpoints
{
    public static void MapExternalServiceDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/external-services")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "External Services")
            .RequireAdminAuthorization();

        _ = group.MapMethods("/discover", [HttpMethods.Post], HandleDiscover)
            .WithDisplayName("Discover External Service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task HandleDiscover(HttpContext context)
    {
        ExternalServiceDiscoveryRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                    ExternalServiceDiscoveryJsonContext.Default.ExternalServiceDiscoveryRequest,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid request body")
                .ExecuteAsync(context);
            return;
        }

        if (request is null)
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Request body is required")
                .ExecuteAsync(context);
            return;
        }

        try
        {
            var service = context.RequestServices.GetRequiredService<IExternalServiceDiscoveryService>();
            var response = await service.DiscoverAsync(request, context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    response,
                    ExternalServiceDiscoveryJsonContext.Default.ExternalServiceDiscoveryResponse,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (ExternalServiceDiscoveryRequestException ex)
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    ex.Message)
                .ExecuteAsync(context);
        }
        catch (HttpRequestException)
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status502BadGateway,
                    "External service discovery failed",
                    "The external service could not be reached or returned an unsupported response.")
                .ExecuteAsync(context);
        }
        catch (ExternalServiceDiscoveryRemoteException ex)
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status502BadGateway,
                    "External service discovery failed",
                    ex.Message)
                .ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status502BadGateway,
                    "External service discovery timed out")
                .ExecuteAsync(context);
        }
    }
}
