// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.VectorTileServer.Models;
using SpatialConstants = Honua.Core.Features.Shared.Models.SpatialConstants;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

/// <summary>
/// Builds the static WebMercatorQuad <c>tileInfo</c> descriptor advertised by VectorTileServer
/// service metadata. The level-of-detail table is fixed for the Web Mercator (EPSG:3857)
/// vector tiling scheme. This mirrors <c>ImageServerTileInfoBuilder</c> but for the vector
/// tiling scheme: 512-pixel logical tiles serving the <c>pbf</c> (Mapbox Vector Tile) format,
/// where the level-0 scale denominator follows the OGC WebMercatorQuad standard
/// (<c>559082264.0287178</c>) and halves every level. This is pure metadata assembly — the
/// metadata foundation does not host an Esri-format vector tile cache (#1777).
/// </summary>
internal static class VectorTileServerTileInfoBuilder
{
    /// <summary>Logical tile dimension, in pixels, for the vector WebMercatorQuad scheme.</summary>
    private const int TileSize = 512;

    /// <summary>Esri advertises a logical 96 DPI for WebMercator caches.</summary>
    private const int Dpi = 96;

    /// <summary>
    /// Ground resolution (meters per pixel) at zoom level 0 for a 512-pixel
    /// WebMercatorQuad tile: <c>(2 * WebMercatorExtent) / 512</c>.
    /// </summary>
    private const double Resolution0 = (2.0 * SpatialConstants.WebMercatorExtent) / TileSize;

    /// <summary>
    /// Scale denominator at zoom level 0 for the WebMercatorQuad tile matrix set. This is the
    /// OGC-standardized value Esri vector tile caches advertise; each subsequent level halves it.
    /// </summary>
    private const double ScaleDenominator0 = 559082264.0287178;

    /// <summary>WebMercator spatial reference well-known id used by Esri tile caches.</summary>
    private const int WebMercatorWkid = 102100;

    /// <summary>The EPSG-aligned latest well-known id for WebMercator (3857).</summary>
    private const int WebMercatorLatestWkid = 3857;

    /// <summary>Maximum zoom level the WebMercatorQuad scheme supports.</summary>
    internal const int MaxLevel = 24;

    /// <summary>
    /// Builds the fixed WebMercatorQuad <see cref="VectorTileInfo"/> descriptor covering
    /// levels <c>0..maxLevel</c> inclusive.
    /// </summary>
    /// <param name="maxLevel">
    /// Highest zoom level to include in the LOD array (clamped to the Web Mercator range
    /// 0–24 that the WebMercatorQuad scheme accepts).
    /// </param>
    /// <returns>A populated <see cref="VectorTileInfo"/> descriptor.</returns>
    public static VectorTileInfo Build(int maxLevel)
    {
        var clampedMax = Math.Clamp(maxLevel, 0, MaxLevel);
        var lods = new VectorTileLevelOfDetail[clampedMax + 1];
        for (var level = 0; level <= clampedMax; level++)
        {
            var factor = (double)(1L << level);
            lods[level] = new VectorTileLevelOfDetail
            {
                Level = level,
                Resolution = Resolution0 / factor,
                Scale = ScaleDenominator0 / factor
            };
        }

        return new VectorTileInfo
        {
            Rows = TileSize,
            Cols = TileSize,
            Dpi = Dpi,
            Format = "pbf",
            // WebMercatorQuad tiles originate at the top-left of the world extent.
            Origin = new VectorTileOrigin
            {
                X = -SpatialConstants.WebMercatorExtent,
                Y = SpatialConstants.WebMercatorExtent
            },
            SpatialReference = new VectorTileSpatialReference
            {
                Wkid = WebMercatorWkid,
                LatestWkid = WebMercatorLatestWkid
            },
            Lods = lods
        };
    }
}
