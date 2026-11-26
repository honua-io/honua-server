// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Exports;

// "Export this Layer as files": one call starts the export job; Versions, URLs and unpublish follow.
internal static class ExportAdminEndpoints
{
    public static void MapExportAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/exports")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Exports")
            .RequireAdminAuthorization();

        var list = AdminHttpOperationRouteBinding.ForOperate("admin.exports.list", "/exports");
        group.MapMethods(list.RoutePattern, list.Methods, ListAsync)
            .WithAdminOperation(list.Definition)
            .WithSummary("List Exports");
        var get = AdminHttpOperationRouteBinding.ForOperate("admin.exports.get", "/exports");
        group.MapMethods(get.RoutePattern, get.Methods, GetAsync)
            .WithAdminOperation(get.Definition)
            .WithSummary("Get an Export with its Versions and URLs");
        var create = AdminHttpOperationRouteBinding.ForOperate("admin.exports.create", "/exports");
        group.MapMethods(create.RoutePattern, create.Methods, CreateAsync)
            .WithAdminOperation(create.Definition)
            .WithSummary("Export a Layer as files");
        var delete = AdminHttpOperationRouteBinding.ForOperate("admin.exports.delete", "/exports");
        group.MapMethods(delete.RoutePattern, delete.Methods, DeleteAsync)
            .WithAdminOperation(delete.Definition)
            .WithSummary("Unpublish an Export");
    }

    private static async Task<IResult> ListAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var catalog = context.RequestServices.GetRequiredService<ExportCatalog>();
        var page = new ExportListPage(ParseIntQuery(context, "limit"), ParseIntQuery(context, "offset"));
        var read = await BuildReadOptionsAsync(context, cancellationToken).ConfigureAwait(false);
        var items = await catalog.ListAsync(read, page, cancellationToken).ConfigureAwait(false);
        return Json(ApiResponse<ExportListResponse>.CreateSuccess(new ExportListResponse(items)), StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetAsync(string exportId, HttpContext context, CancellationToken cancellationToken)
    {
        var catalog = context.RequestServices.GetRequiredService<ExportCatalog>();
        var read = await BuildReadOptionsAsync(context, cancellationToken).ConfigureAwait(false);
        var export = ExportRequestRules.IsExportId(exportId)
            ? await catalog.GetAsync(exportId, read, cancellationToken).ConfigureAwait(false)
            : null;
        return export is null
            ? Failure($"Export '{exportId}' was not found.", StatusCodes.Status404NotFound)
            : Json(ApiResponse<ExportResponse>.CreateSuccess(export), StatusCodes.Status200OK);
    }

    private static async Task<IResult> CreateAsync(ExportCreateRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        var snapshot = await services.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var tiles = services.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        var resolved = ExportCreateDefaults.Resolve(request, snapshot, new ExportZoomBounds(tiles.MinTileZoom, tiles.MaxTileZoom));
        if (resolved.Problem is { } problem)
        {
            return Failure(problem, resolved.StatusCode);
        }

        var accessError = await ExportAdminTenantAuthorization.AuthorizeCreateAsync(context, resolved.Source!, cancellationToken)
            .ConfigureAwait(false);
        if (accessError is not null)
        {
            return accessError;
        }

        var start = resolved.Start!;
        var catalog = services.GetRequiredService<ExportCatalog>();
        var isVisible = await ExportAdminTenantAuthorization.CreateVisibilityFilterAsync(context, cancellationToken)
            .ConfigureAwait(false);
        if (isVisible is not null &&
            await catalog.ExistsForAnotherTenantAsync(start.ExportId!, isVisible, cancellationToken).ConfigureAwait(false))
        {
            return Failure($"Export '{start.ExportId}' is not available.", StatusCodes.Status409Conflict);
        }

        if (await catalog.HasActiveJobAsync(start.ExportId!, cancellationToken).ConfigureAwait(false))
        {
            return Failure($"An export job is already running for Export '{start.ExportId}'.", StatusCodes.Status409Conflict);
        }

        return await SubmitCreateJobAsync(context, resolved, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApiResponse<ExportResponse>>> SubmitCreateJobAsync(
        HttpContext context, ExportCreatePlan resolved, CancellationToken cancellationToken)
    {
        var start = resolved.Start!;
        var submission = TileOperationSubmission.FromRequest(context, start);
        var jobId = await context.RequestServices.GetRequiredService<ITileCacheJobService>()
            .SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
        var export = new ExportResponse
        {
            Id = start.ExportId!,
            Title = resolved.Title ?? start.ExportId!,
            Status = ExportResponse.Running,
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = start.ServiceId, LayerId = start.LayerId, Name = resolved.Title },
            NotPublicReason = ExportCatalog.NotPublicReason,
            JobId = jobId
        };
        context.Response.Headers.Location = $"{context.Request.PathBase}/api/v1/admin/exports/{Uri.EscapeDataString(export.Id)}";
        return Json(ApiResponse<ExportResponse>.CreateSuccess(export, "Export started; a new Version appears when the job completes."),
            StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> DeleteAsync(string exportId, HttpContext context, CancellationToken cancellationToken)
    {
        var catalog = context.RequestServices.GetRequiredService<ExportCatalog>();
        var read = await BuildReadOptionsAsync(context, cancellationToken).ConfigureAwait(false);
        if (!ExportRequestRules.IsExportId(exportId)
            || await catalog.GetAsync(exportId, read, cancellationToken).ConfigureAwait(false) is null)
        {
            return Failure($"Export '{exportId}' was not found.", StatusCodes.Status404NotFound);
        }

        if (await catalog.HasActiveJobAsync(exportId, cancellationToken).ConfigureAwait(false))
        {
            return Failure($"An export job is running for Export '{exportId}'; wait for it to finish before unpublishing.",
                StatusCodes.Status409Conflict);
        }

        await catalog.UnpublishAsync(exportId, cancellationToken).ConfigureAwait(false);
        var export = await catalog.GetAsync(exportId, read, cancellationToken).ConfigureAwait(false);
        return Json(ApiResponse<ExportResponse>.CreateSuccess(export!, "Export unpublished; its files are no longer served."), StatusCodes.Status200OK);
    }

    private static async Task<ExportCatalogReadOptions> BuildReadOptionsAsync(
        HttpContext context, CancellationToken cancellationToken)
        => new(BaseUrl(context),
            await ExportAdminTenantAuthorization.CreateVisibilityFilterAsync(context, cancellationToken).ConfigureAwait(false));

    private static int? ParseIntQuery(HttpContext context, string name)
        => context.Request.Query.TryGetValue(name, out var raw)
            && int.TryParse(raw.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    private static string BaseUrl(HttpContext context) => BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');

    internal static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApiResponse<object>> CreateNotFoundFailure(string message)
        => Failure(message, StatusCodes.Status404NotFound);

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApiResponse<object>> Failure(string message, int statusCode)
        => Json(ApiResponse<object>.Failure(message), statusCode);

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApiResponse<T>> Json<T>(ApiResponse<T> response, int statusCode)
        => TypedResults.Json(response, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>>)
            ExportAdminJsonContext.Default.GetTypeInfo(typeof(ApiResponse<T>))!, statusCode: statusCode);
}
