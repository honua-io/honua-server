// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TileCachePackage.Domain;

/// <summary>
/// Storage layout of an Esri tile/vector-tile cache package, as published in the
/// documented Esri cache layout. Honua reads these layouts read-only for
/// interoperability (#1269); it does not reverse-engineer proprietary internals
/// beyond the documented cache structure.
/// </summary>
public enum TileCacheStorageFormat
{
    /// <summary>
    /// Compact Cache V2 — <c>L{zz}/R{rrrr}C{cccc}.bundle</c> files with a 64-byte
    /// header and a 16384-entry index. Used by <c>.tpkx</c> and <c>.vtpk</c>.
    /// </summary>
    CompactV2 = 0,

    /// <summary>
    /// Exploded raster cache — <c>_alllayers/L{zz}/R{8hex}/C{8hex}.{png|jpg}</c>
    /// loose tile files. Used by older raster <c>.tpk</c> packages.
    /// </summary>
    Exploded = 1
}

/// <summary>
/// Tile payload kind carried by a cache package.
/// </summary>
public enum TileCacheDataType
{
    /// <summary>Raster tiles (PNG / JPEG).</summary>
    Raster = 0,

    /// <summary>Vector tiles (Mapbox Vector Tile / PBF).</summary>
    Vector = 1
}

/// <summary>
/// Parsed tiling-scheme descriptor for an Esri tile cache package. Derived from the
/// package's <c>root.json</c> (Compact Cache V2 / <c>.tpkx</c>/<c>.vtpk</c>) or
/// <c>conf.xml</c> (exploded raster <c>.tpk</c>).
/// </summary>
public sealed record TileCachePackageDescriptor
{
    /// <summary>Storage layout used by the package.</summary>
    public required TileCacheStorageFormat StorageFormat { get; init; }

    /// <summary>Tile payload kind (raster or vector).</summary>
    public required TileCacheDataType DataType { get; init; }

    /// <summary>
    /// Tile content type emitted for served tiles, such as <c>image/png</c>,
    /// <c>image/jpeg</c>, or <c>application/vnd.mapbox-vector-tile</c>.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Honua tile-matrix-set identifier the package's tiling scheme maps to
    /// (for example <c>WebMercatorQuad</c>). Resolved from the package spatial
    /// reference WKID.
    /// </summary>
    public required string TileMatrixSetIdentifier { get; init; }

    /// <summary>Spatial reference well-known id (EPSG / Esri WKID) of the tiling scheme.</summary>
    public required int Wkid { get; init; }

    /// <summary>Tile width/height in pixels (raster) or the grid tile size (vector).</summary>
    public required int TileSize { get; init; }

    /// <summary>Lowest level-of-detail id present in the package.</summary>
    public required int MinLevel { get; init; }

    /// <summary>Highest level-of-detail id present in the package.</summary>
    public required int MaxLevel { get; init; }

    /// <summary>Optional human-readable title from the package item metadata.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Path inside the package archive where the tile data lives, relative to the
    /// archive root (for example <c>tile</c> for a <c>.tpkx</c> or <c>p12/tile</c>
    /// for a <c>.vtpk</c>). Empty for exploded caches addressed from
    /// <c>_alllayers</c>.
    /// </summary>
    public required string TileBundlesPath { get; init; }
}

/// <summary>
/// A single tile yielded by a <see cref="Abstractions.ITileCachePackageReader"/>.
/// </summary>
public sealed record TileCachePackageTile
{
    /// <summary>Zoom / level-of-detail id.</summary>
    public required int Z { get; init; }

    /// <summary>Tile column.</summary>
    public required int X { get; init; }

    /// <summary>Tile row.</summary>
    public required int Y { get; init; }

    /// <summary>Raw tile bytes (PNG/JPEG, or PBF for vector tiles).</summary>
    public required byte[] Content { get; init; }
}
