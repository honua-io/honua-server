// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.GeoServices.ImageServer.Models;
using SpatialConstants = Honua.Core.Features.Shared.Models.SpatialConstants;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerTileInfoBuilder
{
    private const int TileSize = 256;

    private const int Dpi = 96;

    private const double Resolution0 = (2.0 * SpatialConstants.WebMercatorExtent) / TileSize;

    // ArcGIS scale at 96 DPI converts Web Mercator meters with 39.37, while WMTS keeps its own 0.28 mm pixel size.
    private const double Scale0 = Resolution0 * Dpi * 39.37;

    private const int WebMercatorWkid = 102100;

    private const int WebMercatorLatestWkid = 3857;

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
                Scale = Scale0 / factor
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
