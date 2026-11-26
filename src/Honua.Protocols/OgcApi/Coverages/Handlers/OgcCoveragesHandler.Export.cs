// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.Ogc.Api.Coverages;
using Honua.Protocols.Ogc.Api.Coverages.Models;
using Honua.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.IO;

namespace Honua.Protocols.Ogc.Api.Coverages.Handlers;

internal sealed partial class OgcCoveragesHandler
{
    private sealed class CoverageScalingInput
    {
        public required string CollectionId { get; init; }
        public required RasterInfo Raster { get; init; }
        public required IReadOnlyDictionary<string, CrsDefinition> SupportedCrs { get; init; }
        public required int StorageSrid { get; init; }
        public required RasterQuery Query { get; init; }
    }

    private async Task<IResult> ServeZarrCoverageAsync(
        HttpContext context,
        (string CollectionId, ZarrRegistration Registration, string? Format, HonuaTelemetryScope Telemetry) target,
        CancellationToken cancellationToken)
    {
        var zarrResult = await _zarrCoverages.GetCoverageAsync(
                context,
                target.CollectionId,
                target.Registration,
                target.Format,
                cancellationToken)
            .ConfigureAwait(false);
        target.Telemetry.SetSuccess(1);
        return zarrResult;
    }

    private async Task<(RasterQuery Query, IResult? Error)> ApplyCoverageScalingStepAsync(
        HttpContext context,
        CoverageScalingInput input,
        CancellationToken cancellationToken)
    {
        var scalingResult = await TryApplyScalingAsync(
                context,
                input.Raster,
                input.SupportedCrs,
                input.StorageSrid,
                input.Query,
                cancellationToken)
            .ConfigureAwait(false);
        if (!scalingResult.Success)
        {
            OgcCoveragesLog.ValidationFailed(_logger, input.CollectionId, scalingResult.Error);
            return (input.Query, StandardErrorHelpers.CreateBadRequest(context, scalingResult.Error));
        }

        return (scalingResult.Query, null);
    }

    private async Task<(RasterRenderCapacityLimiter.Lease? Lease, IResult? Error)> TryAcquireCoverageRenderLeaseAsync(
        HttpContext context,
        (RasterQuery Query, RasterInfo Raster, int StorageSrid) target,
        CancellationToken cancellationToken)
    {
        var limiter = context.RequestServices.GetService<RasterRenderCapacityLimiter>();
        if (limiter is null)
        {
            return (null, null);
        }

        var (renderWidth, renderHeight) = await ResolveCoverageRenderDimensionsAsync(target, cancellationToken)
            .ConfigureAwait(false);
        var renderLease = await limiter.TryAcquireAsync(renderWidth, renderHeight, cancellationToken).ConfigureAwait(false);
        return renderLease is null
            ? (null, StandardErrorHelpers.CreateServiceUnavailable(
                context,
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds))
            : (renderLease, null);
    }

    private async ValueTask<(int Width, int Height)> ResolveCoverageRenderDimensionsAsync(
        (RasterQuery Query, RasterInfo Raster, int StorageSrid) target,
        CancellationToken cancellationToken)
    {
        var (query, raster, storageSrid) = target;
        if (query.OutputWidth is int explicitWidth && query.OutputHeight is int explicitHeight)
        {
            return (Math.Max(1, explicitWidth), Math.Max(1, explicitHeight));
        }

        if (query.ClipRegion is { } clip)
        {
            var clipped = await EstimateClippedRenderDimensionsAsync((raster, storageSrid, clip), cancellationToken)
                .ConfigureAwait(false);
            if (clipped.HasValue)
            {
                return ApplyPixelSizeCap(clipped.Value, query.PixelSize, raster);
            }
        }

        return ApplyPixelSizeCap((Math.Max(1, raster.Width), Math.Max(1, raster.Height)), query.PixelSize, raster);
    }

    private async ValueTask<(int Width, int Height)?> EstimateClippedRenderDimensionsAsync(
        (RasterInfo Raster, int StorageSrid, RasterClipRegion Clip) input,
        CancellationToken cancellationToken)
    {
        var (raster, storageSrid, clip) = input;
        if (raster.Extent is not { } extent || extent.Srid.GetValueOrDefault(storageSrid) != storageSrid ||
            raster.Width <= 0 || raster.Height <= 0)
        {
            return null;
        }

        var nativeWidth = extent.XMax - extent.XMin;
        var nativeHeight = extent.YMax - extent.YMin;
        if (!IsFinitePositive(nativeWidth) || !IsFinitePositive(nativeHeight))
        {
            return null;
        }

        var envelope = new WKBReader().Read(clip.Geometry).EnvelopeInternal;
        var clipSrid = clip.Srid ?? storageSrid;
        var bounds = clipSrid == storageSrid
            ? (MinX: envelope.MinX, MinY: envelope.MinY, MaxX: envelope.MaxX, MaxY: envelope.MaxY)
            : await _coordinateTransformService.TransformExtentAsync(
                envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY,
                clipSrid, storageSrid, cancellationToken).ConfigureAwait(false);
        if (!bounds.HasValue)
        {
            return null;
        }

        var width = Math.Ceiling((Math.Clamp(bounds.Value.MaxX, extent.XMin, extent.XMax) - extent.XMin) / nativeWidth * raster.Width) -
                    Math.Floor((Math.Clamp(bounds.Value.MinX, extent.XMin, extent.XMax) - extent.XMin) / nativeWidth * raster.Width);
        var height = Math.Ceiling((Math.Clamp(bounds.Value.MaxY, extent.YMin, extent.YMax) - extent.YMin) / nativeHeight * raster.Height) -
                     Math.Floor((Math.Clamp(bounds.Value.MinY, extent.YMin, extent.YMax) - extent.YMin) / nativeHeight * raster.Height);
        return double.IsFinite(width) && double.IsFinite(height)
            ? (Math.Max(1, (int)Math.Min(width, MaxScaleSize)), Math.Max(1, (int)Math.Min(height, MaxScaleSize)))
            : null;
    }

    private static (int Width, int Height) ApplyPixelSizeCap(
        (int Width, int Height) nativePixels,
        PixelSize? requestedPixelSize,
        RasterInfo raster)
    {
        if (requestedPixelSize is not { } requested ||
            ResolveNativePixelSize(raster) is not { } native ||
            !IsFinitePositive(requested.Width) || !IsFinitePositive(requested.Height))
        {
            return nativePixels;
        }

        var width = Math.Clamp(Math.Ceiling(nativePixels.Width * (native.Width / requested.Width)), 1.0, MaxScaleSize);
        var height = Math.Clamp(Math.Ceiling(nativePixels.Height * (native.Height / requested.Height)), 1.0, MaxScaleSize);
        return ((int)width, (int)height);
    }
}
