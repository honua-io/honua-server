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

    private static async Task<(RasterRenderCapacityLimiter.Lease? Lease, IResult? Error)> TryAcquireCoverageRenderLeaseAsync(
        HttpContext context,
        (RasterQuery Query, RasterInfo Raster) target,
        CancellationToken cancellationToken)
    {
        var limiter = context.RequestServices.GetService<RasterRenderCapacityLimiter>();
        if (limiter is null)
        {
            return (null, null);
        }

        var renderWidth = Math.Max(1, target.Query.OutputWidth ?? target.Raster.Width);
        var renderHeight = Math.Max(1, target.Query.OutputHeight ?? target.Raster.Height);
        var renderLease = await limiter.TryAcquireAsync(renderWidth, renderHeight, cancellationToken).ConfigureAwait(false);
        return renderLease is null
            ? (null, StandardErrorHelpers.CreateServiceUnavailable(
                context,
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds))
            : (renderLease, null);
    }
}
