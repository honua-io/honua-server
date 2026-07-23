// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// A small, protocol-neutral description of a tile matrix set (gridset): the spatial
/// reference, tile origin, tile pixel dimensions, and the per-level matrix dimensions and
/// scale denominators. Both the OGC API Tiles and classic WMTS adapters consume this so the
/// "which gridsets exist and what is their geometry" decision lives in one place
/// (<see cref="ITileMatrixSetRegistry"/>) instead of being hardcoded per protocol.
/// </summary>
/// <remarks>
/// <para>This is intentionally a small value type defined in <c>Honua.Core</c> so it can be
/// shared across the protocol assemblies and the rendering pipeline without introducing a
/// cross-assembly project-reference cycle (the rendering layer's <c>TileGridKind</c> enum
/// cannot represent operator-defined gridsets).</para>
/// <para>The two built-in gridsets (WebMercatorQuad, WorldCRS84Quad) are seeded from the same
/// constants and formulas the protocol adapters already use, so their advertised metadata and
/// capabilities XML remain byte-identical (the OGC CITE guard).</para>
/// </remarks>
public sealed record GridGeometry
{
    /// <summary>The tile matrix set identifier (e.g. <c>WebMercatorQuad</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The numeric spatial reference identifier (SRID) of the gridset CRS.</summary>
    public required int Srid { get; init; }

    /// <summary>
    /// <see langword="true"/> when the gridset CRS is geographic (degrees, e.g. CRS84) rather
    /// than projected (meters, e.g. Web Mercator). Drives the tile-envelope filter SRID and the
    /// axis interpretation of <see cref="TopLeftX"/>/<see cref="TopLeftY"/>.
    /// </summary>
    public required bool IsGeographic { get; init; }

    /// <summary>
    /// The X (easting / longitude) coordinate of the grid's top-left corner (point of origin),
    /// expressed in the gridset CRS.
    /// </summary>
    public required double TopLeftX { get; init; }

    /// <summary>
    /// The Y (northing / latitude) coordinate of the grid's top-left corner (point of origin),
    /// expressed in the gridset CRS.
    /// </summary>
    public required double TopLeftY { get; init; }

    /// <summary>Tile width in pixels (default 256).</summary>
    public required int TileWidth { get; init; }

    /// <summary>Tile height in pixels (default 256).</summary>
    public required int TileHeight { get; init; }

    /// <summary>The per-level tile matrices, ordered by ascending level identifier.</summary>
    public required ImmutableArray<GridLevel> Levels { get; init; }

    /// <summary>
    /// Returns the tile matrix for the given level identifier, or <see langword="null"/> when no
    /// level with that identifier exists in the gridset.
    /// </summary>
    /// <param name="level">The tile matrix (zoom) level identifier.</param>
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

    /// <summary>
    /// Computes the bounding box of a tile in the gridset CRS from the grid origin and the
    /// requested level's matrix dimensions. Returns <see langword="null"/> when the level is
    /// not part of the gridset.
    /// </summary>
    /// <param name="x">Tile column index.</param>
    /// <param name="y">Tile row index.</param>
    /// <param name="level">Tile matrix (zoom) level identifier.</param>
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

/// <summary>
/// A single tile matrix (zoom level) within a <see cref="GridGeometry"/>: its scale
/// denominator, cell size, and matrix dimensions (columns × rows).
/// </summary>
/// <param name="Level">The tile matrix (zoom) level identifier.</param>
/// <param name="ScaleDenominator">The OGC scale denominator at this level.</param>
/// <param name="CellSize">The cell size (CRS units per pixel) at this level.</param>
/// <param name="MatrixWidth">The number of tile columns at this level.</param>
/// <param name="MatrixHeight">The number of tile rows at this level.</param>
public readonly record struct GridLevel(
    int Level,
    double ScaleDenominator,
    double CellSize,
    long MatrixWidth,
    long MatrixHeight);
