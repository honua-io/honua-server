// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Capacity;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Protocols.Zarr;

/// <summary>
/// Coordinates pure planning, capacity admission, object-reader resolution, and
/// managed rendering for one public Zarr datacube tile request.
/// </summary>
internal sealed class ZarrDatacubeTileService(
    IZarrStore store,
    IZarrSubsetReader subsetReader,
    IEnumerable<ICloudRangeReader> rangeReaders,
    ITileMatrixSetRegistry tileMatrixSets,
    IRasterCapacityAdmission capacityAdmission,
    ITenantContext tenantContext,
    ILogger<ZarrEndpointsLog> logger)
{
    /// <summary>
    /// Decoded payload cap for a single tile slice. A 256x256 float64 grid plus a
    /// little headroom; the planner rejects anything larger before any read happens.
    /// </summary>
    private const long MaxTileSliceBytes = 4L * 1024L * 1024L;

    /// <summary>
    /// Handles one public datacube tile request while preserving fail-closed
    /// admission before reader resolution, object I/O, or raster-sized allocation.
    /// </summary>
    internal async Task<IResult> HandleAsync(
        HttpContext context,
        int layerId,
        string tileMatrixSetId,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        if (x < 0 || y < 0 || z < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Tile coordinates must be non-negative.");
        }

        var registrations = await store.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var servable = registrations.FirstOrDefault(candidate => candidate.Metadata is not null);

        if (servable is null || servable.Metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "No servable Zarr coverage is registered for this layer.");
        }

        var metadata = servable.Metadata;

        if (!tileMatrixSets.TryGetGeometry(tileMatrixSetId, z, out var geometry))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix set '{tileMatrixSetId}' is not supported.");
        }

        if (geometry.Srid != metadata.Srid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Tile matrix set '{tileMatrixSetId}' (EPSG:{geometry.Srid.ToString(CultureInfo.InvariantCulture)}) does not match the coverage CRS (EPSG:{metadata.Srid.ToString(CultureInfo.InvariantCulture)}). Request a matching gridset.");
        }

        if (geometry.GetTileBounds(x, y, z) is not { } bounds)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile level {z.ToString(CultureInfo.InvariantCulture)} is not part of '{tileMatrixSetId}'.");
        }

        var variable = GetQueryValue(context, "variable");
        var datetimeRaw = GetQueryValue(context, "datetime");
        (DateTimeOffset? Start, DateTimeOffset? End)? datetime = null;
        if (!string.IsNullOrWhiteSpace(datetimeRaw))
        {
            if (!Iso8601TemporalIntervalParser.TryParseRange(datetimeRaw, out var start, out var end, out var dtError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, dtError ?? "Invalid datetime parameter.");
            }
            datetime = (start, end);
        }

        int? verticalIndex = null;
        var elevationRaw = GetQueryValue(context, "elevation");
        if (!string.IsNullOrWhiteSpace(elevationRaw))
        {
            if (!int.TryParse(elevationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "The elevation parameter must be a non-negative grid index.");
            }
            verticalIndex = parsed;
        }

        var tileBounds = new ZarrTileBounds(bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax);
        if (!ZarrTileSlicePlanner.TryPlan(metadata, variable, tileBounds, datetime, verticalIndex, MaxTileSliceBytes, out var slice, out var planError))
        {
            if (planError is { } message && message.Contains("does not intersect", StringComparison.Ordinal))
            {
                return Results.NoContent();
            }
            return StandardErrorHelpers.CreateBadRequest(context, planError ?? "The tile could not be resolved against the coverage.");
        }

        RasterCapacityWork work;
        try
        {
            work = ZarrSubsetWorkEstimator.Estimate(
                slice!.Plan.Array,
                slice.Plan.Request,
                ZarrTileRenderer.DefaultTileSize,
                ZarrTileRenderer.DefaultTileSize);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            return StandardErrorHelpers.CreatePayloadTooLarge(
                context,
                "The planned Zarr tile cannot be bounded safely. Reduce the requested slice or submit it as a durable raster process.");
        }

        // Pure planning is the last step before reader resolution and the first
        // raster-sized allocation. Admission therefore refuses/promotes excessive
        // work before any object range is read. No GDAL/native path exists in web.
        var admission = await capacityAdmission.TryAcquireAsync(
                new RasterCapacityRequest(
                    Operation: "zarr.datacube-tile",
                    TenantPartition: tenantContext.TenantId ?? string.Empty,
                    Work: work,
                    OverflowAction: RasterCapacityOverflowAction.SubmitDurableJob),
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.IsAdmitted)
        {
            ZarrLog.DatacubeTileCapacityDenied(
                logger,
                layerId,
                admission.Dimension.ToString(),
                admission.Requested,
                admission.Limit);
            return CreateCapacityDeniedResult(context, admission);
        }

        await using var capacityLease = admission.Lease!;
        var rangeReader = rangeReaders.FirstOrDefault(reader => reader.Provider == servable.Provider);
        if (rangeReader is null)
        {
            return StandardErrorHelpers.CreateInternalServerError(context, "The storage backend for this coverage is not configured.");
        }

        ZarrSubsetResult result;
        try
        {
            result = await subsetReader.ReadSubsetAsync(
                    rangeReader,
                    servable.Bucket,
                    servable.RootPath,
                    metadata,
                    slice!.Plan.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (InvalidDataException)
        {
            return StandardErrorHelpers.CreateInternalServerError(context, "The Zarr store returned invalid data for this tile.");
        }

        var fillValue = slice.Plan.Array.FillValue as double?;
        var png = ZarrTileRenderer.Render(result, slice, ZarrTileRenderer.DefaultTileSize, colormap: null, fillValue: fillValue);
        ZarrLog.DatacubeTileRendered(logger, layerId, result.Variable, z, x, y, png.Length);
        return Results.Bytes(png, "image/png");
    }

    private static IResult CreateCapacityDeniedResult(
        HttpContext context,
        RasterCapacityAdmissionResult admission)
    {
        var durableGuidance = admission.OverflowAction == RasterCapacityOverflowAction.SubmitDurableJob
            ? " Submit the work as a durable raster geoprocessing job for worker or Batch execution."
            : string.Empty;

        if (admission.DenialKind == RasterCapacityDenialKind.WorkLimitExceeded)
        {
            return StandardErrorHelpers.CreatePayloadTooLarge(
                context,
                $"The synchronous raster request requires {admission.Requested.ToString(CultureInfo.InvariantCulture)} " +
                $"{admission.Dimension} but the limit is {admission.Limit.ToString(CultureInfo.InvariantCulture)}. " +
                "Reduce the bounds or resolution." + durableGuidance);
        }

        return StandardErrorHelpers.CreateTooManyRequests(
            context,
            "Synchronous raster capacity is currently in use." + durableGuidance,
            admission.RetryAfterSeconds);
    }

    private static string? GetQueryValue(HttpContext context, string key)
    {
        if (!context.Request.Query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
