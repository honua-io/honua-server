// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.OpenData.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OpenData;
using Honua.Server.Features.OpenData.Models;
using Honua.Server.Features.OpenData.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for Console-managed STAC publication controls.
/// </summary>
internal static class StacPublicationAdminEndpoints
{
    /// <summary>
    /// Maps STAC publication admin endpoints.
    /// </summary>
    public static void MapStacPublicationAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/stac/publications")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin - STAC Publication")
            .RequireAdminAuthorization();

        group.MapPost("", HandlePublish)
            .WithDisplayName("Publish STAC Collection")
            .WithSummary("Publishes an eligible Console content item into the STAC publication control surface.");

        group.MapPut("/{collectionId}", HandleUpdate)
            .WithDisplayName("Update STAC Publication")
            .WithSummary("Updates STAC publication metadata for a collection.");

        group.MapDelete("/{collectionId}", HandleUnpublish)
            .WithDisplayName("Unpublish STAC Collection")
            .WithSummary("Unpublishes a Console-managed STAC collection without deleting the source item.");

        group.MapGet("/{collectionId}", HandleStatus)
            .WithDisplayName("Get STAC Publication Status")
            .WithSummary("Returns STAC publication status and readback metadata.");
    }

    private static async Task<IResult> HandlePublish(
        StacPublicationPublishRequest request,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] OutputCacheInvalidationService cacheInvalidation,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "stac.publication.publish",
            "/api/v1/admin/stac/publications",
            HttpMethods.Post,
            request.ItemId);
        try
        {
            var result = await service.PublishStacAsync(request, BaseUrlResolver.GetBaseUrl(context), context.RequestAborted)
                .ConfigureAwait(false);
            return await MapStacMutationResultAsync(
                result,
                context,
                cacheInvalidation,
                logger,
                created: true,
                activity).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.stac.publish", ex);
            return Results.Problem(
                title: "STAC publication failed",
                detail: "An internal error occurred while publishing the STAC collection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleUpdate(
        string collectionId,
        StacPublicationUpdateRequest request,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] OutputCacheInvalidationService cacheInvalidation,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "stac.publication.update",
            "/api/v1/admin/stac/publications/{collectionId}",
            HttpMethods.Put,
            collectionId);
        try
        {
            var result = await service.UpdateStacAsync(collectionId, request, context.RequestAborted)
                .ConfigureAwait(false);
            return await MapStacMutationResultAsync(
                result,
                context,
                cacheInvalidation,
                logger,
                created: false,
                activity).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.stac.update", ex);
            return Results.Problem(
                title: "STAC publication update failed",
                detail: "An internal error occurred while updating the STAC publication.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleUnpublish(
        string collectionId,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] OutputCacheInvalidationService cacheInvalidation,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "stac.publication.unpublish",
            "/api/v1/admin/stac/publications/{collectionId}",
            HttpMethods.Delete,
            collectionId);
        try
        {
            var result = await service.UnpublishStacAsync(collectionId, context.RequestAborted).ConfigureAwait(false);
            if (result.Status == StacPublicationOperationStatus.NotFound)
            {
                OpenDataTelemetry.SetFailed(activity, "not_found");
                return Results.NotFound(ApiResponse<object>.Failure("STAC publication not found."));
            }

            await OpenDataAdminEndpoints.InvalidateOpenDataAsync(
                cacheInvalidation,
                result.Record?.ItemId,
                logger,
                context.RequestAborted).ConfigureAwait(false);
            OpenDataTelemetry.SetResult(activity, 1);
            return Results.NoContent();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.stac.unpublish", ex);
            return Results.Problem(
                title: "STAC unpublish failed",
                detail: "An internal error occurred while unpublishing the STAC collection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleStatus(
        string collectionId,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "stac.publication.status",
            "/api/v1/admin/stac/publications/{collectionId}",
            HttpMethods.Get,
            collectionId);
        try
        {
            var response = await service.GetStacStatusAsync(collectionId, context.RequestAborted).ConfigureAwait(false);
            if (response is null)
            {
                OpenDataTelemetry.SetFailed(activity, "not_found");
                return Results.NotFound(ApiResponse<object>.Failure("STAC publication not found."));
            }

            OpenDataTelemetry.SetResult(activity, 1);
            return Results.Json(
                ApiResponse<StacPublicationStatusResponse>.CreateSuccess(response),
                OpenDataJsonContext.Default.ApiResponseStacPublicationStatusResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.stac.status", ex);
            return Results.Problem(
                title: "STAC publication status failed",
                detail: "An internal error occurred while retrieving STAC publication status.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> MapStacMutationResultAsync(
        StacPublicationOperationResult result,
        HttpContext context,
        OutputCacheInvalidationService cacheInvalidation,
        ILogger logger,
        bool created,
        System.Diagnostics.Activity? activity)
    {
        switch (result.Status)
        {
            case StacPublicationOperationStatus.NotFound:
                OpenDataTelemetry.SetFailed(activity, "not_found");
                return Results.NotFound(ApiResponse<object>.Failure("STAC publication source not found."));
            case StacPublicationOperationStatus.Ineligible:
                OpenDataTelemetry.SetFailed(activity, "ineligible");
                return Results.BadRequest(ApiResponse<StacPublicationStatusResponse>.Failure(
                    "Open-data eligibility blocked STAC publication.",
                    result.Eligibility is null
                        ? null
                        : new StacPublicationStatusResponse
                        {
                            CollectionId = string.Empty,
                            ItemId = result.Eligibility.ItemId,
                            Status = OpenDataStacPublicationStatus.Unpublished,
                            PublicStacCollectionUrl = string.Empty,
                            Eligibility = result.Eligibility,
                            UpdatedAt = DateTimeOffset.UtcNow
                        }));
            case StacPublicationOperationStatus.Conflict:
                OpenDataTelemetry.SetFailed(activity, "conflict");
                return Results.Conflict(ApiResponse<object>.Failure("STAC publication already exists."));
        }

        var response = OpenDataPublicationService.MapStacStatus(result.Record!, result.Eligibility);
        await OpenDataAdminEndpoints.InvalidateOpenDataAsync(
            cacheInvalidation,
            response.ItemId,
            logger,
            context.RequestAborted).ConfigureAwait(false);
        OpenDataTelemetry.SetResult(activity, 1);

        var envelope = ApiResponse<StacPublicationStatusResponse>.CreateSuccess(response);
        return created
            ? Results.Created(
                response.PublicStacCollectionUrl,
                envelope)
            : Results.Json(envelope, OpenDataJsonContext.Default.ApiResponseStacPublicationStatusResponse);
    }
}
