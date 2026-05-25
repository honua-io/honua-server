// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.OpenData;
using Honua.Server.Features.OpenData.Models;
using Honua.Server.Features.OpenData.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.OpenData;

/// <summary>
/// Public anonymous open-data publication endpoints.
/// </summary>
internal static class OpenDataEndpoints
{
    /// <summary>
    /// Maps public open-data endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapOpenDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/open-data/catalog.json", HandleGetDcatCatalog)
            .AllowAnonymous()
            .WithDisplayName("Open Data DCAT Catalog")
            .WithName("OpenDataDcatCatalog")
            .WithSummary("Returns the public DCAT/data.json catalog.")
            .WithTags("Open Data")
            .CacheOutput("OpenDataCatalog")
            .Produces<DcatCatalogResponse>(StatusCodes.Status200OK, "application/json");

        endpoints.MapGet("/open-data", HandleList)
            .AllowAnonymous()
            .WithDisplayName("List Open Data Items")
            .WithName("OpenDataList")
            .WithSummary("Lists anonymous-readable open-data items.")
            .WithTags("Open Data")
            .CacheOutput("OpenDataList")
            .Produces<OpenDataListResponse>(StatusCodes.Status200OK, "application/json");

        endpoints.MapGet("/open-data/{itemId}", HandleGet)
            .AllowAnonymous()
            .WithDisplayName("Get Open Data Item")
            .WithName("OpenDataItem")
            .WithSummary("Returns an anonymous-readable open-data page projection.")
            .WithTags("Open Data")
            .CacheOutput("OpenDataItem")
            .Produces<OpenDataItemResponse>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleList(
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 25)
    {
        using var activity = OpenDataTelemetry.StartActivity("page.list", "/open-data", HttpMethods.Get);
        try
        {
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var response = await service.ListPublicItemsAsync(baseUrl, cursor, limit, context.RequestAborted)
                .ConfigureAwait(false);
            OpenDataTelemetry.SetResult(activity, response.Items.Count);
            return Results.Json(response, OpenDataJsonContext.Default.OpenDataListResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "public.list", ex);
            return Results.Problem(
                title: "Open-data listing failed",
                detail: "An internal error occurred while listing open-data items.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleGet(
        string itemId,
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity("page.read", "/open-data/{itemId}", HttpMethods.Get, itemId);
        try
        {
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var response = await service.GetPublicItemAsync(itemId, baseUrl, context.RequestAborted)
                .ConfigureAwait(false);
            if (response is null)
            {
                OpenDataTelemetry.SetFailed(activity, "not_found_or_denied");
                return Results.NotFound();
            }

            OpenDataTelemetry.SetResult(activity, 1);
            return Results.Json(response, OpenDataJsonContext.Default.OpenDataItemResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "public.get", ex);
            return Results.Problem(
                title: "Open-data item retrieval failed",
                detail: "An internal error occurred while retrieving the open-data item.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleGetDcatCatalog(
        HttpContext context,
        [FromServices] OpenDataPublicationService service,
        [FromServices] ILogger<OpenDataPublicationService> logger)
    {
        using var activity = OpenDataTelemetry.StartActivity("dcat.export", "/open-data/catalog.json", HttpMethods.Get);
        try
        {
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var response = await service.BuildDcatCatalogAsync(baseUrl, context.RequestAborted)
                .ConfigureAwait(false);
            OpenDataTelemetry.SetResult(activity, response.Dataset.Count);
            return Results.Json(response, OpenDataJsonContext.Default.DcatCatalogResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OpenDataTelemetry.RecordException(activity, ex);
            OpenDataLog.EndpointFailed(logger, "public.dcat", ex);
            return Results.Problem(
                title: "Open-data catalog export failed",
                detail: "An internal error occurred while generating the open-data catalog.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
