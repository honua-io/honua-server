// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
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

    private async Task<(RasterQuery Query, (int Width, int Height) RenderSize, IResult? Error)> ApplyCoverageScalingStepAsync(
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
            return (input.Query, default, StandardErrorHelpers.CreateBadRequest(context, scalingResult.Error));
        }

        return (scalingResult.Query, scalingResult.RenderSize, null);
    }

    private static async Task<(RasterRenderCapacityLimiter.Lease? Lease, IResult? Error)> TryAcquireCoverageRenderLeaseAsync(
        HttpContext context,
        (int Width, int Height) renderSize,
        CancellationToken cancellationToken)
    {
        var limiter = context.RequestServices.GetService<RasterRenderCapacityLimiter>();
        if (limiter is null)
        {
            return (null, null);
        }

        var renderLease = await limiter.TryAcquireAsync(
                Math.Max(1, renderSize.Width),
                Math.Max(1, renderSize.Height),
                cancellationToken)
            .ConfigureAwait(false);
        return renderLease is null
            ? (null, StandardErrorHelpers.CreateServiceUnavailable(
                context,
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds))
            : (renderLease, null);
    }

    private async ValueTask<((int Width, int Height) RenderSize, string? Error)> ValidateUnscaledCoverageSizeAsync(
        (RasterInfo Raster, int StorageSrid, RasterClipRegion? Clip) input,
        CancellationToken cancellationToken)
    {
        var (raster, storageSrid, clip) = input;
        if (clip is { } nativeClip)
        {
            var estimated = await TryEstimateNativeSubsetGridAsync((raster, storageSrid, nativeClip), cancellationToken)
                .ConfigureAwait(false);
            if ((raster.Width > MaxScaleSize || raster.Height > MaxScaleSize) &&
                (!estimated.HasValue || estimated.Value.Width > MaxScaleSize || estimated.Value.Height > MaxScaleSize))
            {
                return (default, $"Native subset must not exceed {MaxScaleSize.ToString(CultureInfo.InvariantCulture)} pixels on either axis. Select a smaller subset or use scale-size to bound the output.");
            }

            var renderSize = estimated.HasValue
                ? ClampRenderDimensions(estimated.Value)
                : (Math.Max(1, raster.Width), Math.Max(1, raster.Height));
            return (renderSize, null);
        }

        if (raster.Width > 0 &&
            raster.Height > 0 &&
            (raster.Width > MaxScaleSize || raster.Height > MaxScaleSize))
        {
            return (default, $"Coverage native size exceeds {MaxScaleSize.ToString(CultureInfo.InvariantCulture)} pixels on an axis. Use subset or bbox to select a smaller area, or resolution, scale-factor, or scale-size to downsample the output.");
        }

        return ((Math.Max(1, raster.Width), Math.Max(1, raster.Height)), null);
    }

    private async ValueTask<(double Width, double Height)?> TryEstimateNativeSubsetGridAsync(
        (RasterInfo Raster, int StorageSrid, RasterClipRegion Clip) input,
        CancellationToken cancellationToken)
    {
        var (raster, storageSrid, clip) = input;
        if (raster.Extent is not { } extent || extent.Srid.GetValueOrDefault(storageSrid) != storageSrid ||
            raster.Width <= 0 || raster.Height <= 0 ||
            // codeql[cs/equality-on-floats]: exact zero shear means an axis-aligned grid; a tolerance would serve a rotated grid as north-up
            (raster.GeoTransform is { Length: >= 6 } transform && (transform[2] != 0 || transform[4] != 0)))
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
        return double.IsFinite(width) && double.IsFinite(height) ? (width, height) : null;
    }

    private static (int Width, int Height) ClampRenderDimensions((double Width, double Height) pixels)
        => ((int)Math.Clamp(pixels.Width, 1.0, MaxScaleSize), (int)Math.Clamp(pixels.Height, 1.0, MaxScaleSize));
}
