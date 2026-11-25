// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;

namespace Honua.Infrastructure.SharedCaching;

internal static class CdnTileRegionTags
{
    internal const int MaxInvalidations = 50;
    private const int GridZoom = 4;
    private const double MercatorLatitudeLimit = 85.0511287798066d;

    internal static string[] ForNativeTile(PathString path, TileOptions options)
    {
        var pathSpan = (path.Value ?? string.Empty).AsSpan();
        Span<Range> ranges = stackalloc Range[7];
        var count = pathSpan.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries);
        if (count is not (3 or 5 or 6) || !pathSpan[ranges[0]].Equals("tiles", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var segments = ranges[..count];
        if (TryMatchSpecialNativeTileTag(pathSpan, segments) is { } specialTags)
        {
            return specialTags;
        }

        if (ParseStandardMvtPath(pathSpan, segments) is not { } parsed)
        {
            return [];
        }

        var extent = options.TileExtent > 0 ? options.TileExtent : 4096;
        return ForTile(parsed.LayerId, parsed.Tile, Math.Max(0, options.TileBuffer) / (double)extent);
    }

    private static string[]? TryMatchSpecialNativeTileTag(ReadOnlySpan<char> pathSpan, ReadOnlySpan<Range> ranges)
    {
        if (ranges.Length == 3
            && pathSpan[ranges[2]].Equals("tile.json", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(pathSpan[ranges[1]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var metadataLayerId))
        {
            return [Metadata(metadataLayerId)];
        }

        // H3 hexagons cross tile edges by up to a cell, so an H3 tile is purged with every regional purge
        // of its layer through the buffered tag rather than by its own cells.
        if (ranges.Length == 6
            && pathSpan[ranges[2]].Equals("h3", StringComparison.OrdinalIgnoreCase)
            && pathSpan[ranges[5]].EndsWith(".mvt", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(pathSpan[ranges[1]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h3LayerId))
        {
            return [Buffered(h3LayerId)];
        }

        return null;
    }

    private static (int LayerId, TileAddress Tile)? ParseStandardMvtPath(ReadOnlySpan<char> pathSpan, ReadOnlySpan<Range> ranges)
    {
        if (ranges.Length != 5)
        {
            return null;
        }

        var rowSpan = pathSpan[ranges[4]];
        if (!rowSpan.EndsWith(".mvt", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(pathSpan[ranges[1]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId)
            || !int.TryParse(pathSpan[ranges[2]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoom)
            || !int.TryParse(pathSpan[ranges[3]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column)
            || !int.TryParse(rowSpan[..^4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row)
            || zoom is < 0 or > 30 || column < 0 || row < 0 || column >= (1 << zoom) || row >= (1 << zoom))
        {
            return null;
        }

        return (layerId, new TileAddress(zoom, column, row));
    }

    internal static string[] ForTile(int layerId, TileAddress tile, double bufferFraction = 0d)
    {
        if (tile.Zoom is < 0 or > 30 || tile.Column < 0 || tile.Row < 0
            || tile.Column >= (1 << tile.Zoom) || tile.Row >= (1 << tile.Zoom))
        {
            throw new ArgumentOutOfRangeException(nameof(tile));
        }

        if (!double.IsFinite(bufferFraction) || bufferFraction < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferFraction));
        }

        var tags = new List<string>(GridZoom);
        for (var zoom = 1; zoom <= Math.Min(tile.Zoom, GridZoom); zoom++)
        {
            var cells = BufferedCells(tile, zoom, bufferFraction);
            if (tags.Count + cells.Count > MaxInvalidations - 5)
            {
                return [Buffered(layerId)];
            }

            tags.AddRange(cells.Select(cell => Cell(layerId, cell)));
        }

        if (tile.Zoom < GridZoom)
        {
            tags.Add(Overview(layerId, tile.Zoom));
        }

        return [.. tags];
    }

    private static List<TileAddress> BufferedCells(TileAddress tile, int zoom, double bufferFraction)
    {
        var size = 1 << zoom;
        var scale = (double)(1 << (tile.Zoom - zoom));
        var west = MinimumCell((tile.Column - bufferFraction) / scale, size);
        var east = MaximumCell((tile.Column + 1d + bufferFraction) / scale, size);
        var north = MinimumCell((tile.Row - bufferFraction) / scale, size);
        var south = MaximumCell((tile.Row + 1d + bufferFraction) / scale, size);
        return Cells(new(zoom, west, north), new(zoom, east, south));
    }

    private static List<TileAddress> Cells(TileAddress northwest, TileAddress southeast)
    {
        var cells = new List<TileAddress>();
        for (var column = northwest.Column; column <= southeast.Column; column++)
        {
            for (var row = northwest.Row; row <= southeast.Row; row++)
            {
                cells.Add(new(northwest.Zoom, column, row));
            }
        }

        return cells;
    }

    internal static string[] Invalidations(int layerId, BoundingBox extent)
    {
        ValidateExtent(extent);
        for (var zoom = GridZoom; zoom >= 1; zoom--)
        {
            var size = 1 << zoom;
            var west = MinimumCell(LongitudeCoordinate(extent.MinX, size), size);
            var east = MaximumCell(LongitudeCoordinate(extent.MaxX, size), size);
            var north = MinimumCell(LatitudeCoordinate(extent.MaxY, size), size);
            var south = MaximumCell(LatitudeCoordinate(extent.MinY, size), size);
            if ((east - west + 1) * (south - north + 1) + zoom + 1 > MaxInvalidations)
            {
                continue;
            }

            var tags = Cells(new(zoom, west, north), new(zoom, east, south))
                .Select(cell => "#" + Cell(layerId, cell)).ToList();

            tags.AddRange(Enumerable.Range(0, zoom).Select(level => "#" + Overview(layerId, level)));
            tags.Add("#" + Buffered(layerId));
            return [.. tags];
        }

        throw new InvalidOperationException("The regional purge budget cannot cover the extent.");
    }

    private static void ValidateExtent(BoundingBox extent)
    {
        if (extent.SpatialReferenceId is not null and not 4326
            || !double.IsFinite(extent.MinX) || !double.IsFinite(extent.MinY)
            || !double.IsFinite(extent.MaxX) || !double.IsFinite(extent.MaxY)
            || extent.MinX < -180d || extent.MaxX > 180d || extent.MinY < -90d || extent.MaxY > 90d
            || extent.MinX > extent.MaxX || extent.MinY > extent.MaxY)
        {
            throw new ArgumentException("Regional purge extents must be ordered geographic longitude and latitude bounds.", nameof(extent));
        }
    }

    internal static string Metadata(int layerId)
        => string.Create(CultureInfo.InvariantCulture, $"honua-tile-metadata:{layerId}");

    private static string Cell(int layerId, TileAddress tile)
        => string.Create(CultureInfo.InvariantCulture, $"honua-region:tiles/{layerId}/{tile.Zoom}/{tile.Column}/{tile.Row}");

    private static string Overview(int layerId, int zoom)
        => string.Create(CultureInfo.InvariantCulture, $"honua-overview:tiles/{layerId}/{zoom}");

    private static string Buffered(int layerId)
        => string.Create(CultureInfo.InvariantCulture, $"honua-buffered:tiles/{layerId}");

    // An edit on an exact cell boundary can affect rendered pixels on either side.
    private static int MinimumCell(double coordinate, int size)
        => (int)Math.Clamp(Math.Floor(Math.BitDecrement(coordinate)), 0d, size - 1d);

    private static int MaximumCell(double coordinate, int size)
        => (int)Math.Clamp(Math.Floor(coordinate), 0d, size - 1d);

    private static double LongitudeCoordinate(double longitude, int size) => (longitude + 180d) / 360d * size;

    private static double LatitudeCoordinate(double latitude, int size)
    {
        var radians = Math.Clamp(latitude, -MercatorLatitudeLimit, MercatorLatitudeLimit) * Math.PI / 180d;
        return (1d - Math.Asinh(Math.Tan(radians)) / Math.PI) / 2d * size;
    }

    internal readonly record struct TileAddress(int Zoom, int Column, int Row);
}
