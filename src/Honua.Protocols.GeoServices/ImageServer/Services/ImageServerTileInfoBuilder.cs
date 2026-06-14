// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.ImageServer.Models;
using SpatialConstants = Honua.Core.Features.Shared.Models.SpatialConstants;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Builds the static WebMercatorQuad (Esri "GoogleMapsCompatible") <c>tileInfo</c>
/// descriptor advertised by ImageServer service metadata when tiled consumption is
/// enabled. The level-of-detail table is fixed for the Web Mercator (EPSG:3857)
/// 256-pixel tiling scheme that the live <c>/tile/{level}/{row}/{col}</c> route
/// serves, so this is pure metadata assembly — not a new serving capability (#1648).
/// </summary>
internal static class ImageServerTileInfoBuilder
{
    /// <summary>Standard tile dimension, in pixels, for the WebMercatorQuad scheme.</summary>
    private const int TileSize = 256;

    /// <summary>Esri advertises a logical 96 DPI for WebMercator basemap caches.</summary>
    private const int Dpi = 96;

    /// <summary>
    /// Ground resolution (meters per pixel) at zoom level 0 for a 256-pixel
    /// WebMercatorQuad tile: <c>(2 * WebMercatorExtent) / 256</c>.
    /// </summary>
    private const double Resolution0 = (2.0 * SpatialConstants.WebMercatorExtent) / TileSize;

    /// <summary>
    /// Scale denominator at zoom level 0 for the WebMercatorQuad /
    /// GoogleMapsCompatible tile matrix set. This is the OGC-standardized value that
    /// Esri clients and the WMTS capabilities document (<c>WmtsRequestHandlers</c>)
    /// both use; each subsequent level halves it.
    /// </summary>
    private const double ScaleDenominator0 = 559082264.0287178;

    /// <summary>WebMercator spatial reference well-known id used by Esri tile caches.</summary>
    private const int WebMercatorWkid = 102100;

    /// <summary>The EPSG-aligned latest well-known id for WebMercator (3857).</summary>
    private const int WebMercatorLatestWkid = 3857;

    /// <summary>
    /// Builds the fixed WebMercatorQuad <see cref="TileInfo"/> descriptor covering
    /// levels <c>0..maxLevel</c> inclusive.
    /// </summary>
    /// <param name="maxLevel">
    /// Highest zoom level to include in the LOD array (clamped to the Web Mercator
    /// range 0–28 that the tile route accepts).
    /// </param>
    /// <returns>A populated <see cref="TileInfo"/> descriptor.</returns>
    public static TileInfo Build(int maxLevel)
    {
        var clampedMax = Math.Clamp(maxLevel, 0, 28);
        var lods = new LevelOfDetail[clampedMax + 1];
        for (var level = 0; level <= clampedMax; level++)
        {
            var factor = (double)(1L << level);
            lods[level] = new LevelOfDetail
            {
                Level = level,
                Resolution = Resolution0 / factor,
                Scale = ScaleDenominator0 / factor
            };
        }

        return new TileInfo
        {
            Rows = TileSize,
            Cols = TileSize,
            Dpi = Dpi,
            Format = "PNG",
            CompressionQuality = null,
            // WebMercatorQuad tiles originate at the top-left of the world extent.
            Origin = new Point
            {
                X = -SpatialConstants.WebMercatorExtent,
                Y = SpatialConstants.WebMercatorExtent
            },
            SpatialReference = new SpatialReference
            {
                Wkid = WebMercatorWkid,
                LatestWkid = WebMercatorLatestWkid
            },
            Lods = lods
        };
    }
}
