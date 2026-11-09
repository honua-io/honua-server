// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Tiles;

public sealed record GridGeometry
{
    public required string Id { get; init; }

    public required int Srid { get; init; }

    public required bool IsGeographic { get; init; }

    public required double TopLeftX { get; init; }

    public required double TopLeftY { get; init; }

    public required int TileWidth { get; init; }

    public required int TileHeight { get; init; }

    public required ImmutableArray<GridLevel> Levels { get; init; }

    public GridLevel? FindLevel(int level)
    {
        // GridLevel is a value-type record struct, so LINQ's FirstOrDefault would return
        // default(GridLevel) (Level == 0) rather than null when nothing matches; that would
        // silently misreport "found level 0" for a not-found gridset level. The explicit loop
        // preserves the intended nullable "no such level" contract.
        foreach (var candidate in (Levels).Where(candidate => candidate.Level == level))
        {
            return candidate;
        }

        return null;
    }

    public TileBounds? GetTileBounds(int x, int y, int level)
    {
        if (FindLevel(level) is not { } gridLevel)
        {
            return null;
        }

        // The full grid spans (MatrixWidth * tile-extent) horizontally from the top-left
        // origin. The per-tile extent in CRS units is derived from the cell size and tile
        // pixel dimensions: cellSize is CRS-units per pixel, so a tile is cellSize * tileSize.
        var tileSpanX = gridLevel.CellSize * TileWidth;
        var tileSpanY = gridLevel.CellSize * TileHeight;

        var xMin = TopLeftX + (x * tileSpanX);
        var yMax = TopLeftY - (y * tileSpanY);
        var xMax = xMin + tileSpanX;
        var yMin = yMax - tileSpanY;

        return new TileBounds(xMin, yMin, xMax, yMax);
    }
}

public readonly record struct GridLevel(
    int Level,
    double ScaleDenominator,
    double CellSize,
    long MatrixWidth,
    long MatrixHeight);
