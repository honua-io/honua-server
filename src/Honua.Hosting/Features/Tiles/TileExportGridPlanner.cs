// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Tiles;

internal readonly record struct TileExportCoordinate(int Level, int Row, int Column);

internal sealed record TileExportGridPlan(
    long TotalTileCount,
    long SelectedTileCount,
    bool ExceededTransferLimit,
    IEnumerable<TileExportCoordinate> Tiles);

internal static class TileExportGridPlanner
{
    private const int PacketSize = 128;

    internal static TileExportGridPlan Create(TileExportJobPlan plan)
    {
        TileExportExecutionSpecBuilder.Validate(plan);
        var ranges = plan.ZoomLevels.Select(level => CreateRange(plan, level)).ToArray();
        long total = 0;
        foreach (var range in ranges)
            total = checked(total + checked((long)(range.MaxRow - range.MinRow + 1) * (range.MaxColumn - range.MinColumn + 1)));
        var selected = Math.Min(total, plan.MaxTiles);
        return new(total, selected, total > selected, Enumerate(ranges, selected));
    }

    private static IEnumerable<TileExportCoordinate> Enumerate(TileRange[] ranges, long limit)
    {
        long yielded = 0;
        foreach (var range in ranges)
        {
            var firstBundleRow = range.MinRow / PacketSize * PacketSize;
            var lastBundleRow = range.MaxRow / PacketSize * PacketSize;
            var firstBundleColumn = range.MinColumn / PacketSize * PacketSize;
            var lastBundleColumn = range.MaxColumn / PacketSize * PacketSize;
            for (var bundleRow = firstBundleRow; bundleRow <= lastBundleRow; bundleRow += PacketSize)
            {
                for (var bundleColumn = firstBundleColumn; bundleColumn <= lastBundleColumn; bundleColumn += PacketSize)
                {
                    var rowEnd = Math.Min(range.MaxRow, bundleRow + PacketSize - 1);
                    var columnEnd = Math.Min(range.MaxColumn, bundleColumn + PacketSize - 1);
                    for (var row = Math.Max(range.MinRow, bundleRow); row <= rowEnd; row++)
                    {
                        for (var column = Math.Max(range.MinColumn, bundleColumn); column <= columnEnd; column++)
                        {
                            if (yielded++ >= limit)
                                yield break;
                            yield return new(range.Level, row, column);
                        }
                    }
                }
            }
        }
    }

    private static TileRange CreateRange(TileExportJobPlan plan, int level)
    {
        var matrixSize = 1 << level;
        return new(
            level,
            LongitudeToMinimumColumn(plan.West, matrixSize),
            LatitudeToMinimumRow(plan.North, matrixSize),
            LongitudeToMaximumColumn(plan.East, matrixSize),
            LatitudeToMaximumRow(plan.South, matrixSize));
    }

    private static int LongitudeToMinimumColumn(double longitude, int matrixSize)
        => Math.Clamp((int)Math.Floor(LongitudeToMatrixCoordinate(longitude, matrixSize)), 0, matrixSize - 1);

    private static int LongitudeToMaximumColumn(double longitude, int matrixSize)
        // The east bbox edge is exclusive. Ceil(value) - 1 selects the tile immediately west
        // of an exact boundary while preserving the final world column for longitude 180.
        => Math.Clamp((int)Math.Ceiling(LongitudeToMatrixCoordinate(longitude, matrixSize)) - 1, 0, matrixSize - 1);

    private static double LongitudeToMatrixCoordinate(double longitude, int matrixSize)
        => (longitude + 180d) / 360d * matrixSize;

    private static int LatitudeToMinimumRow(double latitude, int matrixSize)
        => Math.Clamp((int)Math.Floor(LatitudeToMatrixCoordinate(latitude, matrixSize)), 0, matrixSize - 1);

    private static int LatitudeToMaximumRow(double latitude, int matrixSize)
        // WebMercator row numbers increase southward, so the south bbox edge is the exclusive
        // maximum. Equality on a tile boundary therefore belongs to the row immediately north.
        => Math.Clamp((int)Math.Ceiling(LatitudeToMatrixCoordinate(latitude, matrixSize)) - 1, 0, matrixSize - 1);

    private static double LatitudeToMatrixCoordinate(double latitude, int matrixSize)
    {
        var clamped = Math.Clamp(latitude, -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        var radians = clamped * Math.PI / 180d;
        return (1d - Math.Asinh(Math.Tan(radians)) / Math.PI) / 2d * matrixSize;
    }

    private readonly record struct TileRange(
        int Level,
        int MinColumn,
        int MinRow,
        int MaxColumn,
        int MaxRow);
}
