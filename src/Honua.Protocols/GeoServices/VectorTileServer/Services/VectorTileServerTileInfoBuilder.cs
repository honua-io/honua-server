// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.GeoServices.VectorTileServer.Models;
using SpatialConstants = Honua.Core.Features.Shared.Models.SpatialConstants;

namespace Honua.Protocols.GeoServices.VectorTileServer.Services;

internal static class VectorTileServerTileInfoBuilder
{
    private const int TileSize = 512;

    private const int Dpi = 96;

    private const double Resolution0 = (2.0 * SpatialConstants.WebMercatorExtent) / TileSize;

    private const int WebMercatorWkid = 102100;

    private const int WebMercatorLatestWkid = 3857;

    internal const int MaxLevel = 24;

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
                Scale = (Resolution0 / factor) * Dpi * 39.37
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
