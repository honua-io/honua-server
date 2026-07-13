// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// A gridset-neutral tile render window: the tile's bounding box expressed in an explicit
/// spatial reference together with the output pixel dimensions. It lets the raster store render
/// a tile aligned to an arbitrary tile matrix set (gridset) — Web Mercator, WorldCRS84Quad, or an
/// operator-defined gridset — without embedding any protocol-local geodesy in the store.
/// </summary>
/// <remarks>
/// The bounds are computed by the caller from the one canonical gridset definition
/// (<c>GridGeometry.GetTileBounds</c>) so the "where is this tile" decision lives in a single
/// place. The store builds a reference raster aligned to <see cref="MinX"/>/<see cref="MaxY"/> in
/// <see cref="Srid"/> and reprojects the source pixels into it, mirroring the Web Mercator
/// <c>ST_TileEnvelope</c> path so no reprojection logic is duplicated per gridset.
/// </remarks>
public readonly record struct RasterTileWindow
{
    /// <summary>Minimum X (easting / longitude) of the tile bounds, in <see cref="Srid"/>.</summary>
    public required double MinX { get; init; }

    /// <summary>Minimum Y (northing / latitude) of the tile bounds, in <see cref="Srid"/>.</summary>
    public required double MinY { get; init; }

    /// <summary>Maximum X (easting / longitude) of the tile bounds, in <see cref="Srid"/>.</summary>
    public required double MaxX { get; init; }

    /// <summary>Maximum Y (northing / latitude) of the tile bounds, in <see cref="Srid"/>.</summary>
    public required double MaxY { get; init; }

    /// <summary>The numeric spatial reference identifier (SRID) the tile bounds are expressed in.</summary>
    public required int Srid { get; init; }

    /// <summary>Output tile width in pixels (default 256).</summary>
    public int TileWidth { get; init; }

    /// <summary>Output tile height in pixels (default 256).</summary>
    public int TileHeight { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RasterTileWindow"/> struct with the default
    /// 256×256 output tile dimensions.
    /// </summary>
    public RasterTileWindow()
    {
        TileWidth = 256;
        TileHeight = 256;
    }
}
