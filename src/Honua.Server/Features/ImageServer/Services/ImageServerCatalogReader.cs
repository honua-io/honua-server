// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.ImageServer.Services;

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
}

/// <summary>
/// Single catalog item row.
/// </summary>
internal sealed class ImageServerCatalogItem
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

    public DateTimeOffset? AcquisitionDate { get; init; }

    /// <summary>
    /// Footprint expressed as ring coordinates already transformed into the requested SRID.
    /// First ring is the outer boundary; subsequent rings are interior holes.
    /// </summary>
    public double[][][]? FootprintRings { get; init; }
}

/// <summary>
/// Parameters used by <see cref="IImageServerCatalogReader"/>.
/// </summary>
internal sealed class ImageServerCatalogQuery
{
    public string? Where { get; init; }

    public IReadOnlyList<long>? ObjectIds { get; init; }

    public int? OutputSrid { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; }

    public bool ReturnGeometry { get; init; } = true;

    public bool ReturnIdsOnly { get; init; }

    public bool ReturnCountOnly { get; init; }

    public bool ReturnExtentOnly { get; init; }
}

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

    public ImageServerCatalogReader(
        IRasterStore rasterStore,
        IImageServerCatalogFilterEvaluator filterEvaluator)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _filterEvaluator = filterEvaluator ?? throw new ArgumentNullException(nameof(filterEvaluator));
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
                AggregateExtent = null
            };
        }

        // Project each raster row into the catalog item shape Esri expects.
        var projected = new List<ImageServerCatalogItem>(rasters.Length);
        foreach (var raster in rasters)
        {
            projected.Add(ProjectRaster(raster, query));
        }

        // Apply objectIds filter (cheap; usually provided alongside or instead of where).
        if (query.ObjectIds is { Count: > 0 })
        {
            var idSet = new HashSet<long>(query.ObjectIds);
            projected = projected.Where(item => idSet.Contains(item.ObjectId)).ToList();
        }

        // Apply WHERE filter via the shared GeoServices SQL parser.
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            projected = _filterEvaluator.Apply(projected, query.Where).ToList();
        }

        // Compute aggregate extent BEFORE pagination (Esri returnExtentOnly semantics).
        RasterExtent? aggregateExtent = null;
        if (projected.Count > 0)
        {
            double xMin = double.MaxValue, yMin = double.MaxValue;
            double xMax = double.MinValue, yMax = double.MinValue;
            foreach (var raster in rasters)
            {
                if (raster.Extent is not { } extent)
                {
                    continue;
                }

                if (extent.XMin < xMin) xMin = extent.XMin;
                if (extent.YMin < yMin) yMin = extent.YMin;
                if (extent.XMax > xMax) xMax = extent.XMax;
                if (extent.YMax > yMax) yMax = extent.YMax;
            }

            if (xMin <= xMax)
            {
                aggregateExtent = new RasterExtent
                {
                    XMin = xMin,
                    YMin = yMin,
                    XMax = xMax,
                    YMax = yMax,
                    Srid = rasters[0].Extent?.Srid
                };
            }
        }

        var totalMatched = projected.Count;

        // Apply offset/limit + exceededTransferLimit semantics that mirror FeatureServer.
        var offset = Math.Max(0, query.Offset);
        var limit = query.Limit > 0 ? query.Limit : totalMatched;
        var pageEnd = Math.Min(totalMatched, offset + limit);
        var pageItems = offset < pageEnd
            ? projected.GetRange(offset, pageEnd - offset)
            : new List<ImageServerCatalogItem>();

        var exceeded = pageEnd < totalMatched;

        return new ImageServerCatalogPage
        {
            Items = pageItems,
            TotalCount = totalMatched,
            ExceededTransferLimit = exceeded,
            AggregateExtent = aggregateExtent
        };
    }

    private static ImageServerCatalogItem ProjectRaster(RasterInfo raster, ImageServerCatalogQuery query)
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
        if (query.ReturnGeometry && extent is { } footprint)
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
            AcquisitionDate = raster.CreatedAt,
            FootprintRings = rings
        };
    }
}
