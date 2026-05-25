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
/// Admin endpoints for open-data page state and DCAT publication validation.
/// </summary>
internal static class OpenDataAdminEndpoints
{
    /// <summary>
    /// Maps admin open-data endpoints.
    /// </summary>
    public static void MapOpenDataAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/open-data")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin - Open Data")
            .RequireAdminAuthorization();

        group.MapGet("/{itemId}/eligibility", HandleEligibility)
            .WithDisplayName("Get Open Data Eligibility")
            .WithSummary("Evaluates whether a Console content item can be published as open data.");

        group.MapGet("/{itemId}", HandleGetPage)
            .WithDisplayName("Get Open Data Page")
            .WithSummary("Returns server-owned open-data page metadata for a Console content item.");

        group.MapPut("/{itemId}", HandleUpdatePage)
            .WithDisplayName("Update Open Data Page")
            .WithSummary("Updates server-owned open-data page metadata for a Console content item.");

        group.MapGet("/dcat/status", HandleDcatStatus)
            .WithDisplayName("Get Open Data DCAT Status")
            .WithSummary("Returns DCAT/data.json validation and publication status.");

        group.MapPost("/dcat/validate", HandleDcatValidate)
            .WithDisplayName("Validate Open Data DCAT")
            .WithSummary("Validates DCAT/data.json output for one item or the current public catalog.");
    }

    private static async Task<IResult> HandleEligibility(
        string itemId,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "eligibility.check",
            "/api/v1/admin/open-data/{itemId}/eligibility",
            HttpMethods.Get,
            itemId);
        try
        {
            var eligibility = await service.EvaluateEligibilityAsync(itemId, context.RequestAborted)
                .ConfigureAwait(false);
            OpenDataTelemetry.SetResult(activity, eligibility.Reasons.Count);
            return Results.Json(
                ApiResponse<OpenDataEligibility>.CreateSuccess(eligibility),
                OpenDataJsonContext.Default.ApiResponseOpenDataEligibility);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.eligibility", ex);
            return Results.Problem(
                title: "Open-data eligibility check failed",
                detail: "An internal error occurred while evaluating open-data eligibility.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleGetPage(
        string itemId,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "page.admin.read",
            "/api/v1/admin/open-data/{itemId}",
            HttpMethods.Get,
            itemId);
        try
        {
            var response = await service.GetAdminPageAsync(itemId, context.RequestAborted).ConfigureAwait(false);
            if (response is null)
            {
                OpenDataTelemetry.SetFailed(activity, "not_found");
                return Results.NotFound(ApiResponse<object>.Failure("Open-data item not found."));
            }

            OpenDataTelemetry.SetResult(activity, 1);
            return Results.Json(
                ApiResponse<OpenDataPageAdminResponse>.CreateSuccess(response),
                OpenDataJsonContext.Default.ApiResponseOpenDataPageAdminResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.page.get", ex);
            return Results.Problem(
                title: "Open-data page retrieval failed",
                detail: "An internal error occurred while retrieving open-data page metadata.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleUpdatePage(
        string itemId,
        OpenDataPageUpdateRequest request,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] OutputCacheInvalidationService cacheInvalidation,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "page.write",
            "/api/v1/admin/open-data/{itemId}",
            HttpMethods.Put,
            itemId);
        try
        {
            var response = await service.UpdatePageAsync(itemId, request, context.User, context.RequestAborted)
                .ConfigureAwait(false);
            if (response is null)
            {
                OpenDataTelemetry.SetFailed(activity, "not_found");
                return Results.NotFound(ApiResponse<object>.Failure("Open-data item not found."));
            }

            await InvalidateOpenDataAsync(cacheInvalidation, itemId, logger, context.RequestAborted).ConfigureAwait(false);
            OpenDataTelemetry.SetResult(activity, 1);
            return Results.Json(
                ApiResponse<OpenDataPageAdminResponse>.CreateSuccess(response),
                OpenDataJsonContext.Default.ApiResponseOpenDataPageAdminResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.page.update", ex);
            return Results.Problem(
                title: "Open-data page update failed",
                detail: "An internal error occurred while updating open-data page metadata.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleDcatStatus(
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "dcat.status",
            "/api/v1/admin/open-data/dcat/status",
            HttpMethods.Get);
        try
        {
            var validation = await service.ValidateDcatAsync(itemId: null, context.RequestAborted).ConfigureAwait(false);
            var response = new OpenDataDcatStatusResponse
            {
                CatalogUrl = $"{BaseUrlResolver.GetBaseUrl(context)}/open-data/catalog.json",
                Validation = validation,
                GeneratedAt = DateTimeOffset.UtcNow
            };
            OpenDataTelemetry.SetResult(activity, validation.ItemCount);
            return Results.Json(
                ApiResponse<OpenDataDcatStatusResponse>.CreateSuccess(response),
                OpenDataJsonContext.Default.ApiResponseOpenDataDcatStatusResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.dcat.status", ex);
            return Results.Problem(
                title: "Open-data DCAT status failed",
                detail: "An internal error occurred while retrieving DCAT status.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleDcatValidate(
        OpenDataDcatValidateRequest request,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity(
            "dcat.validate",
            "/api/v1/admin/open-data/dcat/validate",
            HttpMethods.Post,
            request.ItemId);
        try
        {
            var validation = await service.ValidateDcatAsync(request.ItemId, context.RequestAborted).ConfigureAwait(false);
            OpenDataTelemetry.SetResult(activity, validation.ItemCount);
            return Results.Json(
                ApiResponse<OpenDataValidationSummary>.CreateSuccess(validation),
                OpenDataJsonContext.Default.ApiResponseOpenDataValidationSummary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "admin.dcat.validate", ex);
            return Results.Problem(
                title: "Open-data DCAT validation failed",
                detail: "An internal error occurred while validating DCAT output.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    internal static async Task InvalidateOpenDataAsync(
        OutputCacheInvalidationService cacheInvalidation,
        string? itemId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await cacheInvalidation.InvalidateOpenDataAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OpenDataLog.CacheInvalidationFailed(logger, itemId, ex);
        }
    }
}
