// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Bound configuration for operator-defined custom tile matrix sets (gridsets). Operators add
/// entries under the <c>TileMatrixSets</c> configuration section; each entry is merged with the
/// two built-in gridsets (WebMercatorQuad, WorldCRS84Quad) by <see cref="ITileMatrixSetRegistry"/>
/// and advertised through the OGC API Tiles and classic WMTS metadata surfaces.
/// </summary>
public sealed class TileMatrixSetDefinitionOptions
{
    /// <summary>
    /// The configuration section name that binds to these options.
    /// </summary>
    public const string SectionName = "TileMatrixSets";

    /// <summary>
    /// The operator-defined custom tile matrix sets. The two built-in gridsets are always
    /// available and must not be redefined here (the validator rejects collisions).
    /// </summary>
    public IList<CustomTileMatrixSet> Custom { get; set; } = new List<CustomTileMatrixSet>();
}

/// <summary>
/// One operator-defined custom tile matrix set (gridset). Mirrors the OGC tile matrix set model:
/// an identifier, CRS, origin, tile pixel size, and an ordered list of zoom levels.
/// </summary>
public sealed class CustomTileMatrixSet
{
    /// <summary>
    /// The tile matrix set identifier (used in URLs and capabilities). Must be unique and must
    /// not collide with the reserved built-in identifiers.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The CRS URI advertised for this gridset (e.g.
    /// <c>http://www.opengis.net/def/crs/EPSG/0/3857</c>).
    /// </summary>
    public string Crs { get; set; } = string.Empty;

    /// <summary>
    /// Optional tile matrix set URI. When omitted, adapters fall back to the identifier.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// Optional human-readable title. When omitted, adapters fall back to the identifier.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// The numeric spatial reference identifier (SRID) for the gridset CRS. Used by the render
    /// pipeline to build the tile envelope filter and reproject from the storage CRS.
    /// </summary>
    public int Srid { get; set; }

    /// <summary>
    /// The grid origin (top-left corner) as <c>[x, y]</c> in the gridset CRS. For geographic
    /// CRSes the values are longitude/latitude in CRS storage order; adapters apply the
    /// protocol-specific axis order when emitting metadata.
    /// </summary>
    public double[] TopLeftCorner { get; set; } = [];

    /// <summary>
    /// Tile width in pixels.
    /// </summary>
    public int TileWidth { get; set; } = 256;

    /// <summary>
    /// Tile height in pixels.
    /// </summary>
    public int TileHeight { get; set; } = 256;

    /// <summary>
    /// The ordered list of zoom levels (tile matrices) for this gridset.
    /// </summary>
    public IList<TileMatrixLevel> Levels { get; set; } = new List<TileMatrixLevel>();
}

/// <summary>
/// One zoom level (tile matrix) within a <see cref="CustomTileMatrixSet"/>.
/// </summary>
public sealed class TileMatrixLevel
{
    /// <summary>
    /// The tile matrix (zoom) level identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The OGC scale denominator at this level.
    /// </summary>
    public double ScaleDenominator { get; set; }

    /// <summary>
    /// The cell size (CRS units per pixel) at this level.
    /// </summary>
    public double CellSize { get; set; }

    /// <summary>
    /// The number of tile columns at this level.
    /// </summary>
    public long MatrixWidth { get; set; }

    /// <summary>
    /// The number of tile rows at this level.
    /// </summary>
    public long MatrixHeight { get; set; }
}
