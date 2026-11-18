// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Admin.TileOperations;
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

        _ = group.MapGet("", ListAsync).WithName("ListExports").WithSummary("List Exports");
        _ = group.MapGet("/{exportId}", GetAsync).WithName("GetExport").WithSummary("Get an Export with its Versions and URLs");
        _ = group.MapPost("", CreateAsync).WithName("CreateExport").WithSummary("Export a Layer as files");
        _ = group.MapDelete("/{exportId}", DeleteAsync).WithName("DeleteExport").WithSummary("Unpublish an Export");
    }

    private static async Task<IResult> ListAsync(HttpContext context, [FromServices] ExportCatalog catalog, CancellationToken cancellationToken)
    {
        var items = await catalog.ListAsync(BaseUrl(context), cancellationToken).ConfigureAwait(false);
        return Json(ApiResponse<ExportListResponse>.CreateSuccess(new ExportListResponse(items)), StatusCodes.Status200OK);
    }

    private static async Task<IResult> GetAsync(
        string exportId, HttpContext context, [FromServices] ExportCatalog catalog, CancellationToken cancellationToken)
    {
        var export = ExportRequestRules.IsExportId(exportId)
            ? await catalog.GetAsync(exportId, BaseUrl(context), cancellationToken).ConfigureAwait(false)
            : null;
        return export is null
            ? Failure($"Export '{exportId}' was not found.", StatusCodes.Status404NotFound)
            : Json(ApiResponse<ExportResponse>.CreateSuccess(export), StatusCodes.Status200OK);
    }

    private static async Task<IResult> CreateAsync(
        ExportCreateRequest request,
        HttpContext context,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] ITileCacheJobService jobService,
        [FromServices] IOptions<LimitsOptions> limits,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var tiles = limits.Value.Tiles;
        var resolved = ExportCreateDefaults.Resolve(request, snapshot, tiles.MinTileZoom, tiles.MaxTileZoom);
        if (resolved.Problem is { } problem)
        {
            return Failure(problem, resolved.StatusCode);
        }

        var start = resolved.Start!;
        var submission = new TileOperationSubmission(
            start, JobOwnershipSecurity.ResolveOwner(context.User), context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema);
        var jobId = await jobService.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
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

    private static async Task<IResult> DeleteAsync(
        string exportId, HttpContext context, [FromServices] ExportCatalog catalog, CancellationToken cancellationToken)
    {
        if (!ExportRequestRules.IsExportId(exportId)
            || await catalog.GetAsync(exportId, BaseUrl(context), cancellationToken).ConfigureAwait(false) is null)
        {
            return Failure($"Export '{exportId}' was not found.", StatusCodes.Status404NotFound);
        }

        await catalog.UnpublishAsync(exportId, cancellationToken).ConfigureAwait(false);
        var export = await catalog.GetAsync(exportId, BaseUrl(context), cancellationToken).ConfigureAwait(false);
        return Json(ApiResponse<ExportResponse>.CreateSuccess(export!, "Export unpublished; its files are no longer served."), StatusCodes.Status200OK);
    }

    private static string BaseUrl(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

    private static IResult Failure(string message, int statusCode)
        => Json(ApiResponse<object>.Failure(message), statusCode);

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApiResponse<T>> Json<T>(ApiResponse<T> response, int statusCode)
        => TypedResults.Json(response, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>>)
            ExportAdminJsonContext.Default.GetTypeInfo(typeof(ApiResponse<T>))!, statusCode: statusCode);
}
