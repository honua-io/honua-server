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

    /// <summary>
    /// Rows the provider matched with the pushed predicate (the read bound). Surfaced as telemetry.
    /// </summary>
    public long RowsScanned { get; init; }

    /// <summary>Rows materialized into this page. Surfaced as telemetry.</summary>
    public long RowsReturned { get; init; }

    /// <summary>Provider-side query duration. Surfaced as telemetry; never carries SQL.</summary>
    public TimeSpan ProviderDuration { get; init; }
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

    public int Width { get; init; }

    public int Height { get; init; }

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

    /// <summary>
    /// Optional sensor/camera/orientation/RPC metadata hydrated from the
    /// <c>raster_sensor_metadata</c> companion table. <c>null</c> for plain rasters with no
    /// modeled sensor metadata (the common case). Used by orientation-ranked find (#1880),
    /// height mensuration (#1879), and image-coordinate-system project warps (#1881).
    /// </summary>
    public RasterSensorMetadata? SensorMetadata { get; init; }
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
/// Delegates the spatial (envelope-intersects), identity, and temporal predicates — and, on the
/// common browse path, paging, count, and aggregate extent — to the provider via
/// <see cref="IRasterStore.QueryCatalogAsync"/> so a large imagery catalog is filtered and paged in
/// storage <b>before materialization</b> rather than fully read and filtered in memory (#2666).
/// The Esri-only concerns the neutral contract deliberately omits — <c>where</c> evaluation and
/// computed-field <c>orderByFields</c> ordering — are applied here, in memory, over the
/// spatially-bounded set the provider returns (a bounded fallback for those query shapes).
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

        var includeGeometry = query.ReturnGeometry &&
            !query.ReturnIdsOnly &&
            !query.ReturnCountOnly &&
            !query.ReturnExtentOnly;

        // The neutral contract pushes the spatial/identity/temporal predicates into the provider.
        // Esri `where` evaluation and computed-field `orderByFields` ordering are not part of the
        // neutral contract, so when either is present we finish those steps in memory over the
        // (already spatially bounded) provider result — a declared bounded fallback for those shapes.
        var hasWhere = !string.IsNullOrWhiteSpace(query.Where);
        var hasCustomOrder = query.OrderBy is { Count: > 0 };
        var pushPaging = !hasWhere && !hasCustomOrder && !query.ReturnIdsOnly;

        // returnCountOnly / returnExtentOnly do not consume the row page, so on the pushdown path we
        // ask the provider for a single row plus the aggregate metadata rather than a full page.
        var metadataOnly = pushPaging && (query.ReturnCountOnly || query.ReturnExtentOnly);
        int? pushLimit = metadataOnly ? 1 : (pushPaging ? query.Limit : null);

        RasterCatalogSpatialPredicate? predicate = null;
        if (query.SpatialFilter is { } spatialFilter)
        {
            if (!RasterCatalogSpatialPredicate.TryCreate(
                    spatialFilter.XMin, spatialFilter.YMin, spatialFilter.XMax, spatialFilter.YMax,
                    spatialFilter.Srid, out var built, out var predicateError))
            {
                throw new ImageServerCatalogFilterException(predicateError);
            }

            predicate = built;
        }

        var neutralQuery = new RasterCatalogQuery
        {
            SpatialPredicate = predicate,
            ObjectIds = query.ObjectIds,
            Timestamp = query.Time,
            Offset = pushPaging ? Math.Max(0, query.Offset) : 0,
            Limit = pushLimit,
            IncludeTotalCount = pushPaging,
            IncludeAggregateExtent = pushPaging,
        };

        var page = await _rasterStore.QueryCatalogAsync(layerId, neutralQuery, cancellationToken)
            .ConfigureAwait(false);

        if (page.Rasters.Count == 0 && !pushPaging)
        {
            return EmptyPage(page);
        }

        // Hydrate sensor/orientation metadata only for the rasters actually returned (bounded).
        var sensorMetadata = await _rasterStore
            .GetSensorMetadataAsync(page.Rasters.Select(static r => r.Id).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var projected = page.Rasters
            .Select(raster =>
            {
                var sensor = sensorMetadata.TryGetValue(raster.Id, out var meta) ? meta : raster.SensorMetadata;
                return (Item: ProjectRaster(raster, includeGeometry, sensor), Source: raster);
            })
            .ToList();

        return pushPaging
            ? await BuildPushdownPageAsync(query, page, projected, includeGeometry, cancellationToken)
                .ConfigureAwait(false)
            : await BuildInMemoryPageAsync(query, page, projected, includeGeometry, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Fast path: the provider already applied the spatial/identity/temporal predicates, natural
    /// ordering, paging, total count, and aggregate extent, so we trust its page and only project,
    /// reproject to <c>outSR</c>, and carry the telemetry counters forward.
    /// </summary>
    private async Task<ImageServerCatalogPage> BuildPushdownPageAsync(
        ImageServerCatalogQuery query,
        RasterCatalogPage page,
        List<(ImageServerCatalogItem Item, RasterInfo Source)> projected,
        bool includeGeometry,
        CancellationToken cancellationToken)
    {
        var nativeSrid = page.AggregateExtent?.Srid
            ?? projected.Select(p => p.Item.FootprintSrid).FirstOrDefault(srid => srid.HasValue);

        var aggregateExtent = page.AggregateExtent;
        if (query.OutputSrid is { } targetSrid && aggregateExtent is { } nativeExtent)
        {
            aggregateExtent = await TransformExtentAsync(nativeExtent, targetSrid, cancellationToken)
                .ConfigureAwait(false);
        }

        var window = projected.Select(p => p.Item).ToList();
        if (query.OutputSrid is { } outSrid && includeGeometry)
        {
            for (var i = 0; i < window.Count; i++)
            {
                window[i] = await ReprojectFootprintAsync(window[i], outSrid, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var exceeded = Math.Max(0, query.Offset) + window.Count < page.TotalCount;

        return new ImageServerCatalogPage
        {
            Items = window,
            TotalCount = page.TotalCount,
            ExceededTransferLimit = exceeded,
            AggregateExtent = aggregateExtent,
            NativeSrid = nativeSrid,
            RowsScanned = page.RowsScanned,
            RowsReturned = window.Count,
            ProviderDuration = page.ProviderDuration,
        };
    }

    /// <summary>
    /// Bounded fallback: the provider returned every spatially-filtered row (no paging) because the
    /// query carries an Esri <c>where</c> clause or a computed-field ordering the neutral contract
    /// cannot express. We finish <c>where</c>/<c>orderByFields</c>, aggregate extent, and paging in
    /// memory over that bounded set, preserving the pre-pushdown semantics exactly.
    /// </summary>
    private async Task<ImageServerCatalogPage> BuildInMemoryPageAsync(
        ImageServerCatalogQuery query,
        RasterCatalogPage page,
        List<(ImageServerCatalogItem Item, RasterInfo Source)> projected,
        bool includeGeometry,
        CancellationToken cancellationToken)
    {
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

        if (query.OutputSrid is { } targetSrid && aggregateExtent is { } nativeExtent)
        {
            aggregateExtent = await TransformExtentAsync(nativeExtent, targetSrid, cancellationToken)
                .ConfigureAwait(false);
        }

        // ArcGIS returns all matching IDs for returnIdsOnly, without maxRecordCount pagination.
        List<ImageServerCatalogItem> pageItems;
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
            RowsScanned = page.RowsScanned,
            RowsReturned = pageItems.Count,
            ProviderDuration = page.ProviderDuration,
        };
    }

    private static ImageServerCatalogPage EmptyPage(RasterCatalogPage page) => new()
    {
        Items = Array.Empty<ImageServerCatalogItem>(),
        TotalCount = 0,
        ExceededTransferLimit = false,
        AggregateExtent = null,
        NativeSrid = null,
        RowsScanned = page.RowsScanned,
        RowsReturned = 0,
        ProviderDuration = page.ProviderDuration,
    };

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
            object? KeySelector((ImageServerCatalogItem Item, RasterInfo Source) entry)
                => ImageServerCatalogFields.TryResolve(term.Field, entry.Item, out var value) ? value : null;

            ordered = ordered is null
                ? (term.Descending
                    ? projected.OrderByDescending(KeySelector, FieldValueComparer.Instance)
                    : projected.OrderBy(KeySelector, FieldValueComparer.Instance))
                : (term.Descending
                    ? ordered.ThenByDescending(KeySelector, FieldValueComparer.Instance)
                    : ordered.ThenBy(KeySelector, FieldValueComparer.Instance));
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

    private static ImageServerCatalogItem ProjectRaster(
        RasterInfo raster,
        bool includeGeometry,
        RasterSensorMetadata? sensorMetadata)
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
            Width = raster.Width,
            Height = raster.Height,
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
            FootprintSrid = extent?.Srid,
            SensorMetadata = sensorMetadata,
        };
    }
}
