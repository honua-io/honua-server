// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Services;

// An exact spatial relation is evaluated here as a bounding-box overlap, which is a superset of the true matches; PostGIS applies the exact predicate.
public static class RasterCatalogQueryEvaluator
{
    public static async Task<RasterCatalogPage> EvaluateAsync(
        IReadOnlyList<RasterInfo> rasters,
        RasterCatalogQuery query,
        ICoordinateTransformService? transformService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        IEnumerable<RasterInfo> matched = rasters;

        // 1. Temporal newest-batch, computed layer-wide (before identity/spatial narrowing) so the
        //    snapshot instant selects the same acquisition the canonical provider query would.
        if (query.Timestamp is not null || query.TimeStart is not null)
        {
            var target = rasters
                .Select(EffectiveAcquisition)
                .Where(t => (query.Timestamp is not { } end || t <= end) && (query.TimeStart is not { } start || t >= start))
                .DefaultIfEmpty(default)
                .Max();

            matched = target == default
                ? Enumerable.Empty<RasterInfo>()
                : rasters.Where(r => EffectiveAcquisition(r) == target);
        }

        // 2. Identity filter.
        if (query.ObjectIds is { } objectIds)
        {
            var idSet = new HashSet<long>(objectIds);
            matched = matched.Where(r => idSet.Contains(r.Id));
        }

        // 3. Envelope-intersects against footprint extents.
        var filtered = new List<RasterInfo>();
        if (query.SpatialPredicate is { } predicate)
        {
            foreach (var raster in matched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (raster.Extent is not { } extent)
                {
                    continue;
                }

                var box = await ResolveFilterBoxAsync(predicate, extent.Srid, transformService, cancellationToken)
                    .ConfigureAwait(false);
                if (box is { } resolved && EnvelopesIntersect(resolved, extent))
                {
                    filtered.Add(raster);
                }
            }
        }
        else
        {
            filtered.AddRange(matched);
        }

        var totalCount = filtered.Count;

        RasterExtent? aggregateExtent = null;
        if (query.IncludeAggregateExtent)
        {
            aggregateExtent = ComputeAggregateExtent(filtered);
        }

        List<RasterInfo> page;
        if (query.Limit is { } limit)
        {
            var offset = Math.Max(0, query.Offset);
            var end = Math.Min(totalCount, offset + limit);
            page = offset < end ? filtered.GetRange(offset, end - offset) : [];
        }
        else
        {
            page = filtered;
        }

        return new RasterCatalogPage
        {
            Rasters = page,
            TotalCount = totalCount,
            AggregateExtent = aggregateExtent,
            RowsScanned = totalCount,
            RowsReturned = page.Count,
            ProviderDuration = TimeSpan.Zero,
            PredicatePushedDown = false,
        };
    }

    private static DateTimeOffset EffectiveAcquisition(RasterInfo raster)
        => raster.AcquisitionDate ?? raster.CreatedAt;

    private static bool EnvelopesIntersect(RasterCatalogSpatialPredicate filter, RasterExtent extent)
        => filter.XMin <= extent.XMax &&
           filter.XMax >= extent.XMin &&
           filter.YMin <= extent.YMax &&
           filter.YMax >= extent.YMin;

    private static async ValueTask<RasterCatalogSpatialPredicate?> ResolveFilterBoxAsync(
        RasterCatalogSpatialPredicate filter,
        int? footprintSrid,
        ICoordinateTransformService? transformService,
        CancellationToken cancellationToken)
    {
        if (!filter.Srid.HasValue || !footprintSrid.HasValue || filter.Srid.Value == footprintSrid.Value)
        {
            return filter;
        }

        if (transformService is null)
        {
            return null;
        }

        var transformed = await transformService.TransformExtentAsync(
            filter.XMin, filter.YMin, filter.XMax, filter.YMax,
            filter.Srid.Value, footprintSrid.Value, cancellationToken).ConfigureAwait(false);

        return transformed.HasValue
            ? RasterCatalogSpatialPredicate.Create(
                transformed.Value.MinX, transformed.Value.MinY,
                transformed.Value.MaxX, transformed.Value.MaxY, footprintSrid)
            : null;
    }

    private static RasterExtent? ComputeAggregateExtent(IReadOnlyList<RasterInfo> rasters)
    {
        double xMin = double.MaxValue, yMin = double.MaxValue;
        double xMax = double.MinValue, yMax = double.MinValue;
        int? srid = null;
        var any = false;

        foreach (var raster in rasters)
        {
            if (raster.Extent is not { } extent)
            {
                continue;
            }

            any = true;
            if (extent.XMin < xMin) xMin = extent.XMin;
            if (extent.YMin < yMin) yMin = extent.YMin;
            if (extent.XMax > xMax) xMax = extent.XMax;
            if (extent.YMax > yMax) yMax = extent.YMax;
            srid ??= extent.Srid;
        }

        return any
            ? new RasterExtent { XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax, Srid = srid }
            : null;
    }
}
