// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

internal sealed partial class ImageServerExportHandler
{
    private sealed class ExportResponseContext
    {
        public required int LayerId { get; init; }
        public required int StorageLayerId { get; init; }
        public required ExportImageRequest Request { get; init; }
        public required RasterResult Result { get; init; }
        public RasterExtent? AggregateExtent { get; init; }
        public required RasterInfo[] SelectedRasters { get; init; }
        public required HonuaTelemetryScope Scope { get; init; }
    }

    private static async Task<(RasterRenderCapacityLimiter.Lease? Lease, IResult? Refusal)> TryAcquireExportRenderLeaseAsync(
        HttpContext context,
        RasterQuery exportQuery,
        CancellationToken cancellationToken)
    {
        var limiter = context.RequestServices.GetService<RasterRenderCapacityLimiter>();
        if (limiter is null)
        {
            return (null, null);
        }

        var lease = await limiter.TryAcquireAsync(
            Math.Max(1, exportQuery.OutputWidth ?? DefaultOutputDimension),
            Math.Max(1, exportQuery.OutputHeight ?? DefaultOutputDimension),
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return (null, StandardErrorHelpers.CreateServiceUnavailable(
                context,
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds));
        }

        return (lease, null);
    }

    private async Task<IResult> CreateExportResponseAsync(
        HttpContext context,
        ExportResponseContext responseContext,
        CancellationToken cancellationToken)
    {
        var result = responseContext.Result;
        if (WantsInlineImageResponse(responseContext.Request.F))
        {
            ImageServerLog.ExportImageCompleted(_logger, responseContext.LayerId, result.Data.Length);
            responseContext.Scope.SetSuccess(1);
            return EncodedRasterResults.File(result.Data, result.ContentType);
        }

        var imageUrl = await _temporaryFileService.StoreTemporaryFileAsync(
            result.Data, result.ContentType, TimeSpan.FromHours(1), principal: context.User, cancellationToken: cancellationToken);
        var extent = result.Extent ?? responseContext.AggregateExtent;
        if (extent == null && responseContext.SelectedRasters.Length == 1)
        {
            extent = await _exportBackend.GetExtentAsync(responseContext.StorageLayerId, responseContext.SelectedRasters[0].Id, cancellationToken);
        }

        var exportResponse = new ExportImageResponse
        {
            Href = imageUrl,
            Width = result.Width,
            Height = result.Height,
            Extent = BuildExtent(extent, responseContext.Request.Bbox, bboxSrid: SpatialReferenceHelpers.TryParseSrid(responseContext.Request.BboxSr), result.Srid),
        };
        ImageServerLog.ExportImageCompleted(_logger, responseContext.LayerId, result.Data.Length);
        responseContext.Scope.SetSuccess(1);
        return Results.Json(exportResponse, ImageServerJsonContext.Default.ExportImageResponse);
    }
}
