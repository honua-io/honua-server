// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Read-only abstraction over a raster catalog. Returns the per-item metadata
/// rows that the Esri Image Server <c>query</c> endpoint exposes as features.
/// </summary>
internal interface IImageServerCatalogReader
{
    /// <summary>
    /// Reads catalog items for a layer with the supplied filter and pagination.
    /// </summary>
    Task<ImageServerCatalogPage> ReadAsync(
        int layerId,
        ImageServerCatalogQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Page of catalog items returned by <see cref="IImageServerCatalogReader"/>.
/// </summary>
internal sealed class ImageServerCatalogPage
{
    public required IReadOnlyList<ImageServerCatalogItem> Items { get; init; }

    public required long TotalCount { get; init; }

    public required bool ExceededTransferLimit { get; init; }

    public required RasterExtent? AggregateExtent { get; init; }

    public int? NativeSrid { get; init; }
}

/// <summary>
/// Single catalog item row.
/// </summary>
internal sealed record ImageServerCatalogItem
{
    public required long ObjectId { get; init; }

    public required string Name { get; init; }

    public double MinPixelSize { get; init; }

    public double MaxPixelSize { get; init; }

    public double LowPixelSize { get; init; }

    public double HighPixelSize { get; init; }

    public double CenterX { get; init; }

    public double CenterY { get; init; }

    public int ZOrder { get; init; }

    public double ShapeLength { get; init; }

    public double ShapeArea { get; init; }

    public int BandCount { get; init; }

    public required string PixelType { get; init; }

    public DateTimeOffset? AcquisitionDate { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Footprint expressed as ring coordinates in the raster's native SRID. The MVP does
    /// not reproject; clients that supply <c>outSR</c> must inspect the geometry's
    /// <c>spatialReference</c> in the response to detect that no reprojection occurred.
    /// First ring is the outer boundary; subsequent rings are interior holes.
    /// </summary>
    public double[][][]? FootprintRings { get; init; }

    /// <summary>
    /// SRID of the footprint coordinates. Equal to the source raster extent SRID.
    /// </summary>
    public int? FootprintSrid { get; init; }
}

/// <summary>
/// Parameters used by <see cref="IImageServerCatalogReader"/>.
/// </summary>
internal sealed class ImageServerCatalogQuery
{
    public string? Where { get; init; }

    public IReadOnlyList<long>? ObjectIds { get; init; }

    public int? OutputSrid { get; init; }

    /// <summary>
    /// Axis-aligned bounding box of the client <c>geometry</c> filter, already
    /// normalised by the parser. <c>null</c> means no spatial filter was supplied.
    /// The catalog reader applies an envelope-intersect test against each raster
    /// footprint; the relationship is interpreted as envelope-intersects because
    /// the catalog reader operates on footprint extents, not exact rings.
    /// </summary>
    public ImageServerCatalogSpatialFilter? SpatialFilter { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; }

    public DateTimeOffset? Time { get; init; }

    /// <summary>
    /// Ordering specification applied after filtering and before pagination.
    /// Each entry is a canonical catalog field name plus a descending flag.
    /// Empty means the catalog's natural order is preserved.
    /// </summary>
    public IReadOnlyList<ImageServerCatalogOrderBy> OrderBy { get; init; } = [];

    /// <summary>
    /// Canonical field names to include in feature attributes and the field
    /// schema. <c>null</c> means all catalog fields are returned. <c>OBJECTID</c>
    /// is always included so clients can correlate features.
    /// </summary>
    public IReadOnlyList<string>? OutFields { get; init; }

    public bool ReturnGeometry { get; init; } = true;

    public bool ReturnIdsOnly { get; init; }

    public bool ReturnCountOnly { get; init; }

    public bool ReturnExtentOnly { get; init; }
}

/// <summary>
/// A single <c>orderByFields</c> term: the canonical catalog field name and
/// whether it sorts descending.
/// </summary>
internal sealed record ImageServerCatalogOrderBy(string Field, bool Descending);

/// <summary>
/// Spatial filter applied against raster footprint extents. The bounding box is
/// expressed in <see cref="Srid"/>; when the footprint SRID differs the reader
/// transforms the filter box into the footprint SRID before the intersect test
/// so the comparison stays in a single coordinate system.
/// </summary>
internal sealed record ImageServerCatalogSpatialFilter(
    double XMin,
    double YMin,
    double XMax,
    double YMax,
    int? Srid);

/// <summary>
/// Default <see cref="IImageServerCatalogReader"/> built on <see cref="IRasterStore"/>.
/// Wraps the existing raster listing surface and projects each row into Esri-compatible
/// catalog metadata. Spatial WHERE/ordering is applied in-memory for the MVP because the
/// raster catalog is normally small (1–N rasters per layer) and adding a SQL emitter for
/// the catalog table is reserved for the dedicated raster ingestion ticket.
/// </summary>
internal sealed class ImageServerCatalogReader : IImageServerCatalogReader
{
    private readonly IRasterStore _rasterStore;
    private readonly IImageServerCatalogFilterEvaluator _filterEvaluator;
    private readonly ICoordinateTransformService? _transformService;

    public ImageServerCatalogReader(
        IRasterStore rasterStore,
        IImageServerCatalogFilterEvaluator filterEvaluator,
        ICoordinateTransformService? transformService = null)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _filterEvaluator = filterEvaluator ?? throw new ArgumentNullException(nameof(filterEvaluator));
        _transformService = transformService;
    }

    public async Task<ImageServerCatalogPage> ReadAsync(
        int layerId,
        ImageServerCatalogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rasters.Length == 0)
        {
            return new ImageServerCatalogPage
            {
                Items = Array.Empty<ImageServerCatalogItem>(),
                TotalCount = 0,
                ExceededTransferLimit = false,
                AggregateExtent = null,
                NativeSrid = null,
            };
        }

        var includeGeometry = query.ReturnGeometry &&
            !query.ReturnIdsOnly &&
            !query.ReturnCountOnly &&
            !query.ReturnExtentOnly;

        // Project each raster row into the catalog item shape Esri expects.
        // We need to retain the source raster reference so the aggregate extent uses the
        // same filtered set as the returned features.
        var projected = new List<(ImageServerCatalogItem Item, RasterInfo Source)>(rasters.Length);
        foreach (var raster in rasters)
        {
            projected.Add((ProjectRaster(raster, includeGeometry), raster));
        }

        if (query.Time.HasValue)
        {
            var selectedAcquisition = projected
                .Select(p => p.Item.AcquisitionDate)
                .Where(t => t.HasValue && t.Value <= query.Time.Value)
                .OrderByDescending(t => t)
                .FirstOrDefault();

            if (selectedAcquisition.HasValue)
            {
                projected = projected
                    .Where(p => p.Item.AcquisitionDate == selectedAcquisition)
                    .ToList();
            }
            else
            {
                projected.Clear();
            }
        }

        // Apply objectIds filter (cheap; usually provided alongside or instead of where).
        if (query.ObjectIds is { Count: > 0 })
        {
            var idSet = new HashSet<long>(query.ObjectIds);
            projected = projected.Where(p => idSet.Contains(p.Item.ObjectId)).ToList();
        }

        // Apply the spatial geometry filter against footprint extents. The filter box
        // is transformed into each footprint's SRID (via the shared CRS pipeline) before
        // the intersect test so the comparison stays in a single coordinate system.
        // Footprints without an extent cannot satisfy a spatial filter and are dropped.
        if (query.SpatialFilter is { } spatialFilter)
        {
            var kept = new List<(ImageServerCatalogItem Item, RasterInfo Source)>(projected.Count);
            foreach (var entry in projected)
            {
                if (entry.Source.Extent is not { } extent)
                {
                    continue;
                }

                var box = await ResolveFilterBoxAsync(spatialFilter, extent.Srid, cancellationToken)
                    .ConfigureAwait(false);
                if (box is { } resolved && EnvelopesIntersect(resolved, extent))
                {
                    kept.Add(entry);
                }
            }

            projected = kept;
        }

        // Apply WHERE filter via the shared GeoServices SQL parser.
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var filteredItems = _filterEvaluator.Apply(projected.Select(p => p.Item), query.Where).ToList();
            var filteredObjectIds = new HashSet<long>(filteredItems.Select(i => i.ObjectId));
            projected = projected.Where(p => filteredObjectIds.Contains(p.Item.ObjectId)).ToList();
        }

        // Apply orderByFields after filtering and before pagination, so paging
        // walks the requested ordering rather than the catalog's natural order.
        if (query.OrderBy is { Count: > 0 })
        {
            projected = ApplyOrdering(projected, query.OrderBy);
        }

        // Compute aggregate extent BEFORE pagination but AFTER filtering, so returnExtentOnly
        // honours objectIds/where filters per Esri spec.
        RasterExtent? aggregateExtent = null;
        if (projected.Count > 0)
        {
            double xMin = double.MaxValue, yMin = double.MaxValue;
            double xMax = double.MinValue, yMax = double.MinValue;
            int? extentSrid = null;
            foreach (var (_, source) in projected)
            {
                if (source.Extent is not { } extent)
                {
                    continue;
                }

                if (extent.XMin < xMin) xMin = extent.XMin;
                if (extent.YMin < yMin) yMin = extent.YMin;
                if (extent.XMax > xMax) xMax = extent.XMax;
                if (extent.YMax > yMax) yMax = extent.YMax;
                extentSrid ??= extent.Srid;
            }

            if (xMin <= xMax)
            {
                aggregateExtent = new RasterExtent
                {
                    XMin = xMin,
                    YMin = yMin,
                    XMax = xMax,
                    YMax = yMax,
                    Srid = extentSrid
                };
            }
        }

        var totalMatched = projected.Count;
        var nativeSrid = aggregateExtent?.Srid
            ?? projected.Select(p => p.Item.FootprintSrid).FirstOrDefault(srid => srid.HasValue);

        // Reproject the aggregate extent into the requested outSR when it differs from the
        // native SRID and the shared CRS pipeline can perform the transform. When the
        // transform is unavailable or fails, the native-SRID extent is retained so the
        // response geometry SR always matches the actual coordinates.
        if (query.OutputSrid is { } targetSrid && aggregateExtent is { } nativeExtent)
        {
            aggregateExtent = await TransformExtentAsync(nativeExtent, targetSrid, cancellationToken)
                .ConfigureAwait(false);
        }

        // ArcGIS returns all matching IDs for returnIdsOnly, without maxRecordCount pagination.
        IReadOnlyList<ImageServerCatalogItem> pageItems;
        bool exceeded;
        if (query.ReturnIdsOnly)
        {
            pageItems = projected.Select(p => p.Item).ToList();
            exceeded = false;
        }
        else
        {
            var offset = Math.Max(0, query.Offset);
            var limit = query.Limit > 0 ? query.Limit : totalMatched;
            var pageEnd = Math.Min(totalMatched, offset + limit);
            var window = offset < pageEnd
                ? projected.GetRange(offset, pageEnd - offset).Select(p => p.Item).ToList()
                : new List<ImageServerCatalogItem>();
            exceeded = pageEnd < totalMatched;

            // Reproject footprint rings into the requested outSR. Footprints are
            // axis-aligned rectangles derived from each raster extent, so transforming the
            // extent corners and rebuilding the rectangle is an exact representation in the
            // target CRS. When reprojection is not possible the native-SRID rings are kept.
            if (query.OutputSrid is { } outSrid && includeGeometry)
            {
                for (var i = 0; i < window.Count; i++)
                {
                    window[i] = await ReprojectFootprintAsync(window[i], outSrid, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            pageItems = window;
        }

        return new ImageServerCatalogPage
        {
            Items = pageItems,
            TotalCount = totalMatched,
            ExceededTransferLimit = exceeded,
            AggregateExtent = aggregateExtent,
            NativeSrid = nativeSrid,
        };
    }

    /// <summary>
    /// Applies the requested <c>orderByFields</c> terms in priority order. The
    /// first term is the primary sort key; subsequent terms break ties. Values
    /// are compared via <see cref="FieldValueComparer"/>, which orders numbers
    /// and dates naturally and falls back to ordinal string comparison.
    /// </summary>
    private static List<(ImageServerCatalogItem Item, RasterInfo Source)> ApplyOrdering(
        List<(ImageServerCatalogItem Item, RasterInfo Source)> projected,
        IReadOnlyList<ImageServerCatalogOrderBy> orderBy)
    {
        IOrderedEnumerable<(ImageServerCatalogItem Item, RasterInfo Source)>? ordered = null;
        foreach (var term in orderBy)
        {
            var localTerm = term;
            object? KeySelector((ImageServerCatalogItem Item, RasterInfo Source) entry)
                => ImageServerCatalogFields.TryResolve(localTerm.Field, entry.Item, out var value) ? value : null;

            if (ordered is null)
            {
                ordered = localTerm.Descending
                    ? projected.OrderByDescending(KeySelector, FieldValueComparer.Instance)
                    : projected.OrderBy(KeySelector, FieldValueComparer.Instance);
            }
            else
            {
                ordered = localTerm.Descending
                    ? ordered.ThenByDescending(KeySelector, FieldValueComparer.Instance)
                    : ordered.ThenBy(KeySelector, FieldValueComparer.Instance);
            }
        }

        return ordered is null ? projected : ordered.ToList();
    }

    /// <summary>
    /// Comparer used for <c>orderByFields</c>. Numbers and dates compare
    /// naturally; everything else falls back to ordinal string comparison.
    /// Nulls sort first.
    /// </summary>
    private sealed class FieldValueComparer : IComparer<object?>
    {
        public static readonly FieldValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            if (TryToDouble(x, out var xd) && TryToDouble(y, out var yd))
            {
                return xd.CompareTo(yd);
            }

            if (x is DateTime xDate && y is DateTime yDate)
            {
                return DateTime.Compare(xDate, yDate);
            }

            return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
        }

        private static bool TryToDouble(object value, out double result)
        {
            switch (value)
            {
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case long l:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }
    }

    /// <summary>
    /// Axis-aligned envelope intersection test between the spatial filter box and a
    /// raster footprint extent. Edge contact counts as an intersection, matching the
    /// inclusive semantics of <c>esriSpatialRelIntersects</c>/<c>EnvelopeIntersects</c>.
    /// </summary>
    private static bool EnvelopesIntersect(ImageServerCatalogSpatialFilter filter, RasterExtent extent)
        => filter.XMin <= extent.XMax &&
           filter.XMax >= extent.XMin &&
           filter.YMin <= extent.YMax &&
           filter.YMax >= extent.YMin;

    /// <summary>
    /// Returns the spatial filter box expressed in <paramref name="footprintSrid"/>. When the
    /// filter and footprint share a SRID (or either is unknown) the filter is used verbatim;
    /// otherwise the shared CRS pipeline transforms the box. A transform failure returns
    /// <see langword="null"/> so the footprint is conservatively excluded rather than matched
    /// against a mismatched coordinate system.
    /// </summary>
    private async ValueTask<ImageServerCatalogSpatialFilter?> ResolveFilterBoxAsync(
        ImageServerCatalogSpatialFilter filter,
        int? footprintSrid,
        CancellationToken cancellationToken)
    {
        if (!filter.Srid.HasValue || !footprintSrid.HasValue || filter.Srid.Value == footprintSrid.Value)
        {
            return filter;
        }

        if (_transformService is null)
        {
            // No CRS pipeline available: comparing across SRIDs would be wrong, so skip
            // the footprint. The handler validates SRIDs up front, so this is a degraded-
            // deployment guard rather than an expected path.
            return null;
        }

        var transformed = await _transformService.TransformExtentAsync(
            filter.XMin, filter.YMin, filter.XMax, filter.YMax,
            filter.Srid.Value, footprintSrid.Value, cancellationToken).ConfigureAwait(false);

        return transformed.HasValue
            ? new ImageServerCatalogSpatialFilter(
                transformed.Value.MinX, transformed.Value.MinY,
                transformed.Value.MaxX, transformed.Value.MaxY, footprintSrid)
            : null;
    }

    /// <summary>
    /// Transforms a raster extent into the requested SRID via the shared CRS pipeline,
    /// returning the original extent when no transform is needed or possible.
    /// </summary>
    private async ValueTask<RasterExtent?> TransformExtentAsync(
        RasterExtent extent,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        if (!extent.Srid.HasValue || extent.Srid.Value == targetSrid || _transformService is null)
        {
            return extent;
        }

        var transformed = await _transformService.TransformExtentAsync(
            extent.XMin, extent.YMin, extent.XMax, extent.YMax,
            extent.Srid.Value, targetSrid, cancellationToken).ConfigureAwait(false);

        return transformed.HasValue
            ? new RasterExtent
            {
                XMin = transformed.Value.MinX,
                YMin = transformed.Value.MinY,
                XMax = transformed.Value.MaxX,
                YMax = transformed.Value.MaxY,
                Srid = targetSrid,
            }
            : extent;
    }

    /// <summary>
    /// Reprojects a catalog item's rectangular footprint into the requested SRID. The
    /// footprint is rebuilt from the transformed extent corners and the item's
    /// <see cref="ImageServerCatalogItem.FootprintSrid"/> is updated so the response
    /// geometry SR matches the emitted ring coordinates.
    /// </summary>
    private async ValueTask<ImageServerCatalogItem> ReprojectFootprintAsync(
        ImageServerCatalogItem item,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        if (item.FootprintRings is not { Length: > 0 } ||
            !item.FootprintSrid.HasValue ||
            item.FootprintSrid.Value == targetSrid ||
            _transformService is null)
        {
            return item;
        }

        var sourceSrid = item.FootprintSrid.Value;
        var (minX, minY, maxX, maxY) = RingsBoundingBox(item.FootprintRings);
        var transformed = await _transformService.TransformExtentAsync(
            minX, minY, maxX, maxY, sourceSrid, targetSrid, cancellationToken).ConfigureAwait(false);

        if (!transformed.HasValue)
        {
            return item;
        }

        var (tx0, ty0, tx1, ty1) = transformed.Value;
        var rings = new[]
        {
            new[]
            {
                new[] { tx0, ty0 },
                new[] { tx0, ty1 },
                new[] { tx1, ty1 },
                new[] { tx1, ty0 },
                new[] { tx0, ty0 },
            },
        };

        return item with { FootprintRings = rings, FootprintSrid = targetSrid };
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) RingsBoundingBox(double[][][] rings)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var ring in rings)
        {
            foreach (var point in ring)
            {
                if (point.Length < 2)
                {
                    continue;
                }

                minX = Math.Min(minX, point[0]);
                minY = Math.Min(minY, point[1]);
                maxX = Math.Max(maxX, point[0]);
                maxY = Math.Max(maxY, point[1]);
            }
        }

        return (minX, minY, maxX, maxY);
    }

    private static ImageServerCatalogItem ProjectRaster(RasterInfo raster, bool includeGeometry)
    {
        var pixelSizeX = 0d;
        var pixelSizeY = 0d;
        if (raster.GeoTransform is { Length: >= 6 })
        {
            pixelSizeX = Math.Abs(raster.GeoTransform[1]);
            pixelSizeY = Math.Abs(raster.GeoTransform[5]);
        }

        var minPixelSize = Math.Min(pixelSizeX, pixelSizeY);
        var maxPixelSize = Math.Max(pixelSizeX, pixelSizeY);

        var extent = raster.Extent;
        var centerX = extent is { } e1 ? (e1.XMin + e1.XMax) / 2.0 : 0;
        var centerY = extent is { } e2 ? (e2.YMin + e2.YMax) / 2.0 : 0;
        var width = extent is { } e3 ? e3.XMax - e3.XMin : 0;
        var height = extent is { } e4 ? e4.YMax - e4.YMin : 0;
        var shapeLength = 2 * (width + height);
        var shapeArea = width * height;

        double[][][]? rings = null;
        if (includeGeometry && extent is { } footprint)
        {
            rings = new[]
            {
                new[]
                {
                    new[] { footprint.XMin, footprint.YMin },
                    new[] { footprint.XMin, footprint.YMax },
                    new[] { footprint.XMax, footprint.YMax },
                    new[] { footprint.XMax, footprint.YMin },
                    new[] { footprint.XMin, footprint.YMin }
                }
            };
        }

        return new ImageServerCatalogItem
        {
            ObjectId = raster.Id,
            Name = raster.Name,
            MinPixelSize = minPixelSize,
            MaxPixelSize = maxPixelSize,
            LowPixelSize = minPixelSize,
            HighPixelSize = maxPixelSize,
            CenterX = centerX,
            CenterY = centerY,
            ZOrder = 0,
            ShapeLength = shapeLength,
            ShapeArea = shapeArea,
            BandCount = raster.BandCount,
            PixelType = raster.PixelType,
            AcquisitionDate = raster.AcquisitionDate ?? raster.CreatedAt,
            CreatedAt = raster.CreatedAt,
            FootprintRings = rings,
            FootprintSrid = extent?.Srid
        };
    }
}
