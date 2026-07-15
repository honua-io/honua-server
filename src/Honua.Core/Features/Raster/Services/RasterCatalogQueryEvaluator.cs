// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Services;

/// <summary>
/// Reference in-memory evaluation of the neutral <see cref="RasterCatalogQuery"/> contract. This is
/// the <b>explicitly-declared bounded fallback</b> for raster providers that cannot push the
/// predicate into storage: they materialize the layer's rasters once and hand them here. PostGIS
/// overrides the contract with an indexed, paged SQL pushdown instead; this path exists so a
/// non-pushdown provider still produces byte-for-byte identical envelope-intersects, identity,
/// temporal, paging, count, and aggregate-extent semantics.
/// </summary>
/// <remarks>
/// Evaluation order mirrors the canonical provider query so the fallback and the pushdown agree:
/// temporal "newest batch" (layer-wide) → identity (<c>objectIds</c>) → envelope-intersects → paging.
/// Input order is preserved (no re-sort) so the catalog's natural order flows through unchanged.
/// This fallback has no geometry engine, so an exact
/// <see cref="RasterCatalogSpatialPredicate.Relation"/> (Intersects/Contains/Within/Overlaps) is
/// evaluated with the envelope-intersects box only — a conservative superset of every exact
/// relation's match set. Providers with spatial storage (PostGIS) apply the exact predicate instead.
/// </remarks>
public static class RasterCatalogQueryEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="query"/> against an already-materialized raster set.
    /// </summary>
    /// <param name="rasters">The layer's rasters, in the catalog's natural order.</param>
    /// <param name="query">The validated neutral query.</param>
    /// <param name="transformService">
    /// Optional CRS pipeline used to transform the spatial predicate into a footprint's SRID when
    /// they differ. When <c>null</c> and the SRIDs differ, the footprint is conservatively excluded.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluated page, with <see cref="RasterCatalogPage.PredicatePushedDown"/> false.</returns>
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
        if (query.Timestamp is { } timestamp)
        {
            var target = rasters
                .Select(EffectiveAcquisition)
                .Where(t => t <= timestamp)
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

    /// <summary>
    /// Axis-aligned envelope intersection. Edge contact counts as an intersection, matching the
    /// inclusive semantics of <c>esriSpatialRelEnvelopeIntersects</c> and the PostGIS <c>&amp;&amp;</c>
    /// bounding-box operator used by the pushdown.
    /// </summary>
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
